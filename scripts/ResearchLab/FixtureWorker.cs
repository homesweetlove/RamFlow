using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ResearchLab;

sealed record WorkerReply(int Id, string Kind, long Units, double Seconds, double? LatencyMs = null, string? Checksum = null,
    double? CompletionSeconds = null, DateTimeOffset? ObservedUtc = null);

static class FixtureWorker
{
    public static async Task<int> Run(string[] args) {
        string role = Options.Get(args, "--role", "foreground"), mode = Options.Get(args, "--mode", "cpu");
        int mib = Options.Int(args, "--mib", 16, 4, 256), threads = Options.Int(args, "--threads", 1, 1, 32);
        long target = Options.Int(args, "--units", 0, 0, 10000000);
        byte[] data = new byte[mib * 1048576];
        new Random(Options.Int(args, "--seed", 1, 0, int.MaxValue)).NextBytes(data);
        var clock = Stopwatch.StartNew(); using var cancellation = new CancellationTokenSource();
        object output = new(); long units = 0; double? completion = null;
        void Write(WorkerReply value) { lock (output) Console.WriteLine(JsonSerializer.Serialize(value with { ObservedUtc = DateTimeOffset.UtcNow })); }
        string Work() {
            if (mode is "ram" or "mixed") {
                long sum = 0; for (int i = 0; i < data.Length; i += 4096) sum += data[i];
                if (mode == "ram") return sum.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return Convert.ToHexString(SHA256.HashData(data));
        }
        var tasks = new List<Task>();
        if (role == "background") {
            for (int t = 0; t < threads; t++) tasks.Add(Task.Factory.StartNew(() => {
                while (!cancellation.IsCancellationRequested && (target == 0 || Interlocked.Read(ref units) < target)) { Work(); Interlocked.Increment(ref units); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
            _ = Task.Run(async () => {
                while (!cancellation.IsCancellationRequested) {
                    if (target > 0 && tasks.All(x => x.IsCompleted) && completion is null) completion = clock.Elapsed.TotalSeconds;
                    Write(new(0, "progress", Interlocked.Read(ref units), clock.Elapsed.TotalSeconds, CompletionSeconds: completion));
                    try { await Task.Delay(100, cancellation.Token); } catch (OperationCanceledException) { break; }
                }
            });
        }
        Write(new(0, "ready", 0, clock.Elapsed.TotalSeconds));
        while (await Console.In.ReadLineAsync() is string line) {
            using var command = JsonDocument.Parse(line); int id = command.RootElement.GetProperty("id").GetInt32();
            string verb = command.RootElement.GetProperty("verb").GetString()!;
            if (verb == "stop") break;
            if (verb == "progress") { Write(new(id, "progress", Interlocked.Read(ref units), clock.Elapsed.TotalSeconds, CompletionSeconds: completion)); continue; }
            if (verb is not ("work" or "probe")) throw new ArgumentException("Unknown fixture command.");
            var request = Stopwatch.StartNew(); string hash = Work(); double elapsed = request.Elapsed.TotalMilliseconds;
            Write(new(id, verb, Interlocked.Read(ref units), clock.Elapsed.TotalSeconds, elapsed, hash, completion));
        }
        cancellation.Cancel(); await Task.WhenAll(tasks); return 0;
    }
}

sealed class FixtureChild : IAsyncDisposable
{
    readonly Process process; readonly Task reader;
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<WorkerReply>> pending = new();
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly StreamWriter raw;
    int nextId;
    public WorkerReply? Progress { get; private set; }
    public Process Process => process;
    public long Identity { get; }
    public FixtureChild(string executable, string role, string mode, int mib, int threads, int seed, int targetUnits, string rawPath) {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "worker", "--role", role, "--mode", mode, "--mib", mib.ToString(), "--threads", threads.ToString(), "--seed", seed.ToString(), "--units", targetUnits.ToString() }) start.ArgumentList.Add(arg);
        raw = new(new FileStream(rawPath, FileMode.CreateNew)) { AutoFlush = true };
        process = Process.Start(start) ?? throw new IOException("Fixture child failed to launch."); Identity = process.StartTime.ToFileTimeUtc();
        reader = Task.Run(async () => {
            try {
                while (await process.StandardOutput.ReadLineAsync() is string line) {
                    await raw.WriteLineAsync(line);
                    var reply = JsonSerializer.Deserialize<WorkerReply>(line)!;
                    if (reply.Kind == "ready") ready.TrySetResult();
                    if (reply.Kind == "progress") Progress = reply;
                    if (reply.Id != 0 && pending.TryRemove(reply.Id, out var waiter)) waiter.TrySetResult(reply);
                }
                throw new IOException("Fixture output closed: " + await process.StandardError.ReadToEndAsync());
            } catch (Exception e) { ready.TrySetException(e); foreach (var waiter in pending.Values) waiter.TrySetException(e); }
        });
    }
    public Task Ready() => ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
    public async Task<WorkerReply> Request(string verb) {
        int id = Interlocked.Increment(ref nextId); var reply = new TaskCompletionSource<WorkerReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = reply;
        try { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, verb })); await process.StandardInput.FlushAsync();
            return await reply.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { pending.TryRemove(id, out _); }
    }
    public async ValueTask DisposeAsync() {
        try { if (!process.HasExited) { await process.StandardInput.WriteLineAsync("{\"id\":0,\"verb\":\"stop\"}"); await process.StandardInput.FlushAsync(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
        catch (Exception) { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        await reader; raw.Dispose(); process.Dispose();
    }
}
