using System.Text.Json;
using System.Text.Json.Serialization;

namespace RamFlow.Core;

// Opt-in research adapter. An instrumented workload writes a complete JSON
// array atomically. Files are not discovered from user profiles or the network.
// Policy validation still checks creation identity, age, model, and contention.
public sealed class JsonPolicyObservationSource(string path) : IPolicyObservationSource
{
    readonly string path = Path.GetFullPath(path);
    readonly object gate = new();
    DateTimeOffset? cachedTime;
    Dictionary<(int, long), CarpEvidence> cached = new();
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() } };
    public CarpEvidence? Read(ProcessSample process, Snapshot snapshot) {
        lock (gate) {
            if (cachedTime != snapshot.Time) {
                cached.Clear(); cachedTime = null;
                if (!File.Exists(path)) return null;
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (file.Length > 1048576) throw new IOException("Observation file exceeds 1 MiB.");
                var evidence = JsonSerializer.Deserialize<CarpEvidence[]>(file, Json) ?? [];
                if (evidence.Length > 1024) throw new IOException("Observation file exceeds 1024 targets.");
                // Duplicate identities are an ambiguous observation, not last-wins.
                var next = evidence.ToDictionary(x => (x.Pid, x.Identity));
                cached = next; cachedTime = snapshot.Time;
            }
            return cached.GetValueOrDefault((process.Pid, process.Identity));
        }
    }
}
