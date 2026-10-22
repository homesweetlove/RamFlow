using System;
using System.Threading.Tasks;
using RamFlow.Core;

namespace RamFlow.UI;

// Tracks file writes separately from UI continuations, so shutdown can finish
// an explicit export without waiting for the WPF dispatcher to resume an await.
internal sealed class BenchmarkPageLifetime : IDisposable
{
    internal BenchmarkSession Session { get; }
    private Task pendingExport = Task.CompletedTask;
    private bool disposed;

    internal BenchmarkPageLifetime(Engine engine) => Session = new BenchmarkSession(engine);

    internal Task ExportAsync(Func<Task> write)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!pendingExport.IsCompleted) throw new InvalidOperationException("보고서를 저장 중입니다.");
        return pendingExport = write();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try {
            if (Session.IsRunning) Session.StopAsync().GetAwaiter().GetResult();
            pendingExport.GetAwaiter().GetResult();
        } finally { Session.Dispose(); }
    }
}
