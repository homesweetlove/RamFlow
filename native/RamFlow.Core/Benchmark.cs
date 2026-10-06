using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RamFlow.Core;

public sealed record BenchmarkResult(DateTimeOffset Time, bool DryRun, Snapshot Before, Snapshot After,
    double? ApplicationLaunchMs, double? WorkloadDurationMs, double SchedulingJitterMs, string[] Notes);
public static class Benchmark
{
    public static async Task<BenchmarkResult> RunAsync(Engine engine, string? executable = null, string[]? arguments = null, CancellationToken token = default)
    {
        var before = engine.GetState().Snapshot;
        double? launch = null, duration = null;
        if (!string.IsNullOrEmpty(executable)) {
            if (!File.Exists(executable) || !Path.IsPathFullyQualified(executable)) throw new ArgumentException("실행 파일의 전체 경로를 선택하세요.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
            foreach (string arg in arguments ?? []) start.ArgumentList.Add(arg);
            var clock = Stopwatch.StartNew();
            using var process = Process.Start(start) ?? throw new IOException("벤치마크 프로그램을 실행하지 못했습니다.");
            // 메시지 루프 준비 시간이며 실제 UI 입력 지연과 동일하지 않다.
            bool ready = await Task.Run(() => { try { return process.WaitForInputIdle(10000); } catch (InvalidOperationException) { return false; } }, token);
            if (ready) launch = clock.Elapsed.TotalMilliseconds;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try { await process.WaitForExitAsync(timeout.Token); duration = clock.Elapsed.TotalMilliseconds; }
            catch (OperationCanceledException) { /* 사용자 작업은 임의 종료하지 않는다. */ }
        } else await Task.Delay(3000, token);
        var timer = Stopwatch.StartNew(); await Task.Delay(20, token); double jitter = Math.Max(0, timer.Elapsed.TotalMilliseconds - 20);
        return new(DateTimeOffset.UtcNow, engine.Settings.DryRun, before, engine.GetState().Snapshot, launch, duration, jitter,
            ["조치를 강제로 발생시키지 않습니다. Before/After는 자연 변동을 포함합니다.", "GUI 준비 시간과 스케줄링 지터는 실제 입력 지연·프레임 시간이 아닙니다.", "게임 Frame Time/LLM tokens/sec는 런타임이 생성한 CSV를 가져와 측정하세요."]);
    }
    public static void Export(BenchmarkResult result, string path)
    {
        string full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full + ".json", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        string csv = "metric,before,after\n" + $"available_bytes,{result.Before.Available},{result.After.Available}\ncommit_bytes,{result.Before.Commit},{result.After.Commit}\ncpu_percent,{result.Before.CpuPercent},{result.After.CpuPercent}\ndisk_percent,{result.Before.DiskPercent},{result.After.DiskPercent}\n";
        File.WriteAllText(full + ".csv", csv, Encoding.UTF8);
    }
    public static (int Samples, double Mean, double P95) ImportMetricCsv(string path, int column = 0)
    {
        if (column < 0 || column > 256) throw new ArgumentException("CSV 열 번호가 올바르지 않습니다.");
        var values = new List<double>();
        using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(path) { TextFieldType = Microsoft.VisualBasic.FileIO.FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        for (int row = 0; !parser.EndOfData && row < 100000; row++) {
            var fields = parser.ReadFields() ?? [];
            if (column < fields.Length && double.TryParse(fields[column], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value >= 0) values.Add(value);
        }
        if (values.Count == 0) throw new ArgumentException("CSV에서 숫자 측정값을 찾지 못했습니다.");
        values.Sort(); return (values.Count, values.Average(), values[(int)Math.Floor((values.Count - 1) * .95)]);
    }
}
