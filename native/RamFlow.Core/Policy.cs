namespace RamFlow.Core;

public static class Policy
{
    public static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase) {
        "system", "registry", "smss", "csrss", "wininit", "winlogon", "lsass", "services", "svchost", "dwm", "explorer", "audiodg", "spoolsv", "conhost", "dllhost", "fontdrvhost", "sihost", "ctfmon", "taskhostw", "runtimebroker", "searchindexer", "searchhost", "msmpeng", "nissrv", "sense", "mssense", "securityhealthservice", "securityhealthsystray", "csfalconservice", "avp", "avastsvc", "avgsvc", "ekrn", "bdagent", "mbamservice", "nvcontainer", "nvdisplay.container", "amdrsserv", "vgc", "beservice", "easyanticheat", "easyanticheat_eos", "wudfhost", "vmmem", "vmmemwsl", "vmcompute", "vmwp" };
    public static readonly HashSet<string> ActiveNames = new(StringComparer.OrdinalIgnoreCase) {
        "ffmpeg", "vlc", "spotify", "qbittorrent", "aria2c", "curl", "wget", "robocopy", "7z", "node", "python", "pythonw", "dotnet", "msbuild", "cl", "clang", "gcc", "rustc", "cargo", "java", "javac", "docker", "postgres", "mysqld", "sqlservr", "redis-server", "ollama", "ollama_llama_server", "llama-server", "llama-cli", "lm studio", "lmstudio", "koboldcpp", "comfyui", "automatic1111", "blender", "obs64", "discord" };
    public static readonly Dictionary<string, HashSet<string>> Groups = new() {
        ["developer"] = new(StringComparer.OrdinalIgnoreCase) { "code", "devenv", "idea64", "pycharm64", "rider64", "windowsterminal", "powershell", "pwsh", "cmd", "git", "chrome", "msedge", "firefox", "node", "python", "docker", "dotnet" },
        ["gaming"] = new(StringComparer.OrdinalIgnoreCase) { "discord", "steam", "audiodg", "obs64", "easyanticheat", "vgc" },
        ["ai"] = new(StringComparer.OrdinalIgnoreCase) { "ollama", "ollama_llama_server", "llama-server", "llama-cli", "lm studio", "lmstudio", "koboldcpp", "python", "comfyui", "automatic1111" },
        ["browser"] = new(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge", "firefox", "brave", "opera" },
        ["office"] = new(StringComparer.OrdinalIgnoreCase) { "winword", "excel", "powerpnt", "outlook", "onenote", "hwp" },
        ["media"] = new(StringComparer.OrdinalIgnoreCase) { "vlc", "spotify", "ffmpeg", "blender", "obs64", "photoshop", "premiere" } };
    public static string Base(string name) => Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
    public static int TrimIdleSeconds(PressureLevel level) => level switch {
        PressureLevel.Critical => 60,
        PressureLevel.Severe => 180,
        PressureLevel.High => 600,
        _ => 7200
    };
    public static string Mode(Settings settings, string foreground, Snapshot snap, bool fullscreen)
    {
        if (settings.Mode != "auto") return settings.Mode;
        string name = Base(foreground);
        foreach (string group in new[] { "developer", "ai", "media", "office", "browser" }) if (Groups[group].Contains(name)) return group;
        return fullscreen ? "gaming" : snap.OnBattery ? "battery" : "background";
    }
    public static string? Protect(ProcessSample p, (int Pid, string Name) foreground, Settings settings, string mode)
    {
        string name = Base(p.Name);
        if (p.Pid <= 4 || p.Pid == Environment.ProcessId || name is "ramflow" or "ramflow.service" || ProtectedNames.Contains(name) || p.Session <= 0) return "시스템·서비스·보안 프로세스";
        if (p.Pid == foreground.Pid || string.Equals(p.Name, foreground.Name, StringComparison.OrdinalIgnoreCase)) return "현재 사용 중인 앱";
        if (settings.Whitelist.Any(x => Base(x) == name)) return "사용자 예외";
        if (!p.ActivityKnown) return "활동 정보 확인 불가";
        if (ActiveNames.Contains(name) || p.CpuPercent >= 2 || p.IoBytesPerSecond >= 1048576) return "AI·미디어·컴파일·서버 또는 활성 작업";
        if (Groups.TryGetValue(mode, out var group) && group.Contains(name)) return "현재 작업 그룹 보호";
        if (p.Path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "Windows 시스템 경로";
        if (p.WorkingSet > 209715200 && p.PrivateBytes < p.WorkingSet * .4) return "파일 매핑 비중이 큰 앱";
        return null;
    }
}

public sealed class Pressure
{
    private double? ema;
    private int up, down, critical;
    public double Score => ema ?? 0;
    public PressureLevel Level { get; private set; }
    public void Update(Snapshot s, double bias = 0)
    {
        double available = 100 * Math.Clamp((.4 - (double)s.Available / Math.Max(1, s.RamTotal)) / .37, 0, 1);
        double absolute = 100 * Math.Clamp((4 * 1073741824d - s.Available) / (3.7 * 1073741824), 0, 1);
        double commit = 100 * Math.Clamp(((double)s.Commit / Math.Max(1, s.CommitLimit) - .6) / .4, 0, 1);
        double faults = Math.Clamp((s.PagesInput ?? 0) / 30, 0, 100);
        double raw = Math.Max(.75 * Math.Max(available, commit), .4 * Math.Max(available, absolute) + .35 * commit + .15 * faults + .1 * (s.PagefileUsagePercent ?? 0));
        if (s.Available < 536870912 || (double)s.Commit / Math.Max(1, s.CommitLimit) >= .95) raw = Math.Max(raw, 80);
        raw -= 8 * Math.Min(1, (double)s.Standby / Math.Max(1, s.RamTotal) / .15);
        raw = Math.Clamp(raw + bias, 0, 100);
        ema = ema is null ? raw : ema + (raw > ema ? .35 : .08) * (raw - ema);
        bool severe = s.Available < 322122547 || (double)s.Commit / Math.Max(1, s.CommitLimit) >= .97;
        critical = severe ? critical + 1 : 0;
        PressureLevel target = critical >= 2 ? PressureLevel.Critical : FromScore(Score);
        if (target > Level) { down = 0; if (++up >= 3 || critical >= 2) { Level = target; up = 0; } }
        else { up = 0; if (target < Level && FromScore(Score + 5) < Level && ++down >= 5) { Level--; down = 0; } else if (target >= Level || FromScore(Score + 5) >= Level) down = 0; }
    }
    private static PressureLevel FromScore(double score) => score >= 90 ? PressureLevel.Critical : score >= 75 ? PressureLevel.Severe : score >= 50 ? PressureLevel.High : score >= 25 ? PressureLevel.Moderate : PressureLevel.Normal;
}
