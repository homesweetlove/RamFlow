using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using RamFlow.Core;

public static class BenchmarkTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        using var provider = new MockResourceProvider();
        using var engine = new Engine(provider, persist: false);
        using var currentProcess = Process.GetCurrentProcess();
        string executable = currentProcess.MainModule?.FileName ?? throw new Exception("Cannot locate test host executable.");

        // All workload launches below go through FakeSource.Launch; the host is never started.
        {
            var source = new FakeSource(engine);
            int factories = 0;
            using var session = new BenchmarkSession(engine, () => { factories++; return source; });
            check(!session.IsRunning && factories == 0 && source.ReadCount == 0,
                "Benchmark construction must not create an observer or sample resources");
            Throws<InvalidOperationException>(() => { _ = session.StopAsync(); }, check, "Stop before Start");
            bool duplicateRejected = false;
            source.OnWait = s => {
                try { session.Start(); }
                catch (InvalidOperationException) { duplicateRejected = true; }
                _ = session.StopAsync();
            };
            session.Start();
            var result = await Finished(session, source);
            check(factories == 1 && duplicateRejected, "Duplicate Start must reject without creating another observer");
            check(result.StopReason == SessionBenchmarkStopReason.ManualStop && !session.IsRunning,
                "Manual stop must finish the observation");
            check(source.DisposeCount == 1, "Observer must be disposed exactly once after Stop");
            check(ReferenceEquals(session.StopAsync(), session.StopAsync()), "Repeated Stop must return the same completion task");
        }

        {
            int factories = 0;
            using var session = new BenchmarkSession(engine, () => { factories++; return new FakeSource(engine); });
            Throws<ArgumentException>(() => session.Start("relative.exe"), check, "Relative workload path");
            Throws<ArgumentException>(() => session.Start(""), check, "Empty workload path");
            Throws<ArgumentException>(() => session.Start(Path.GetDirectoryName(executable)), check, "Directory workload path");
            Throws<ArgumentException>(() => session.Start(typeof(BenchmarkSession).Assembly.Location), check, "Non-executable workload path");
            Throws<ArgumentException>(() => session.Start(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe")), check, "Missing workload path");
            Throws<ArgumentException>(() => session.Start(null, ["orphan"]), check, "Arguments without workload");
            Throws<ArgumentException>(() => session.Start(executable, ["bad\0argument"]), check, "NUL in workload argument");
            Throws<ArgumentException>(() => session.Start(executable, [null!]), check, "Null workload argument");
            Throws<ArgumentException>(() => session.Start(executable, Enumerable.Repeat("a", 257).ToArray()), check, "Excess argument count");
            Throws<ArgumentException>(() => session.Start(executable, [new string('x', 30001)]), check, "Excess argument length");
            check(!session.IsRunning && factories == 0, "Invalid workload must fail before starting an observer");
        }

        {
            var source = new FakeSource(engine);
            using var session = new BenchmarkSession(engine, () => source);
            source.OnWait = s => { if (s.Delays.Count == 2) _ = session.StopAsync(); };
            string[] arguments = ["space ; & argument", "", "quoted\"value"];
            session.Start(executable, arguments);
            var result = await Finished(session, source);
            check(source.LaunchCount == 1 && source.LaunchInfo is { UseShellExecute: false } &&
                source.LaunchInfo.FileName == Path.GetFullPath(executable) &&
                source.LaunchInfo.WorkingDirectory == Path.GetDirectoryName(executable) &&
                source.LaunchInfo.ArgumentList.SequenceEqual(arguments), "Workload must preserve literal ArgumentList and disable shell execution");
            check(result.StopReason == SessionBenchmarkStopReason.ManualStop && !result.WorkloadCompleted &&
                result.WorkloadDurationMs is null && result.WorkloadExitCode is null && !result.IsValid,
                "Manual stop must leave an unfinished workload incomplete, without fabricated duration or exit code");
            check(source.Workload.DisposeCount == 1 && source.Workload.ExitAt is null,
                "Stopping observation must release the workload observer without pretending the workload exited");
        }

        {
            var source = new FakeSource(engine);
            source.Workload.ExitAt = source.Epoch.AddMilliseconds(3250);
            source.Workload.MeasuredDurationMs = 3125.5;
            source.Workload.Code = 7;
            using var session = new BenchmarkSession(engine, () => source);
            session.Start(executable);
            var result = await Finished(session, source);
            check(result.StopReason == SessionBenchmarkStopReason.WorkloadExited && result.WorkloadCompleted && result.IsValid,
                "Workload exit must complete a valid observation");
            check(result.WorkloadDurationMs == 3125.5 && result.WorkloadExitCode == 7 && result.DurationMs == 4000,
                "Workload duration and exit code must use the workload observer, not the later sampling timestamp");
            check(source.Workload.DisposeCount == 1 && source.DisposeCount == 1,
                "Completed workload and sampling observers must both be released");
        }

        {
            var source = new FakeSource(engine);
            using var session = new BenchmarkSession(engine, () => source);
            session.Start();
            var result = await Finished(session, source);
            check(result.StopReason == SessionBenchmarkStopReason.MaximumDuration && result.DurationMs == 30 * 60 * 1000 &&
                source.Elapsed == TimeSpan.FromMinutes(30), "Observation must stop at thirty minutes of fake elapsed time");
            check(result.SampleCount > 256 && result.SampleCount == source.ReadCount && result.Samples.Count == 256,
                "Observation must retain only the latest 256 samples while counting every accepted sample");
            check(result.Samples[0].ElapsedMs > 0 && result.Samples.Zip(result.Samples.Skip(1),
                (a, b) => a.ElapsedMs < b.ElapsedMs).All(x => x), "Bounded sample history must preserve the newest samples in chronological order");
            check(source.Delays.All(x => x == TimeSpan.FromSeconds(2)) && result.IsValid,
                "Healthy observations must keep the two-second interval");
        }

        foreach (bool lateWorkloadExit in new[] { false, true }) {
            var source = new FakeSource(engine) { AdvanceReadBy = TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(1) };
            if (lateWorkloadExit) {
                source.Workload.ExitAt = source.Epoch.AddMinutes(30).AddMilliseconds(500);
                source.Workload.MeasuredDurationMs = 1800500;
            }
            using var session = new BenchmarkSession(engine, () => source);
            session.Start(lateWorkloadExit ? executable : null);
            var result = await Finished(session, source);
            check(result.StopReason == SessionBenchmarkStopReason.MaximumDuration && source.Delays.Count == 0 && source.ReadCount == 1,
                "Read crossing the deadline must stop without a negative or extra wait: " + lateWorkloadExit);
            check(!result.WorkloadCompleted && result.WorkloadDurationMs is null && result.WorkloadExitCode is null,
                "An exit recorded after the maximum duration must not complete the workload");
        }

        {
            var source = new FakeSource(engine);
            source.SnapshotForRead = s => s.ReadCount switch {
                1 => Healthy(s) with { CpuPercent = 85 },
                2 => Healthy(s) with { Available = 99, RamTotal = 1000 },
                _ => Healthy(s) with { CpuPercent = 84.99, Available = 100, RamTotal = 1000 }
            };
            using var session = new BenchmarkSession(engine, () => source);
            source.OnWait = s => { if (s.Delays.Count == 4) _ = session.StopAsync(); };
            session.Start();
            var result = await Finished(session, source);
            check(result.SkippedSamples == 2 && result.SampleCount >= 2 && result.Samples.Count == result.SampleCount,
                "CPU at 85 percent and RAM below ten percent must skip detailed retained observations");
            check(source.Delays.SequenceEqual(new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2) }), "Pressure must back off ten seconds and recover to two seconds at exact ten-percent available RAM");
            check(result.Samples.All(x => x.System.CpuPercent < 85 && x.System.Available == 100),
                "Pressure samples must not enter detailed history");
        }

        // Each mutable setting must invalidate the session even when later restored.
        (string Name, Func<Settings, Settings> Change)[] settingChanges = [
            ("DryRun", s => s with { DryRun = !s.DryRun }),
            ("Paused", s => s with { Paused = !s.Paused }),
            ("Mode", s => s with { Mode = "ai" }),
            ("CpuManagement", s => s with { CpuManagement = !s.CpuManagement }),
            ("EcoQos", s => s with { EcoQos = !s.EcoQos }),
            ("CpuSets", s => s with { CpuSets = !s.CpuSets }),
            ("WorkingSetTrim", s => s with { WorkingSetTrim = !s.WorkingSetTrim }),
            ("Learning", s => s with { Learning = !s.Learning }),
            ("Whitelist", s => s with { Whitelist = ["changed.exe"] })
        ];
        foreach (var change in settingChanges) {
            var source = new FakeSource(engine);
            var original = source.SettingsValue;
            using var session = new BenchmarkSession(engine, () => source);
            source.OnWait = s => {
                if (s.Delays.Count == 1) s.SettingsValue = change.Change(original);
                if (s.Delays.Count == 2) s.SettingsValue = original;
                if (s.Delays.Count == 3) _ = session.StopAsync();
            };
            session.Start();
            var result = await Finished(session, source);
            check(result.SettingsChanged && !result.IsValid && result.SettingsBefore == result.SettingsAfter,
                "Observed setting flip must stay invalid after restoration: " + change.Name);
        }

        foreach (bool contentChanged in new[] { false, true }) {
            var source = new FakeSource(engine) { SettingsValue = new Settings { DryRun = false, Whitelist = ["Editor.exe", "worker.exe"] } };
            using var session = new BenchmarkSession(engine, () => source);
            source.OnWait = s => {
                if (s.Delays.Count == 1) s.SettingsValue = s.SettingsValue with {
                    Whitelist = contentChanged ? ["EDITOR.EXE", "other.exe"] : ["EDITOR.EXE", "WORKER.EXE"]
                };
                if (s.Delays.Count == 3) _ = session.StopAsync();
            };
            session.Start();
            var result = await Finished(session, source);
            check(result.SettingsChanged == contentChanged && result.IsValid == !contentChanged,
                "Whitelist comparison must use case-insensitive content rather than array identity: " + contentChanged);
        }

        foreach (int failureAt in new[] { 1, 3 }) {
            var source = new FakeSource(engine) { FailReadAt = failureAt };
            using var session = new BenchmarkSession(engine, () => source);
            session.Start();
            var result = await Finished(session, source);
            check(result.StopReason == SessionBenchmarkStopReason.Error && !result.IsValid &&
                result.SampleCount == failureAt - 1 && result.Notes.Any(x => x.Contains("injected sample failure", StringComparison.Ordinal)),
                "Sampling failure must retain prior observations and return an invalid error result: " + failureAt);
            check(source.DisposeCount == 1 && !session.IsRunning, "Sampling failure must clean up and clear running state");
        }

        {
            int factories = 0;
            var session = new BenchmarkSession(engine, () => { factories++; return new FakeSource(engine); });
            session.Dispose(); session.Dispose();
            Throws<ObjectDisposedException>(() => session.Start(), check, "Start after disposal");
            check(factories == 0 && !session.IsRunning, "Disposing an idle observer must remain lazy and idempotent");
        }
        {
            var source = new FakeSource(engine);
            using var session = new BenchmarkSession(engine, () => source);
            source.OnWait = _ => { session.Dispose(); session.Dispose(); };
            session.Start(executable);
            var result = await Finished(session, source);
            check(result.StopReason == SessionBenchmarkStopReason.Disposed && !result.WorkloadCompleted && !result.IsValid,
                "Disposal during a workload must end observation with an incomplete workload");
            check(source.DisposeCount == 1 && source.Workload.DisposeCount == 1 && !session.IsRunning,
                "Running disposal must release observers exactly once");
            Throws<ObjectDisposedException>(() => session.Start(), check, "Restart after running disposal");
        }

        await ExportUnderCulture(check);
        check(provider.Writes == 0 && provider.Trims == 0, "Benchmark regression tests must never alter process resources");
        Console.WriteLine("Native on-demand benchmark regression passed (fake clock and workload; no real workload launched).");
    }

    private static async Task<SessionBenchmarkResult> Finished(BenchmarkSession session, FakeSource source)
    {
        // Disposal signals that the loop has already selected its reason. Do not request an early stop
        // merely to obtain its completion task, and do not assume a sample after a stop request.
        await source.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static void Throws<T>(Action action, Action<bool, string> check, string description) where T : Exception
    {
        try { action(); }
        catch (T) { check(true, description); return; }
        check(false, description + " was not rejected");
    }

    private static Snapshot Healthy(FakeSource source) => new() {
        Time = source.UtcNow, RamTotal = 8UL * 1073741824, Available = 4UL * 1073741824,
        Commit = 6UL * 1073741824, CommitLimit = 16UL * 1073741824,
        CpuPercent = 12.5, PagesInput = 1.25
    };

    private static async Task ExportUnderCulture(Action<bool, string> check)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        string temporary = Path.Combine(Path.GetTempPath(), "RamFlow.Benchmark.Tests-" + Guid.NewGuid().ToString("N"));
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var time = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var sample = new SessionBenchmarkSample(time, 1234.5,
                new Snapshot { Time = time, Available = 123456, Commit = 234567, CpuPercent = 12.5, PagesInput = 1.25 },
                new SessionProcessMetrics(11, 2.75, 345678, 456789),
                new SessionProcessMetrics(22, null, 567890, 678901));
            var result = new SessionBenchmarkResult {
                StartedAt = time, EndedAt = time.AddMilliseconds(1234.5), DurationMs = 1234.5,
                SampleCount = 2, Samples = Array.AsReadOnly(new[] { sample, sample with { UiProcess = null, EngineProcess = null } }),
                StopReason = SessionBenchmarkStopReason.ManualStop, WorkloadCompleted = true,
                WorkloadDurationMs = 987.25, WorkloadExitCode = 7, SettingsChanged = true,
                IsValid = false, SkippedSamples = 3, SamplingCpuMs = 1.5,
                SamplingWallMs = 2.75, SamplingAllocatedBytes = 456,
                Notes = ["Mesure manuelle — 설정 변경 없음"]
            };
            string path = Path.Combine(temporary, "nested", "observation");
            await BenchmarkSession.Export(result, path);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path + ".json"));
            check(document.RootElement.GetProperty("DurationMs").GetDouble() == 1234.5 &&
                document.RootElement.GetProperty("Samples")[0].GetProperty("System").GetProperty("CpuPercent").GetDouble() == 12.5 &&
                document.RootElement.GetProperty("Notes")[0].GetString() == result.Notes[0],
                "JSON export must preserve decimal values and Unicode under a non-English culture");
            var lines = await File.ReadAllLinesAsync(path + ".csv");
            var cells = lines[1].Split(',');
            check(lines.Length == 3 && lines[0].Split(',').Length == 24 && cells.Length == 24 &&
                cells[0] == time.ToString("O", CultureInfo.InvariantCulture) && cells[1] == "1234.5" &&
                cells[4] == "12.5" && cells[5] == "1.25" && cells[7] == "2.75" && cells[11] == "" &&
                cells[13] == "678901", "CSV export must keep invariant decimals, ISO timestamps and twenty-four aligned columns");
            check(cells.Skip(14).SequenceEqual(new[] { "ManualStop", "True", "987.25", "7", "True", "False", "3", "1.5", "2.75", "456" }),
                "CSV must export explicit stop, completion, validity and sampling overhead metadata with invariant decimals");
            check(lines[2].Split(',').Length == 24 && lines[2].Split(',').Skip(6).Take(8).All(x => x == ""),
                "CSV must preserve empty columns for unavailable process metrics");
            string emptyPath = Path.Combine(temporary, "empty");
            await BenchmarkSession.Export(result with { Samples = [], SampleCount = 0,
                StopReason = SessionBenchmarkStopReason.Error, WorkloadCompleted = false,
                WorkloadDurationMs = null, WorkloadExitCode = null }, emptyPath);
            var emptyLines = await File.ReadAllLinesAsync(emptyPath + ".csv");
            var emptyCells = emptyLines[1].Split(',');
            check(emptyLines.Length == 2 && emptyCells.Length == 24 && emptyCells.Take(14).All(x => x == "") &&
                emptyCells[14] == "Error" && emptyCells[15] == "False" && emptyCells[16] == "" &&
                emptyCells[17] == "" && emptyCells[19] == "False" && emptyCells[21] == "1.5",
                "Empty failed observations must still export an aligned metadata row without fabricated sample or workload values");
        } finally {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
            if (Path.GetDirectoryName(temporary) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
                Path.GetFileName(temporary).StartsWith("RamFlow.Benchmark.Tests-", StringComparison.Ordinal) && Directory.Exists(temporary))
                Directory.Delete(temporary, true);
        }
    }

    private sealed class FakeSource : BenchmarkSource
    {
        internal readonly DateTimeOffset Epoch = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        internal readonly List<TimeSpan> Delays = [];
        internal readonly TaskCompletionSource<bool> Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly FakeWorkload Workload;
        internal Settings SettingsValue;
        internal Action<FakeSource>? OnWait;
        internal Func<FakeSource, Snapshot>? SnapshotForRead;
        internal ProcessStartInfo? LaunchInfo;
        internal int ReadCount, LaunchCount, DisposeCount, FailReadAt;
        internal TimeSpan AdvanceReadBy;
        private TimeSpan elapsed;

        internal FakeSource(Engine engine)
        {
            SettingsValue = engine.Settings;
            Workload = new FakeWorkload(this);
        }
        internal override DateTimeOffset UtcNow => Epoch + elapsed;
        internal override TimeSpan Elapsed => elapsed;
        internal override Settings ReadSettings() => SettingsValue;
        internal override SessionBenchmarkSample Read()
        {
            ReadCount++;
            if (ReadCount == FailReadAt) throw new IOException("injected sample failure");
            elapsed += AdvanceReadBy;
            return new(UtcNow, elapsed.TotalMilliseconds, SnapshotForRead?.Invoke(this) ?? Healthy(this),
                new SessionProcessMetrics(111, 1.5, 1000000, 2000000),
                new SessionProcessMetrics(222, 2.5, 3000000, 4000000));
        }
        internal override void Wait(WaitHandle stop, TimeSpan delay)
        {
            if (delay <= TimeSpan.Zero || Delays.Count >= 1000) throw new InvalidOperationException("Fake observer exceeded its wait budget");
            if (stop.WaitOne(0)) return;
            Delays.Add(delay);
            elapsed += delay;
            OnWait?.Invoke(this);
        }
        internal override BenchmarkWorkload Launch(ProcessStartInfo info)
        {
            LaunchCount++; LaunchInfo = info;
            return Workload;
        }
        public override void Dispose() { DisposeCount++; Disposed.TrySetResult(true); }
    }

    private sealed class FakeWorkload(FakeSource source) : BenchmarkWorkload
    {
        internal DateTimeOffset? ExitAt;
        internal double MeasuredDurationMs;
        internal int Code, DisposeCount;
        internal override DateTimeOffset? ExitTime => ExitAt.HasValue && source.UtcNow >= ExitAt.Value ? ExitAt : null;
        internal override double DurationMs => MeasuredDurationMs;
        internal override int ExitCode => Code;
        public override void Dispose() { DisposeCount++; }
    }
}
