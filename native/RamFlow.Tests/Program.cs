using RamFlow.Core;
using System.IO.Compression;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

int passed = 0;
if (args.Contains("--ipc")) EnginePipe.ConfigureVerificationNamespace(Guid.NewGuid());
void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
(MockResourceProvider P, Engine E) Busy(bool dry = false)
{
    var p = new MockResourceProvider();
    p.Current = p.Current with { Available = 400 * 1048576UL, Commit = 15UL * 1073741824 };
    var e = new Engine(p, persist: false); e.ApplySettings(new() { DryRun = dry, CpuSets = true });
    for (int i = 0; i < 36; i++) { e.Tick(); p.Current = p.Current with { Time = p.Current.Time.AddMinutes(1) }; }
    return (p, e);
}
{
    var (p, e) = Busy(true); using (e) { Check(p.Writes == 0 && p.Trims == 0, "Dry Run changed resources"); }
}
{
    var (p, e) = Busy(); using (e) {
        Check(p.Values[987650].CpuPriority == 0x4000, "Idle CPU not lowered");
        Check(p.Values[987650].MemoryPriority == 3, "Idle memory not lowered");
        Check(p.Values[987650].CpuSets.SequenceEqual(new uint[] { 1 }), "Efficiency CPU Sets wrong");
        Check(!p.Values.ContainsKey(987651), "Foreground changed");
        e.ApplySettings(new() { Paused = true, DryRun = false });
        Check(p.Values[987650].CpuPriority == 0x20 && p.Values[987650].MemoryPriority == 5 && p.Values[987650].PowerState == 0, "Pause restoration failed");
        Check(p.Values[987650].CpuSets.Length == 0, "CPU Sets not restored");
    }
}
{
    var (p, e) = Busy(); using (e) {
        p.FailWrite = true; e.ApplySettings(new() { DryRun = false, CpuManagement = false });
        Check(e.GetState().PendingRestores > 0, "Failed restore not tracked");
        p.FailWrite = false; e.Tick(); Check(p.Values[987650].CpuPriority == 0x20, "Disabled CPU restore not retried");
        p.IdentityUnknown = true; e.ApplySettings(new() { Paused = true });
        Check(e.GetState().PendingRestores > 0, "Identity lookup failure lost snapshot");
        p.IdentityUnknown = false; e.Tick(); Check(e.GetState().PendingRestores == 0, "Restoration did not recover");
    }
}
{
    var (p, e) = Busy(); using (e) {
        p.Items.RemoveAll(x => x.Pid == 987650); p.Values.Remove(987650);
        p.Items.Add(new(987650, 99, "replacement.exe", "D:\\Apps\\replacement.exe", 1, 500 * 1048576UL, 400 * 1048576UL, 0, 0, true));
        for (int i = 0; i < 36; i++) { p.Current = p.Current with { Time = p.Current.Time.AddMinutes(1) }; e.Tick(); }
        Check(p.Values[987650].CpuPriority == 0x4000, "Replacement not managed"); e.Stop();
        Check(p.Values[987650].CpuPriority == 0x20, "Replacement originals stale");
    }
}
{
    var p = new MockResourceProvider(); using var e = new Engine(p, persist: false); for (int i = 0; i < 100; i++) { p.Current = p.Current with { Time = p.Current.Time.AddMinutes(1) }; e.Tick(); }
    Check(p.Writes == 0 && p.Trims == 0, "Healthy memory intervention");
    var settings = e.Settings; try { e.ApplySettings(settings with { Mode = "invalid" }); } catch (ArgumentException) { }
    Check(e.Settings.Mode == settings.Mode, "Invalid settings partially applied");
}
{
    try { Pagefile.Validate(new(false, [new("C:\\pagefile.sys';bad", 128, 512)])); throw new Exception("Injected path accepted"); } catch (ArgumentException) { passed++; }
    var result = Pagefile.Recommend(new() { RamTotal = 8UL * 1073741824, Commit = 12UL * 1073741824 }, "ai", 6.8);
    Check(result.MaximumMb >= result.InitialMb, "Pagefile recommendation order");
}
Console.WriteLine($"Native regression: {passed} assertions passed; no live Windows processes changed.");

