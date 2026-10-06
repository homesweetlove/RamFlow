using System.Security.Principal;
using System.Text.Json;
using RamFlow.Core;

string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RamFlowNative");
int dataAt = Array.IndexOf(args, "--data-root"); if (dataAt >= 0 && dataAt + 1 < args.Length) dataRoot = Path.GetFullPath(args[dataAt + 1]);
int scopeAt = Array.IndexOf(args, "--ipc-namespace");
if (scopeAt >= 0) {
    if (scopeAt + 1 >= args.Length || !Guid.TryParseExact(args[scopeAt + 1], "N", out var scope) || scope == Guid.Empty) { Environment.ExitCode = 2; return; }
    EnginePipe.ConfigureVerificationNamespace(scope);
}
if (args.Contains("--service")) { ServiceHost.Run(); return; }
if (args.Contains("--ipc-state")) { Console.WriteLine(JsonSerializer.Serialize(EnginePipe.Request<State>(new("state")))); return; }
if (args.Contains("--ipc-stop")) { EnginePipe.Request<bool>(new("stop")); return; }
if (args.Contains("--restore-pagefile")) {
    int at = Array.IndexOf(args, "--restore-pagefile"); await Pagefile.RestoreAsync(args[at + 1], false); return;
}
if (args.Contains("--serve")) {
    using var identity = WindowsIdentity.GetCurrent();
    using var mutex = new Mutex(true, EnginePipe.EngineMutexName, out bool created);
    if (!created) return;
    using var backgroundIo = new BackgroundIo();
    using var lifetime = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
    using var engine = new Engine(new WindowsResourceProvider(), dataRoot, allowRemote: false);
    engine.Start();
    try { await EnginePipe.ServeAsync(engine, lifetime); } finally { engine.Stop(); }
    return;
}
if (args.Contains("--restore-and-stop")) {
    try { EnginePipe.Request<bool>(new("stop"), 3000); }
    catch (InvalidOperationException error) { Console.Error.WriteLine(error.Message); Environment.ExitCode = 1; }
    catch {
        using var identity = WindowsIdentity.GetCurrent();
        if (Mutex.TryOpenExisting(EnginePipe.EngineMutexName, out var active)) { active.Dispose(); Console.Error.WriteLine("엔진이 동작 중입니다. 복원 완료 후 다시 제거하세요."); Environment.ExitCode = 1; return; }
        using var engine = new Engine(new WindowsResourceProvider(), dataRoot, allowRemote: false); engine.Stop();
        if (engine.GetState().PendingRestores > 0) { Console.Error.WriteLine("복원 대기 중이라 제거를 중단합니다."); Environment.ExitCode = 1; }
    }
    return;
}
if (args.Contains("--simulate")) {
    var provider = new MockResourceProvider(); provider.Current = provider.Current with { Available = 400 * 1048576UL, Commit = 15UL * 1073741824 };
    using var engine = new Engine(provider, persist: false);
    for (int i = 0; i < 40; i++) { provider.Current = provider.Current with { Time = provider.Current.Time.AddMinutes(1) }; engine.Tick(); }
    Console.WriteLine(JsonSerializer.Serialize(engine.GetState())); return;
}
using (var engine = new Engine(new WindowsResourceProvider(), dataRoot, persist: false, allowRemote: false)) {
    engine.Tick(); Console.WriteLine(JsonSerializer.Serialize(engine.GetState()));
}
