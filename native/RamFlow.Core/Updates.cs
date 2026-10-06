using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;

namespace RamFlow.Core;

public sealed record ReleaseInfo(string Version, string Page, string? Zip, string? Checksums);
public static class Updates
{
    public const string CurrentVersion = "0.3.1";
    public static async Task<ReleaseInfo> CheckAsync(CancellationToken token = default)
    {
        using var client = Client();
        using var data = JsonDocument.Parse(await client.GetStringAsync("https://api.github.com/repos/homesweetlove/RamFlow/releases/latest", token));
        var root = data.RootElement; string tag = root.GetProperty("tag_name").GetString()!.TrimStart('v');
        if (!Version.TryParse(tag, out _)) throw new IOException("릴리스 버전 형식 오류");
        string? zip = null, sums = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray()) {
            string name = asset.GetProperty("name").GetString()!, url = asset.GetProperty("browser_download_url").GetString()!;
            if (!url.StartsWith("https://github.com/homesweetlove/RamFlow/releases/download/", StringComparison.Ordinal)) continue;
            if (name == $"RamFlow-{tag}-windows-x64.zip") zip = url;
            if (name == "SHA256SUMS.txt") sums = url;
        }
        return new(tag, root.GetProperty("html_url").GetString()!, zip, sums);
    }
    public static async Task<string> DownloadAsync(ReleaseInfo info, string destination, CancellationToken token = default)
    {
        if (info.Zip is null || info.Checksums is null || !Version.TryParse(info.Version, out _)) throw new IOException("검증 가능한 배포 파일이 없습니다.");
        foreach (string url in new[] { info.Zip, info.Checksums }) if (!url.StartsWith("https://github.com/homesweetlove/RamFlow/releases/download/", StringComparison.Ordinal)) throw new IOException("배포 주소 오류");
        Directory.CreateDirectory(destination);
        string path = Path.Combine(destination, $"RamFlow-{info.Version}-windows-x64.zip"), temporary = path + ".partial";
        using var client = Client();
        string sums = await client.GetStringAsync(info.Checksums, token);
        string expected = sums.Split('\n').FirstOrDefault(x => x.TrimEnd().EndsWith(Path.GetFileName(path), StringComparison.Ordinal))?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0] ?? throw new IOException("SHA-256 항목 없음");
        if (expected.Length != 64 || expected.Any(x => !Uri.IsHexDigit(x))) throw new IOException("SHA-256 형식 오류");
        try {
            using var response = await client.GetAsync(info.Zip, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(token)) await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1048576, true)) {
                byte[] buffer = new byte[1048576]; long count = 0; int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0) { count += read; if (count > 512L * 1048576) throw new IOException("배포 파일 크기 제한 초과"); await output.WriteAsync(buffer.AsMemory(0, read), token); }
            }
            await using var file = File.OpenRead(temporary); string actual = Convert.ToHexString(await SHA256.HashDataAsync(file, token)); file.Close();
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("다운로드 SHA-256 불일치");
            File.Move(temporary, path, true); return path;
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task<string> StageAsync(ReleaseInfo info, string destination, CancellationToken token = default)
    {
        if (Version.Parse(info.Version) <= Version.Parse(CurrentVersion)) throw new InvalidOperationException("현재 버전이 최신입니다.");
        string zip = await DownloadAsync(info, destination, token);
        string stage = Path.Combine(Path.GetFullPath(destination), "stage-" + Guid.NewGuid().ToString("N"));
        return await ExtractValidatedAsync(zip, stage, token);
    }
    public static async Task<string> ExtractValidatedAsync(string zip, string stage, CancellationToken token = default)
    {
        stage = Path.GetFullPath(stage);
        if (Directory.Exists(stage)) throw new IOException("업데이트 임시 경로가 이미 있습니다.");
        Directory.CreateDirectory(stage);
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > 10000 || archive.Entries.Sum(x => x.Length) > 1073741824) throw new IOException("업데이트 압축 파일 크기 제한 초과");
        long total = 0; byte[] buffer = new byte[131072];
        foreach (var entry in archive.Entries) {
            token.ThrowIfCancellationRequested();
            string target = Path.GetFullPath(Path.Combine(stage, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(':') || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new IOException("업데이트 경로 검증 실패");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, true);
            long extracted = 0; int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0) {
                extracted += read; total += read;
                if (extracted > entry.Length || total > 1073741824) throw new IOException("업데이트 실제 압축 해제 크기 불일치/제한 초과");
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
            if (extracted != entry.Length) throw new IOException("업데이트 압축 항목의 실제 길이가 다릅니다.");
        }
        string app = Path.Combine(stage, "RamFlow-native");
        if (!File.Exists(Path.Combine(app, "RamFlow.exe")) || !File.Exists(Path.Combine(app, "Install.ps1"))) throw new IOException("업데이트 배포 구조 오류");
        return app;
    }
    private static HttpClient Client() { var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) }; client.DefaultRequestHeaders.UserAgent.ParseAdd("RamFlow/0.3.1"); return client; }
}
