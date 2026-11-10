using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RamFlow.Core;

public sealed record ResearchAuditEvent(string Kind, DateTimeOffset Time, string Policy, string Version,
    string SettingsSha256, int? Pid, long? Identity, string Result, object? Detail);
public interface IResearchAuditSink { void Write(ResearchAuditEvent entry); }

// Caller owns this sink. A separate directory is required for every run.
// JSONL is authoritative; CSV is an index into the same sequence of records.
public sealed class FileResearchAuditSink : IResearchAuditSink, IDisposable
{
    static readonly JsonSerializerOptions EventJson = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    readonly StreamWriter json, csv;
    readonly object gate = new();
    long sequence;
    public FileResearchAuditSink(string directory, string sourceCommit, string? sourceTreeSha256 = null) {
        if (string.IsNullOrWhiteSpace(sourceCommit)) throw new ArgumentException("Record a source commit or explicitly use unknown.");
        Directory.CreateDirectory(directory);
        // CreateNew claims the run before opening other files; never overwrite.
        using (var claim = new FileStream(Path.Combine(directory, "manifest.json"), FileMode.CreateNew, FileAccess.Write)) {
            var assembly = typeof(Engine).Assembly;
            var manifest = new { SchemaVersion = 1, RunId = Guid.NewGuid(), CreatedUtc = DateTimeOffset.UtcNow,
                SourceCommit = sourceCommit, SourceTreeSha256 = sourceTreeSha256,
                BinaryPath = assembly.Location, BinarySha256 = FileHash(assembly.Location),
                AssemblyVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                Runtime = Environment.Version.ToString(), Os = Environment.OSVersion.ToString(), ProcessorCount = Environment.ProcessorCount,
                SystemPagesInputMeaning = "system-wide pages read from disk; not per-process hard faults",
                PerProcessHardFaults = "unavailable unless independently collected with attributable ETW events",
                ForegroundLatency = "missing unless explicitly supplied by an instrumented workload",
                Estimates = "candidate terms are normalized model estimates; not observed performance results" };
            JsonSerializer.Serialize(claim, manifest, new JsonSerializerOptions { WriteIndented = true });
            claim.Flush(true);
        }
        json = new(new FileStream(Path.Combine(directory, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        try { csv = new(new FileStream(Path.Combine(directory, "events.csv"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true }; }
        catch { json.Dispose(); throw; }
        csv.WriteLine("sequence,time_utc,kind,policy,version,settings_sha256,pid,creation_filetime,result");
    }
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))).ToLowerInvariant();
    public static string? FileHash(string path) { if (!File.Exists(path)) return null; using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    static string Cell(object? value) => "\"" + (Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + "\"";
    public void Write(ResearchAuditEvent entry) {
        lock (gate) {
            long n = ++sequence;
            json.WriteLine(JsonSerializer.Serialize(new { Sequence = n, entry.Kind, entry.Time, entry.Policy, entry.Version,
                entry.SettingsSha256, entry.Pid, entry.Identity, entry.Result, entry.Detail }, EventJson));
            csv.WriteLine(string.Join(",", new object?[] { n, entry.Time.ToUniversalTime().ToString("O"), entry.Kind, entry.Policy,
                entry.Version, entry.SettingsSha256, entry.Pid, entry.Identity, entry.Result }.Select(Cell)));
        }
    }
    public void Dispose() { lock (gate) { json.Dispose(); csv.Dispose(); } }
}
