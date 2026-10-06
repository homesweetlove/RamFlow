using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RamFlow.Core;

/// <summary>문서화된 Background Mode는 자기 프로세스에만 적용한다.</summary>
public sealed class BackgroundIo : IDisposable
{
    [DllImport("kernel32.dll")] private static extern bool SetPriorityClass(IntPtr process, uint value);
    private readonly Process process = Process.GetCurrentProcess();
    private readonly bool enabled;
    public BackgroundIo() => enabled = SetPriorityClass(process.Handle, 0x100000);
    public void Dispose() { if (enabled) SetPriorityClass(process.Handle, 0x200000); process.Dispose(); }
}
