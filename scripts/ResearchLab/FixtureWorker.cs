using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ResearchLab;

sealed record WorkerReply(int Id, string Kind, long Units, double Seconds, double? LatencyMs = null, string? Checksum = null,
    double? CompletionSeconds = null, DateTimeOffset? ObservedUtc = null) { public string? Phase {get;init;} public object? Telemetry {get;init;} }

static class FixtureWorker
{
    public static async Task<int> Run(string[] args) {
        string role = Options.Get(args, "--role", "foreground"), mode = Options.Get(args, "--mode", "cpu");
        int mib = Options.Int(args, "--mib", 16, 4, 256), threads = Options.Int(args, "--threads", 1, 1, 32);
        long target = Options.Int(args, "--units", 0, 0, 10000000);
        byte[] data = new byte[mib * 1048576];
        int seed=Options.Int(args,"--seed",1,0,int.MaxValue);
        new Random(seed).NextBytes(data);
        var clock = Stopwatch.StartNew(); using var cancellation = new CancellationTokenSource();
        object output = new(); long units = 0; double? completion = null;
        string? outputPhase=null,lastChecksum=null;
        void Write(WorkerReply value) { lock (output) Console.WriteLine(JsonSerializer.Serialize(value with { ObservedUtc = DateTimeOffset.UtcNow,Phase=Volatile.Read(ref outputPhase) })); }
        string Work() {
            if(mode=="cpu-arithmetic")return CpuWork.Complete(seed);
            if (mode is "ram" or "mixed") {
                long sum = 0; for (int i = 0; i < data.Length; i += 4096) sum += data[i];
                if (mode == "ram") return sum.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return Convert.ToHexString(SHA256.HashData(data));
        }
        var tasks = new List<Task>();
        int activeThreads=Options.Int(args,"--active-threads",threads,0,threads);
        bool diagnostics=args.Contains("--diagnostics");
        uint[] workerIds=new uint[threads],lastProcessors=Enumerable.Repeat(uint.MaxValue,threads).ToArray();long[] completed=new long[threads];
        if (role == "background") {
            for (int t = 0; t < threads; t++) {
                int workerIndex=t;
                tasks.Add(Task.Factory.StartNew(() => {
                    if(diagnostics)Volatile.Write(ref workerIds[workerIndex],CpuInspection.GetCurrentThreadId());
                    while (!cancellation.IsCancellationRequested && (target == 0 || Interlocked.Read(ref units) < target)) {
                        if(workerIndex>=Volatile.Read(ref activeThreads)) { Thread.Sleep(10);continue; }
                        string checksum=Work();Volatile.Write(ref lastChecksum,checksum);Interlocked.Increment(ref units);
                        if(diagnostics && (Interlocked.Increment(ref completed[workerIndex])&63)==1)Volatile.Write(ref lastProcessors[workerIndex],CpuInspection.Processor());
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
            }
            _ = Task.Run(async () => {
                while (!cancellation.IsCancellationRequested) {
                    if (target > 0 && tasks.All(x => x.IsCompleted) && completion is null) completion = clock.Elapsed.TotalSeconds;
                    Write(new(0, "progress", Interlocked.Read(ref units), clock.Elapsed.TotalSeconds, Checksum:Volatile.Read(ref lastChecksum),CompletionSeconds: completion));
                    try { await Task.Delay(100, cancellation.Token); } catch (OperationCanceledException) { break; }
                }
            });
        }
        Write(new(0, "ready", 0, clock.Elapsed.TotalSeconds));
        while (await Console.In.ReadLineAsync() is string line) {
            using var command = JsonDocument.Parse(line); int id = command.RootElement.GetProperty("id").GetInt32();
            string verb = command.RootElement.GetProperty("verb").GetString()!;
            if (verb == "stop") break;
            if (verb == "inspect") {
                if(!diagnostics)throw new InvalidOperationException("Read-only inspection requires --diagnostics.");
                Write(new(id,"inspect",Interlocked.Read(ref units),clock.Elapsed.TotalSeconds,Checksum:Volatile.Read(ref lastChecksum)) {
                    Telemetry=CpuInspection.Capture(Volatile.Read(ref activeThreads),workerIds,completed,lastProcessors,tasks.ToArray()) });continue;
            }
            if (verb == "load") {
                int count=command.RootElement.GetProperty("activeThreads").GetInt32();
                if(role!="background" || count<0 || count>threads)throw new ArgumentException("Invalid owned-worker load.");
                if(command.RootElement.TryGetProperty("phase",out var tag) && tag.ValueKind==JsonValueKind.String) {
                    string selected=tag.GetString()!;if(selected is not ("Normal" or "Contention" or "Recovery"))throw new ArgumentException("Unknown semantic phase.");
                    Volatile.Write(ref outputPhase,selected);
                }
                Volatile.Write(ref activeThreads,count);
                Write(new(id,"load",Interlocked.Read(ref units),clock.Elapsed.TotalSeconds,Checksum:Volatile.Read(ref lastChecksum)));continue;
            }
            if (verb == "progress") { Write(new(id, "progress", Interlocked.Read(ref units), clock.Elapsed.TotalSeconds, Checksum:Volatile.Read(ref lastChecksum),CompletionSeconds: completion)); continue; }
            if (verb is not ("work" or "probe")) throw new ArgumentException("Unknown fixture command.");
            var request = Stopwatch.StartNew(); string hash = Work(); double elapsed = request.Elapsed.TotalMilliseconds;
            Write(new(id, verb, Interlocked.Read(ref units), clock.Elapsed.TotalSeconds, elapsed, hash, completion));
        }
        cancellation.Cancel(); await Task.WhenAll(tasks); return 0;
    }
}

sealed class FixtureChild : IAsyncDisposable
{
    readonly Process process; readonly Task reader,stderr;
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<WorkerReply>> pending = new();
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly StreamWriter raw;
    readonly EvidenceJournal? evidence;
    readonly string role;
    volatile bool stopping;
    Exception? readerError;
    int nextId;
    public WorkerReply? Progress { get; private set; }
    public Process Process => process;
    public long Identity { get; }
    public FixtureChild(string executable, string role, string mode, int mib, int threads, int seed, int targetUnits, string rawPath,EvidenceJournal? evidence=null,int? initialThreads=null) {
        this.evidence=evidence;this.role=role;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "worker", "--role", role, "--mode", mode, "--mib", mib.ToString(), "--threads", threads.ToString(), "--seed", seed.ToString(), "--units", targetUnits.ToString() }) start.ArgumentList.Add(arg);
        if(initialThreads is {} initial) { start.ArgumentList.Add("--active-threads");start.ArgumentList.Add(initial.ToString()); }
        raw = new(new FileStream(rawPath, FileMode.CreateNew)) { AutoFlush = true };
        Process? created=null;StreamWriter? errorWriter=null;
        try {
            errorWriter=new(new FileStream(rawPath+".stderr.jsonl",FileMode.CreateNew,FileAccess.Write,FileShare.Read)) { AutoFlush=true };
            process=created=Process.Start(start) ?? throw new IOException("Fixture child failed to launch.");Identity=process.StartTime.ToFileTimeUtc();
            evidence?.Write("process-owned",new { role,pid=process.Id,creation_filetime=Identity,executable,sha256=RamFlow.Core.FileResearchAuditSink.FileHash(executable),arguments=start.ArgumentList.ToArray() },true);
        } catch {
            if(created is not null) { try { if(!created.HasExited) { created.Kill();created.WaitForExit(5000); } } finally {created.Dispose();} }
            errorWriter?.Dispose();raw.Dispose();throw;
        }
        stderr=Task.Run(async()=> { using(errorWriter) {
            while(await process.StandardError.ReadLineAsync() is string line) await errorWriter.WriteLineAsync(JsonSerializer.Serialize(new { time_utc=DateTimeOffset.UtcNow,text=line }));
        } });
        reader = Task.Run(async () => {
            try {
                while (await process.StandardOutput.ReadLineAsync() is string line) {
                    await raw.WriteLineAsync(line);
                    var reply = JsonSerializer.Deserialize<WorkerReply>(line)!;
                    if (reply.Kind == "ready") ready.TrySetResult();
                    if (reply.Kind == "progress") Progress = reply;
                    if (reply.Id != 0 && pending.TryRemove(reply.Id, out var waiter)) waiter.TrySetResult(reply);
                }
                if(!stopping) throw new IOException("Fixture output closed unexpectedly; inspect the persisted stderr stream.");
            } catch (Exception e) { readerError=e;ready.TrySetException(e); foreach (var waiter in pending.Values) waiter.TrySetException(e);
                evidence?.TryWrite("worker-stream-error",new { role,pid=process.Id,creation_filetime=Identity,error=e.ToString() }); }
        });
    }
    public Task Ready() => ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
    public async Task<WorkerReply> Request(string verb,int? activeThreads=null,string? phase=null) {
        if(readerError is not null) throw new IOException("Fixture reader failed",readerError);
        int id = Interlocked.Increment(ref nextId); var reply = new TaskCompletionSource<WorkerReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = reply;
        var clock=Stopwatch.StartNew();
        try {
            evidence?.Write("request-begin",new { role,pid=process.Id,creation_filetime=Identity,id,verb,phase });
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, verb,activeThreads,phase })); await process.StandardInput.FlushAsync();
            var value=await reply.Task.WaitAsync(TimeSpan.FromSeconds(15));double wallMs=clock.Elapsed.TotalMilliseconds;
            evidence?.Write("request-end",new { role,pid=process.Id,creation_filetime=Identity,id,verb,wall_ms=wallMs,reply=value });
            return value;
        } catch(Exception e) { evidence?.TryWrite("request-error",new { role,pid=process.Id,creation_filetime=Identity,id,verb,error=e.ToString(),wall_ms=clock.Elapsed.TotalMilliseconds });throw; }
        finally { pending.TryRemove(id, out _); }
    }
    public async ValueTask DisposeAsync() {
        bool exitedBeforeStop=process.HasExited;
        stopping=true;
        try { if (!process.HasExited) { await process.StandardInput.WriteLineAsync("{\"id\":0,\"verb\":\"stop\"}"); await process.StandardInput.FlushAsync(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
        catch (Exception) { if (!process.HasExited) { evidence?.TryWrite("owned-child-forced-stop",new { role,pid=process.Id,creation_filetime=Identity });process.Kill(); await process.WaitForExitAsync(); } }
        try {
            await reader;await stderr;
            evidence?.TryWrite("process-exited",new { role,pid=process.Id,creation_filetime=Identity,exit_code=process.ExitCode });
            if(readerError is not null) throw new IOException("Fixture reader failed; preserve raw output",readerError);
            if(exitedBeforeStop || process.ExitCode!=0) throw new IOException("Fixture exited unexpectedly or with a nonzero exit code.");
        } finally { raw.Dispose();process.Dispose(); }
    }
}
