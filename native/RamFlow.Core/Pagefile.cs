using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RamFlow.Core;

public sealed record PagefileEntry(string Name, uint InitialSize, uint MaximumSize);
public sealed record PagefileConfiguration(bool Automatic, PagefileEntry[] Entries);
public sealed record PagefileAdvice(string Mode, uint InitialMb, uint MaximumMb, string Reason);
public static class Pagefile
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static bool IsAdministrator() { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
    public static PagefileAdvice Recommend(Snapshot snapshot, string mode, double modelGb = 0, ulong observedPeak = 0)
    {
        if (!new[] { "windows", "balanced", "lowram", "developer", "gaming", "ai", "custom" }.Contains(mode) || !double.IsFinite(modelGb) || modelGb < 0 || modelGb > 4096) throw new ArgumentException("Pagefile 입력이 올바르지 않습니다.");
        double demand = Math.Max(snapshot.Commit, observedPeak) + modelGb * 1.25 * 1073741824;
        double mb = Math.Max(2048, (demand * 1.2 - snapshot.RamTotal) / 1048576);
        uint initial = (uint)Math.Ceiling(Math.Min(1048576, mb) / 1024) * 1024;
        uint maximum = Math.Min(1048576, initial + Math.Max(2048, initial / 2));
        return new(mode, initial, maximum, mode == "windows" ? "Windows 자동 관리 권장" : "관측 Commit 수요 + 모델 런타임 예상 + 여유를 기준으로 계산했습니다. 실제 디스크 여유도 확인하세요.");
    }
    public static async Task<PagefileConfiguration> ReadAsync()
    {
        string json = await PowerShell("$c=Get-CimInstance Win32_ComputerSystem; $e=@(Get-CimInstance Win32_PageFileSetting | Select-Object Name,InitialSize,MaximumSize); @{Automatic=[bool]$c.AutomaticManagedPagefile;Entries=$e}|ConvertTo-Json -Depth 5 -Compress");
        return JsonSerializer.Deserialize<PagefileConfiguration>(json, Json) ?? throw new IOException("Pagefile 설정 조회 실패");
    }
    public static async Task ApplyAsync(PagefileConfiguration desired, string backupPath, bool dryRun)
    {
        Validate(desired);
        if (dryRun) return;
        if (!IsAdministrator()) throw new UnauthorizedAccessException("관리자 권한으로 RamFlow를 열어 적용하세요.");
        await Gate.WaitAsync();
        try {
        var original = await ReadAsync();
        string path = Path.GetFullPath(backupPath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) {
            PublishBackup(path, original);
        }
        ReadBackup(path); // 중단된 쓰기나 손상된 기존 백업을 신뢰하지 않는다.
        try { await PowerShell(Build(desired)); }
        catch { await PowerShell(Build(original)); throw; }
        } finally { Gate.Release(); }
    }
    public static async Task RestoreAsync(string backupPath, bool dryRun)
    {
        var original = ReadBackup(backupPath);
        if (dryRun) return;
        if (!IsAdministrator()) throw new UnauthorizedAccessException("관리자 권한이 필요합니다.");
        await Gate.WaitAsync(); try { await PowerShell(Build(original)); } finally { Gate.Release(); }
    }
    public static PagefileConfiguration ReadBackup(string path)
    {
        if (new FileInfo(path).Length > 1048576) throw new IOException("Pagefile 백업 크기 오류");
        var original = JsonSerializer.Deserialize<PagefileConfiguration>(File.ReadAllText(path), Json) ?? throw new IOException("백업 형식 오류");
        Validate(original, allowDisabled: true); return original;
    }
    public static void PublishBackup(string path, PagefileConfiguration original)
    {
        Validate(original, allowDisabled: true);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, original); file.Flush(true); }
            File.Move(temporary, path, false);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void Validate(PagefileConfiguration config, bool allowDisabled = false)
    {
        if (config.Entries is null || config.Entries.Length > 26 || !allowDisabled && !config.Automatic && config.Entries.Length == 0) throw new ArgumentException("Pagefile 설정이 올바르지 않습니다.");
        foreach (var entry in config.Entries) if (!Regex.IsMatch(entry.Name, @"^[A-Za-z]:\\pagefile\.sys$") || entry.InitialSize > entry.MaximumSize || entry.MaximumSize > 1048576) throw new ArgumentException("Pagefile 경로/크기 오류");
    }
    private static string Build(PagefileConfiguration config)
    {
        Validate(config, allowDisabled: true);
        string command = "Get-CimInstance Win32_ComputerSystem | Set-CimInstance -Property @{AutomaticManagedPagefile=$false}; Get-CimInstance Win32_PageFileSetting | Remove-CimInstance;";
        foreach (var e in config.Entries) command += $"New-CimInstance -ClassName Win32_PageFileSetting -Property @{{Name='{e.Name}';InitialSize=[uint32]{e.InitialSize};MaximumSize=[uint32]{e.MaximumSize}}}|Out-Null;";
        if (config.Automatic) command += "Get-CimInstance Win32_ComputerSystem | Set-CimInstance -Property @{AutomaticManagedPagefile=$true}";
        return command;
    }
    private static async Task<string> PowerShell(string command)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.UTF8Encoding]::new();" + command)) }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("PowerShell 실행 실패");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw new TimeoutException("Pagefile 작업 시간 초과"); }
        if (process.ExitCode != 0) throw new IOException(await error);
        return await output;
    }
}