{
    string temporary = Path.Combine(Path.GetTempPath(), "RamFlow.Native.Recovery-" + Guid.NewGuid().ToString("N"));
    var p = new MockResourceProvider(); p.Current = p.Current with { Available = 400 * 1048576UL, Commit = 15UL * 1073741824 };
    using var first = new Engine(p, temporary); first.ApplySettings(new() { DryRun = false });
    for (int i = 0; i < 36; i++) { first.Tick(); p.Current = p.Current with { Time = p.Current.Time.AddMinutes(1) }; }
    Check(p.Values[987650].CpuPriority == 0x4000, "Crash simulation precondition");
    using (var recovered = new Engine(p, temporary)) { recovered.ApplySettings(new() { Paused = true }); Check(p.Values[987650].CpuPriority == 0x20, "Persisted recovery journal did not restore"); }
    first.Dispose();
    if (!Path.GetFullPath(temporary).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new Exception("Test cleanup boundary");
    Directory.Delete(temporary, true);
    Console.WriteLine($"Native persisted recovery: {passed} assertions passed.");
}

{
    string temp = Path.Combine(Path.GetTempPath(), "RamFlow.Native.Safety-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
    try {
        string backup = Path.Combine(temp, "pagefile.json");
        Pagefile.PublishBackup(backup, new(false, []));
        Check(!Pagefile.ReadBackup(backup).Automatic && Pagefile.ReadBackup(backup).Entries.Length == 0, "Disabled Pagefile backup rejected");
        try { Pagefile.PublishBackup(backup, new(true, [])); throw new Exception("Initial backup overwritten"); } catch (IOException) { passed++; }
        File.WriteAllText(backup, "{broken");
        try { Pagefile.ReadBackup(backup); throw new Exception("Partial backup accepted"); } catch (JsonException) { passed++; }
        string zip = Path.Combine(temp, "valid.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) {
            using (var writer = new StreamWriter(archive.CreateEntry("RamFlow-native/RamFlow.exe").Open())) writer.Write("MZ-test");
            using (var writer = new StreamWriter(archive.CreateEntry("RamFlow-native/Install.ps1").Open())) writer.Write("test");
        }
        Check(File.Exists(Path.Combine(await Updates.ExtractValidatedAsync(zip, Path.Combine(temp, "valid")), "RamFlow.exe")), "Valid update rejected");
        string attack = Path.Combine(temp, "attack.zip");
        using (var archive = ZipFile.Open(attack, ZipArchiveMode.Create)) { using var output = archive.CreateEntry("../escape.txt").Open(); output.WriteByte(1); }
        try { await Updates.ExtractValidatedAsync(attack, Path.Combine(temp, "attack")); throw new Exception("Zip traversal accepted"); } catch (IOException) { passed++; }
        Check(!File.Exists(Path.Combine(temp, "escape.txt")), "Zip escaped staging");
        string sizeAttack = Path.Combine(temp, "size.zip");
        using (var archive = ZipFile.Open(sizeAttack, ZipArchiveMode.Create)) { using var output = archive.CreateEntry("RamFlow-native/data", CompressionLevel.NoCompression).Open(); output.Write(new byte[20000]); }
        byte[] raw = File.ReadAllBytes(sizeAttack);
        for (int i = 0; i < raw.Length - 46; i++) {
            if (BitConverter.ToUInt32(raw, i) == 0x02014b50) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(i + 24), 1);
            if (BitConverter.ToUInt32(raw, i) == 0x04034b50) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(i + 22), 1);
        }
        File.WriteAllBytes(sizeAttack, raw);
        try { await Updates.ExtractValidatedAsync(sizeAttack, Path.Combine(temp, "size")); throw new Exception("Actual extracted size mismatch accepted"); } catch (IOException) { passed++; }
    } finally {
        if (!Path.GetFullPath(temp).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new Exception("Safety cleanup boundary");
        Directory.Delete(temp, true);
    }
    Console.WriteLine($"Native safety regression: {passed} assertions passed.");
}

// Pressure-dependent memory relief uses only mock processes and a controlled clock.
{
    var p = new MockResourceProvider();
    p.Current = p.Current with { Available = 400 * 1048576UL, PagesInput = 0, Standby = 0 };
    p.Items.Add(new(987652, 14, "smaller-idle.exe", "D:\\Apps\\smaller.exe", 1, 300 * 1048576UL, 250 * 1048576UL, 0, 0, true));
    using var e = new Engine(p, persist: false);
    e.ApplySettings(new() { DryRun = false });
    for (int i = 0; i < 90; i++) { e.Tick(); p.Current = p.Current with { Time = p.Current.Time.AddSeconds(2) }; }
    Check(p.Trims == 0, "Severe pressure trimmed before three minutes of observed inactivity");
    var state = e.Tick();
    Check(p.Trims == 1 && p.TrimmedPids[0] == 987650, "Largest eligible app was not trimmed first at three minutes");
    Check(p.Writes == 0, "Short memory relief changed CPU or power settings before thirty minutes");
    Check(state.Processes.Count == 3, "A trim truncated the process list");
    Check(state.TrimCount == 1 && state.LastWorkingSetReduction == 250 * 1048576UL, "Measured working-set reduction is wrong");
    p.Current = p.Current with { Time = p.Current.Time.AddSeconds(119) }; e.Tick();
    Check(p.Trims == 1, "Global trim cooldown violated");
    p.Current = p.Current with { Time = p.Current.Time.AddSeconds(1) }; e.Tick();
    Check(p.Trims == 2 && p.TrimmedPids[1] == 987652, "Other app did not become eligible after global cooldown");
    p.Current = p.Current with { Time = p.Current.Time.AddSeconds(120) }; e.Tick();
    Check(p.Trims == 2, "Same-app fifteen-minute cooldown violated");
}
foreach (string scenario in new[] { "dry", "paused", "fullscreen", "unknown", "whitelist", "foreground", "active", "self", "disabled", "no-foreground" }) {
    var p = new MockResourceProvider();
    p.Current = p.Current with { Available = 100 * 1048576UL, PagesInput = 0, Standby = 0 };
    var settings = new Settings { DryRun = false };
    switch (scenario) {
        case "dry": settings = settings with { DryRun = true }; break;
        case "paused": settings = settings with { Paused = true }; break;
        case "fullscreen": p.FullscreenActive = true; break;
        case "unknown": p.Items[0] = p.Items[0] with { ActivityKnown = false }; break;
        case "whitelist": settings = settings with { Whitelist = ["idle-demo.exe"] }; break;
        case "foreground": p.Focus = (987650, "idle-demo.exe"); p.Items.RemoveAt(1); break;
        case "active": p.Items[0] = p.Items[0] with { CpuPercent = 3 }; break;
        case "self": p.Items[0] = p.Items[0] with { Name = "RamFlow.exe" }; break;
        case "disabled": settings = settings with { WorkingSetTrim = false }; break;
        case "no-foreground": p.Focus = (0, ""); break;
    }
    using var e = new Engine(p, persist: false); e.ApplySettings(settings);
    for (int i = 0; i < 70; i++) { e.Tick(); p.Current = p.Current with { Time = p.Current.Time.AddSeconds(2) }; }
    Check(p.Trims == 0, "Protected relief scenario trimmed resources: " + scenario);
}
{
    var p = new MockResourceProvider();
    p.Current = p.Current with { Available = 100 * 1048576UL, PagesInput = 0, Standby = 0 };
    using var e = new Engine(p, persist: false); e.ApplySettings(new() { DryRun = false });
    for (int i = 0; i < 30; i++) { e.Tick(); p.Current = p.Current with { Time = p.Current.Time.AddSeconds(2) }; }
    Check(p.Trims == 0, "Critical pressure trimmed before one minute");
    e.Tick(); Check(p.Trims == 1, "Critical pressure did not relieve an inactive app after one minute");
    Check(Policy.TrimIdleSeconds(PressureLevel.High) == 600, "High pressure ten-minute delay changed");
}
{
    var pressure = new Pressure();
    var low = new Snapshot { RamTotal = 8UL * 1073741824, Available = 100 * 1048576UL, Commit = 6UL * 1073741824, CommitLimit = 16UL * 1073741824 };
    for (int i = 0; i < 20; i++) { pressure.Update(low); if (i >= 1) Check(pressure.Level == PressureLevel.Critical, "Persistently critical memory pressure was downgraded without recovery"); }
}
Console.WriteLine($"Native pressure relief regression: {passed} assertions passed.");

foreach (string siblingActivity in new[] { "cpu", "io", "unknown", "new" }) {
    var p = new MockResourceProvider();
    p.Current = p.Current with { Available = 100 * 1048576UL, Standby = 0 };
    var sibling = new ProcessSample(987652, 14, "idle-demo.exe", "D:\\Apps\\idle-demo.exe", 1,
        50 * 1048576UL, 45 * 1048576UL, 0, 0, true);
    if (siblingActivity != "new") p.Items.Add(sibling);
    using var e = new Engine(p, persist: false); e.ApplySettings(new() { DryRun = false });
    for (int i = 0; i < 30; i++) { e.Tick(); p.Current = p.Current with { Time = p.Current.Time.AddSeconds(2) }; }
    if (siblingActivity == "new") p.Items.Add(sibling);
    else p.Items[2] = siblingActivity switch {
        "cpu" => sibling with { CpuPercent = 3 },
        "io" => sibling with { IoBytesPerSecond = 2 * 1048576 },
        _ => sibling with { ActivityKnown = false }
    };
    e.Tick();
    Check(p.Trims == 0, "Large quiet process was trimmed before accounting for its sibling: " + siblingActivity);
}
Console.WriteLine($"Native app-group activity regression: {passed} assertions passed.");

if (args.Contains("--windows")) {
    using var provider = new WindowsResourceProvider();
    var snapshot = provider.Sample(); Check(snapshot.RamTotal > 0 && snapshot.CommitLimit >= snapshot.Commit, "Windows telemetry invalid");
    Check(provider.Topology().Count > 0, "CPU topology empty");
    using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, Arguments = "/c ping -n 20 127.0.0.1 > nul" })!;
    try {
        Thread.Sleep(200); long identity = provider.Identity(child.Id)!.Value; var original = provider.ReadValues(child.Id) ?? throw new Exception("Resource lookup failed");
        Check(provider.WriteValues(child.Id, original with { MemoryPriority = 3, CpuPriority = 0x4000, PowerControl = original.PowerControl | 1, PowerState = original.PowerState | 1 }, identity), "Native resource setters failed");
        Check(provider.ReadValues(child.Id)!.CpuPriority == 0x4000, "CPU readback wrong");
        Check(provider.WriteValues(child.Id, original, identity), "Restore failed");
    } finally { child.Kill(true); child.WaitForExit(); }
    Console.WriteLine($"Native Windows verification: {passed} assertions passed.");
}

