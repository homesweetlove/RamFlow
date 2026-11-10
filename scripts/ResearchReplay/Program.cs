using System.Text.Json;
using System.Text.Json.Serialization;
using RamFlow.Core;

if (args.Length != 4 || args[0] != "--input" || args[2] != "--output") {
    Console.Error.WriteLine("Usage: ResearchReplay --input snapshot-inputs.jsonl --output NEW-directory"); return 2;
}
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
string output = Path.GetFullPath(args[3]);
if (Directory.Exists(output)) { Console.Error.WriteLine("Output directory already exists; use a new run directory."); return 2; }
Directory.CreateDirectory(output);
using var writer = new StreamWriter(new FileStream(Path.Combine(output, "replay.jsonl"), FileMode.CreateNew));
int count = 0;
IResourcePolicy? carp = null;
string? configuration = null;
var policies = new IResourcePolicy[] { new V1ResourcePolicy(), new FixedResourcePolicy() };
try {
    foreach (string line in File.ReadLines(args[1])) {
        if (line.Length > 1048576) throw new IOException("Input record exceeds 1 MiB.");
        var input = JsonSerializer.Deserialize<PolicyInput>(line, options) ?? throw new JsonException("Null input.");
        Engine.Validate(input.Settings);
        string hash = FileResearchAuditSink.Hash(input.Settings.Carp);
        if (configuration != hash) { carp = new CarpPolicy(input.Settings.Carp); configuration = hash; }
        foreach (var policy in policies.Append(carp!)) {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var decision = policy.Decide(input);
            writer.WriteLine(JsonSerializer.Serialize(new { Record = count, policy.Name, policy.Version, Input = input,
                Decision = decision, DecisionWallMs = clock.Elapsed.TotalMilliseconds, Applied = false }, options));
        }
        count++;
    }
    writer.Flush();
    File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new {
        SchemaVersion = 1, Status = "completed", Records = count, InputSha256 = FileResearchAuditSink.FileHash(args[1]),
        CoreSha256 = FileResearchAuditSink.FileHash(typeof(Engine).Assembly.Location),
        Interpretation = "Policy replay only. No Windows action or performance experiment was executed." }, options));
    Console.WriteLine($"Replayed {count} snapshots across V1/fixed/CARP; no process changed."); return 0;
} catch (Exception error) {
    writer.Flush();
    File.WriteAllText(Path.Combine(output, "failure.json"), JsonSerializer.Serialize(new { Status = "failed", Records = count, Error = error.ToString() }));
    Console.Error.WriteLine(error.Message); return 1;
}
