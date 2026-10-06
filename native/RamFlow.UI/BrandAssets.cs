using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RamFlow.UI;

internal static class BrandAssets
{
    public static BitmapSource Logo { get; } = LoadLogo();

    private static BitmapSource LoadLogo()
    {
        var logo = new BitmapImage();
        logo.BeginInit();
        logo.CacheOption = BitmapCacheOption.OnLoad;
        logo.UriSource = new Uri("pack://application:,,,/Assets/ramflow.png", UriKind.Absolute);
        logo.EndInit();
        logo.Freeze();
        return logo;
    }

    public static System.Drawing.Icon CreateTrayIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ramflow.ico", UriKind.Absolute))
            ?? throw new IOException("RamFlow icon resource is missing.");
        using var stream = resource.Stream;
        using var icon = new System.Drawing.Icon(stream, 32, 32);
        return (System.Drawing.Icon)icon.Clone();
    }

    public static void ApplyWindowFrame(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || SystemParameters.HighContrast) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        // Official DWM attributes affect this window only; unsupported preferences retain the native frame.
        // https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
        int dark = 1, rounded = 2, caption = 0x0020120B, text = 0x00FBF5ED;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
