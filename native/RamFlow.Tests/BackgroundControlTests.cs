using System.Text.Json;
using RamFlow.Core;

static class BackgroundControlTests
{
    static Settings Config(string[] paths, bool dry = false, string[]? whitelist = null) =>
        JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new { DryRun = dry, BackgroundApps = paths, Whitelist = whitelist ?? [] }))!;
    static MockResourceProvider Provider() {
        var p = new MockResourceProvider();
        p.Items[0] = p.Items[0] with { Name = "python.exe", Path = @"D:\Apps\python.exe", CpuPercent = 40, IoBytesPerSecond = 2 * 1048576 };
        p.Current = p.Current with { CpuPercent = 95 };
        return p;
    }
    public static void Run(Action<bool,string> check) {
        using (var p = Provider()) using (var e = new Engine(p, persist:false)) {
            e.ApplySettings(Config([p.Items[0].Path])); e.Tick();
            check(p.Values.GetValueOrDefault(987650)?.CpuPriority == 0x4000, "Explicit busy background worker was not controlled without thirty-minute wait");
            check(p.Values[987650].MemoryPriority == 3, "Explicit background memory priority was not lowered");
            check(p.Trims == 0, "Busy opt-in target was forcibly trimmed");
            p.Current = p.Current with { CpuPercent = 70 }; e.Tick();
            check(p.Values[987650].CpuPriority == 0x4000, "CPU control oscillated immediately after reducing contention");
            p.Current = p.Current with { CpuPercent = 10 }; e.Tick();
            check(p.Values[987650].CpuPriority == 0x20, "Pressure relief did not restore explicit background worker");
        }
        foreach (string guard in new[] { "unselected", "foreground", "same-name", "system", "unknown", "whitelist", "dry" }) {
            using var p = Provider();
            if (guard == "foreground") p.Focus = (987650,"python.exe");
            if (guard == "same-name") p.Focus = (987651,"python.exe");
            if (guard == "system") p.Items[0] = p.Items[0] with { Name = "svchost.exe" };
            if (guard == "unknown") p.Items[0] = p.Items[0] with { ActivityKnown = false };
            using var e = new Engine(p,persist:false);
            e.ApplySettings(Config(guard == "unselected" ? [] : [p.Items[0].Path],guard == "dry",guard == "whitelist" ? ["python.exe"] : []));
            e.Tick(); check(p.Writes == 0, "Background opt-in bypassed hard protection: " + guard);
        }
        using (var p = Provider()) using (var e = new Engine(p,persist:false)) {
            p.FullscreenActive = true; e.ApplySettings(Config([p.Items[0].Path])); e.Tick();
            check(p.Writes == 1, "Explicit background priority did not yield to fullscreen foreground");
            p.Focus = (987650,"python.exe"); e.Tick();
            check(p.Values[987650].CpuPriority == 0x20, "Background to foreground transition did not restore");
            check(p.Trims == 0, "Fullscreen background opt-in weakened trim safeguards");
        }
        using (var p = Provider()) using (var e = new Engine(p,persist:false)) {
            e.ApplySettings(Config([p.Items[0].Path])); e.Tick(); e.ApplySettings(new() { DryRun = false });
            check(p.Values[987650].CpuPriority == 0x20, "Removing background designation did not restore");
        }
        foreach (string path in new[] { "python.exe", "", @"..\worker.exe" }) {
            bool rejected = false; try { Engine.Validate(Config([path])); } catch (ArgumentException) { rejected = true; }
            check(rejected,"Nonabsolute background path accepted: " + path);
        }
        using(var p=Provider())using(var e=new Engine(p,persist:false)) {
            var incoming=Config([p.Items[0].Path]);e.ApplySettings(incoming);incoming.BackgroundApps[0]=@"D:\other.exe";
            var outgoing=e.Settings;outgoing.BackgroundApps[0]=@"D:\changed.exe";
            e.Tick();check(p.Writes==1,"Caller mutated the engine background path list through an array alias");
        }
        Console.WriteLine("Explicit background policy regressions passed.");
    }
}