if (args.Contains("--ipc")) {
    using (var fake = new NamedPipeServerStream(EnginePipe.Name, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly)) {
        var reading = Task.Run(async () => {
            using var timeout = new CancellationTokenSource(5000); await fake.WaitForConnectionAsync(timeout.Token); fake.ReadMode = PipeTransmissionMode.Message;
            byte[] buffer = new byte[4096];
            do { if (await fake.ReadAsync(buffer, timeout.Token) == 0) throw new IOException("Identity probe disconnected"); } while (!fake.IsMessageComplete);
            TokenImpersonationLevel observed = TokenImpersonationLevel.None;
            fake.RunAsClient(() => { using var identity = WindowsIdentity.GetCurrent(); observed = identity.ImpersonationLevel; });
            await Task.Delay(100, timeout.Token); // Force an asynchronous read, matching the real WPF startup race.
            await fake.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { Ok = true, Data = true }), timeout.Token);
            return observed;
        });
        var blockedContext = new NonPumpingContext();
        bool ping = await Task.Run(() => {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(blockedContext);
            try { return EnginePipe.Request<bool>(new("ping")); }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Check(ping, "Fake identity probe failed");
        Check(blockedContext.Posts == 0, "Synchronous IPC captured the UI context and would deadlock");
        Check(await reading == TokenImpersonationLevel.Identification, "Client delegated impersonation token to server");
    }
    using var mock = new MockResourceProvider(); using var engine = new Engine(mock, persist: false); engine.Tick();
    using var lifetime = new CancellationTokenSource();
    var server = Task.Run(() => EnginePipe.ServeAsync(engine, lifetime));
    bool ready = false;
    for (int i = 0; i < 20 && !ready; i++) { try { ready = EnginePipe.Request<bool>(new("ping"), 300); } catch { await Task.Delay(50); } }
    try {
        Check(ready, "IPC did not start");
        var state = EnginePipe.Request<State>(new("state")); Check(state.Processes.Count == 2, "IPC state wrong");
        EnginePipe.Request<Settings>(new("apply", new() { DryRun = true }));
        if (!Pagefile.IsAdministrator()) {
            try { EnginePipe.Request<Settings>(new("apply", new() { DryRun = false })); throw new Exception("Nonadmin live optimization accepted"); } catch (InvalidOperationException) { passed++; }
        }
        Check(mock.Writes == 0, "IPC test touched process resources");
        var (busyProvider, busyEngine) = Busy();
        using (busyEngine) { busyProvider.FailWrite = true; busyEngine.Stop(); Check(busyEngine.GetState().PendingRestores > 0, "Failed stop lost pending restorations"); busyProvider.FailWrite = false; busyEngine.Stop(); Check(busyEngine.GetState().PendingRestores == 0, "Stop retry failed"); }
        EnginePipe.Request<bool>(new("stop"));
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    } finally { lifetime.Cancel(); }
    Console.WriteLine($"Native IPC verification: {passed} assertions passed.");
}

sealed class NonPumpingContext : SynchronizationContext
{
    public int Posts;
    public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref Posts);
}
