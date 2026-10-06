using System.Runtime.InteropServices;
using RamFlow.Core;

internal static class ServiceHost
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Entry { public string? Name; public MainDelegate? Main; }
    [StructLayout(LayoutKind.Sequential)] private struct Status { public uint Type, State, Accepted, Win32Error, SpecificError, Checkpoint, WaitHint; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void MainDelegate(uint count, IntPtr arguments);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint HandlerDelegate(uint code, uint type, IntPtr data, IntPtr context);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartServiceCtrlDispatcher(Entry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, HandlerDelegate handler, IntPtr context);
    [DllImport("advapi32.dll")] private static extern bool SetServiceStatus(IntPtr handle, ref Status status);
    private static readonly MainDelegate MainCallback = ServiceMain;
    private static readonly HandlerDelegate HandlerCallback = Handler;
    private static readonly ManualResetEventSlim Stop = new();
    private static IntPtr handle;
    public static void Run() { if (!StartServiceCtrlDispatcher([new() { Name = "RamFlowMonitor", Main = MainCallback }, new()])) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
    private static uint Handler(uint code, uint type, IntPtr data, IntPtr context) { if (code is 1 or 5) { Report(3, 0); Stop.Set(); } return 0; }
    private static void Report(uint state, uint controls) { var status = new Status { Type = 0x10, State = state, Accepted = controls, WaitHint = state == 3 ? 10000U : 0 }; SetServiceStatus(handle, ref status); }
    private static void ServiceMain(uint count, IntPtr args)
    {
        handle = RegisterServiceCtrlHandlerEx("RamFlowMonitor", HandlerCallback, IntPtr.Zero); if (handle == IntPtr.Zero) return;
        try {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RamFlowMonitor");
            // Session 0에는 사용자의 포그라운드 데스크톱이 없다. SCM 서비스는 모니터만 담당한다.
            using var engine = new Engine(new WindowsResourceProvider(), root, persist: true, allowRemote: false);
            engine.ApplySettings(new() { Paused = true, DryRun = true }); engine.Start(); Report(4, 5);
            Stop.Wait(); engine.Stop();
        } catch { Report(1, 0); return; }
        Report(1, 0);
    }
}
