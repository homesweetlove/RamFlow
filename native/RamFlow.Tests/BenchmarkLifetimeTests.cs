using System.Diagnostics;
using RamFlow.Core;
using RamFlow.UI;

public static class BenchmarkLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        using var provider = new MockResourceProvider();
        using var engine = new Engine(provider, persist: false);

        using (var lifetime = new BenchmarkPageLifetime(engine)) {
            check(!lifetime.Session.IsRunning, "Idle benchmark lifetime must not start sampling");
            lifetime.Dispose();
            lifetime.Dispose();
            CheckDisposed(lifetime, check);
        }

        await PendingExportAsync(engine, check);

        var failed = new BenchmarkPageLifetime(engine);
        var failure = new IOException("Injected exporter failure");
        var failedWorker = Task.FromException(failure);
        check(ReferenceEquals(failed.ExportAsync(() => failedWorker), failedWorker),
            "Export registration must return the raw worker task, including failures");
        try { await failedWorker; check(false, "Export failure must reach the caller"); }
        catch (IOException e) { check(ReferenceEquals(e, failure), "Export must preserve the original failure"); }
        try { failed.Dispose(); check(false, "Shutdown must surface a failed export"); }
        catch (IOException e) { check(ReferenceEquals(e, failure), "Shutdown must surface the original export failure without AggregateException"); }
        failed.Dispose();
        CheckDisposed(failed, check);

        check(provider.Writes == 0 && provider.Trims == 0,
            "Benchmark lifetime checks must not change process resources");
    }

    private static async Task PendingExportAsync(Engine engine, Action<bool, string> check)
    {
        using var lifetime = new BenchmarkPageLifetime(engine);
        var exportStarted = Milestone();
        var releaseExport = Milestone();
        var disposeStarted = Milestone();
        var disposeFinished = Milestone();
        var worker = Task.Run(async () => {
            exportStarted.TrySetResult(true);
            await releaseExport.Task.ConfigureAwait(false);
        });
        var disposer = new Thread(() => {
            disposeStarted.TrySetResult(true);
            try { lifetime.Dispose(); disposeFinished.TrySetResult(true); }
            catch (Exception e) { disposeFinished.TrySetException(e); }
        }) { IsBackground = true, Name = "Benchmark lifetime regression disposal" };
        bool disposerStarted = false;
        try {
            check(ReferenceEquals(lifetime.ExportAsync(() => worker), worker),
                "Shutdown must track the raw export worker rather than a caller continuation");
            await exportStarted.Task.WaitAsync(Timeout);
            disposer.Start();
            disposerStarted = true;
            await disposeStarted.Task.WaitAsync(Timeout);
            // With an idle session, the only blocking operation on this dedicated
            // thread is joining the pending export. Merely seeing an incomplete
            // Task.Run(Dispose) could pass before Dispose had even been scheduled.
            await UntilAsync(() => disposeFinished.Task.IsCompleted ||
                (disposer.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0);
            check(!disposeFinished.Task.IsCompleted && !worker.IsCompleted,
                "Dispose must block on a pending export until its worker completes");
        } finally {
            releaseExport.TrySetResult(true);
            await worker.WaitAsync(Timeout);
            if (disposerStarted) await disposeFinished.Task.WaitAsync(Timeout);
        }
        check(worker.IsCompletedSuccessfully,
            "Dispose must not return before the registered worker completes successfully");
        CheckDisposed(lifetime, check);
    }

    private static TaskCompletionSource<bool> Milestone() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task UntilAsync(Func<bool> reached)
    {
        var clock = Stopwatch.StartNew();
        while (!reached()) {
            if (clock.Elapsed >= Timeout) throw new TimeoutException("Benchmark disposal did not reach the export-wait milestone.");
            await Task.Yield();
        }
    }

    private static void CheckDisposed(BenchmarkPageLifetime lifetime, Action<bool, string> check)
    {
        try { lifetime.Session.Start(); check(false, "A disposed benchmark lifetime must reject Start"); }
        catch (ObjectDisposedException) { check(true, "A disposed benchmark lifetime must reject Start"); }
        bool invoked = false;
        try {
            _ = lifetime.ExportAsync(() => { invoked = true; return Task.CompletedTask; });
            check(false, "A disposed benchmark lifetime must reject export");
        } catch (ObjectDisposedException) { check(!invoked, "A disposed lifetime must reject export before invoking its writer"); }
    }
}
