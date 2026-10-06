using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Interop;

namespace RamFlow.UI;

internal sealed class WindowActivation : IDisposable
{
    public static string Name { get; } = UserName();
    private readonly IntPtr _handle;
    private readonly string _name;
    private readonly uint _message;
    private readonly HwndSource _source;
    private readonly HwndSourceHook _hook;
    private bool _disposed;

    public WindowActivation(MainWindow window, string? name = null)
    {
        _name = name ?? Name;
        _handle = new WindowInteropHelper(window).Handle;
        _message = RegisterWindowMessage(_name);
        if (_handle == IntPtr.Zero || _message == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _source = HwndSource.FromHwnd(_handle) ?? throw new InvalidOperationException("Window source is missing.");
        _hook = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if ((uint)message != _message || wParam != IntPtr.Zero || lParam != IntPtr.Zero) return IntPtr.Zero;
            // The only permitted action is showing our own UI. No resource/settings commands cross this channel.
            window.Dispatcher.BeginInvoke(new Action(window.ShowFromTray));
            handled = true;
            return new IntPtr(1);
        };
        _source.AddHook(_hook);
        if (!SetProp(_handle, _name, new IntPtr(1))) { _source.RemoveHook(_hook); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        // Ordinary shortcuts may reopen an elevated UI; allow this one parameterless show message only.
        _ = ChangeWindowMessageFilterEx(_handle, _message, 1, IntPtr.Zero);
    }

    public static bool TryShowExisting(string? name = null, int milliseconds = 3000)
    {
        string channel = name ?? Name;
        uint message = RegisterWindowMessage(channel);
        if (message == 0) return false;
        var watch = Stopwatch.StartNew();
        do
        {
            bool shown = false;
            EnumWindows((handle, parameter) =>
            {
                if (GetProp(handle, channel) != new IntPtr(1)) return true;
                _ = GetWindowThreadProcessId(handle, out uint pid);
                _ = AllowSetForegroundWindow(pid);
                shown = SendMessageTimeout(handle, message, IntPtr.Zero, IntPtr.Zero, 0x22, 500, out var result) != IntPtr.Zero && result == new UIntPtr(1);
                return !shown;
            }, IntPtr.Zero);
            if (shown) return true;
            if (watch.ElapsedMilliseconds >= milliseconds) break;
            Thread.Sleep(50);
        } while (true);
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = RemoveProp(_handle, _name);
        if (!_source.IsDisposed) _source.RemoveHook(_hook);
    }

    private static string UserName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return "RamFlow.UI.Show." + (identity.User?.Value ?? Environment.UserName);
    }

    private delegate bool EnumerateWindow(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetProp(IntPtr window, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr RemoveProp(IntPtr window, string name);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumerateWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeFilter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
}
