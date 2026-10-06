using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RamFlow.Core;
using Forms = System.Windows.Forms;

namespace RamFlow.UI;

internal sealed class App : Application
{
    private readonly bool _smoke;
    private readonly string? _screenshot;
    private IResourceProvider? _provider;
    private Engine? _engine;
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _menu;
    private System.Drawing.Icon? _icon;
    private Forms.ToolStripMenuItem? _pausedItem;
    private Forms.ToolStripMenuItem? _dryRunItem;
    private Forms.ToolStripMenuItem[] _modeItems = [];
    private DispatcherTimer? _smokeTimer;
    private int _cleaned;
    private bool _cleanupFailed;

    internal App(bool smoke, string? screenshot)
    {
        _smoke = smoke;
        _screenshot = screenshot;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, e) =>
        {
            Console.Error.WriteLine(e.Exception);
            if (!_smoke)
                MessageBox.Show("오류가 발생하여 RamFlow를 종료합니다.\n" + e.Exception.Message,
                    "RamFlow", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
            ExitApplication(1);
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UiTheme.Install(this);
        _provider = _smoke ? new MockResourceProvider() : new WindowsResourceProvider();
        _engine = new Engine(_provider, dataRoot: null, persist: !_smoke);
        if (!_smoke) _engine.ApplySettings(_engine.Settings with { DryRun = true });
        _window = new MainWindow(_engine, () => ExitApplication(0), _smoke, RestartElevated) { Icon = BrandAssets.Logo };
        _window.SourceInitialized += (_, _) => BrandAssets.ApplyWindowFrame(_window);
        MainWindow = _window;
        _window.SettingsChanged += (_, _) => UpdateTray();
        _engine.Start();
        _window.Show();
        _window.RefreshState();
        if (_smoke)
        {
            _smokeTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            {
                Interval = TimeSpan.FromSeconds(1.2)
            };
            _smokeTimer.Tick += SmokeFinished;
            _smokeTimer.Start();
        }
        else CreateTray();
        SessionEnding += (_, _) => Cleanup();
    }

    private void CreateTray()
    {
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("RamFlow 열기", null, (_, _) => Dispatch(() => _window?.ShowFromTray()));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _pausedItem = new Forms.ToolStripMenuItem("일시 정지", null, (_, _) =>
            Dispatch(() => ApplyTray(s => s with { Paused = !s.Paused })));
        _dryRunItem = new Forms.ToolStripMenuItem("시뮬레이션 (DryRun)", null, (_, _) =>
            Dispatch(() => ApplyTray(s => s with { DryRun = !s.DryRun })));
        _menu.Items.Add(_pausedItem);
        _menu.Items.Add(_dryRunItem);
        var modes = new Forms.ToolStripMenuItem("작업 모드");
        string[] labels = ["자동", "개발", "게임", "AI · 모델", "브라우저", "사무", "미디어", "배터리", "백그라운드"];
        _modeItems = new Forms.ToolStripMenuItem[Display.Modes.Length];
        for (int i = 0; i < Display.Modes.Length; i++)
        {
            string mode = Display.Modes[i];
            var item = new Forms.ToolStripMenuItem(i < labels.Length ? labels[i] : mode,
                null, (_, _) => Dispatch(() => ApplyTray(s => s with { Mode = mode }))) { Tag = mode };
            _modeItems[i] = item;
            modes.DropDownItems.Add(item);
        }
        _menu.Items.Add(modes);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("종료 · 원래 상태 복원", null, (_, _) => Dispatch(() => ExitApplication(0)));
        _icon = BrandAssets.CreateTrayIcon();
        _tray = new Forms.NotifyIcon
        {
            Text = "RamFlow · 시뮬레이션",
            Icon = _icon,
            ContextMenuStrip = _menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => Dispatch(() => _window?.ShowFromTray());
        UpdateTray();
    }

    private void Dispatch(Action action)
    {
        if (Volatile.Read(ref _cleaned) == 0 && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(action);
    }

    private void ApplyTray(Func<Settings, Settings> change)
    {
        if (_engine is null || _smoke) return;
        Settings settings = change(_engine.Settings);
        if (!settings.DryRun && !UiPrivilege.IsAdministrator)
        {
            _window?.ShowFromTray();
            MessageBox.Show(_window!, "실제 적용에는 관리자 권한이 필요합니다. 설정 화면에서 '관리자로 다시 열기'를 선택해 주세요.",
                "관리자 권한 필요", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!settings.DryRun)
        {
            _window?.ShowFromTray();
            if (MessageBox.Show(_window!, "시뮬레이션이 꺼진 상태로 설정을 적용합니다.\n프로세스의 자원 우선순위가 실제로 변경될 수 있습니다.\n계속하시겠습니까?",
                "실제 적용 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
        }
        try { _engine.ApplySettings(settings); }
        catch (Exception error) { MessageBox.Show(_window!, error.Message, "설정 적용 실패", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _window?.RefreshState();
        UpdateTray();
    }

    private void RestartElevated()
    {
        if (_smoke || UiPrivilege.IsAdministrator) return;
        try
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("현재 실행 파일 경로를 찾을 수 없습니다.");
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            // Support both the packaged apphost and development launches through dotnet.exe.
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(typeof(Program).Assembly.Location);
            info.ArgumentList.Add("--restart-from");
            info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            using var child = Process.Start(info);
            if (child is null) throw new InvalidOperationException("관리자 권한으로 새 창을 시작하지 못했습니다.");
            ExitApplication(0);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { /* UAC cancelled; keep this UI running. */ }
        catch (Exception error)
        {
            MessageBox.Show(_window!, "관리자로 다시 열지 못했습니다.\n" + error.Message, "RamFlow",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateTray()
    {
        if (_engine is null || _tray is null) return;
        Settings settings = _engine.Settings;
        _pausedItem!.Checked = settings.Paused;
        _dryRunItem!.Checked = settings.DryRun;
        foreach (var item in _modeItems) item.Checked = (string?)item.Tag == settings.Mode;
        _tray.Text = "RamFlow · " + (settings.Paused ? "일시 정지" : settings.DryRun ? "시뮬레이션" : "실제 적용");
    }

    private void SmokeFinished(object? sender, EventArgs e)
    {
        _smokeTimer?.Stop();
        int code = 0;
        try
        {
            _window!.RefreshState();
            if (!_window.LastRefreshSucceeded)
                throw new InvalidOperationException("Smoke test 상태 조회에 실패했습니다.");
            if (_window.Icon != BrandAssets.Logo || BrandAssets.Logo.PixelWidth < 256)
                throw new InvalidOperationException("RamFlow 창 아이콘이 로드되지 않았습니다.");
            using (var trayIcon = BrandAssets.CreateTrayIcon())
                if (trayIcon.Width != 32 || trayIcon.Height != 32) throw new InvalidOperationException("트레이 아이콘 크기 오류");
            _window.UpdateLayout();
            _window.VerifyPages(_screenshot);
            if (_provider is MockResourceProvider mock && (mock.Writes != 0 || mock.Trims != 0))
                throw new InvalidOperationException("Smoke test 중 자원 변경이 감지되었습니다.");
            if (_screenshot is not null)
            {
                string path = Path.GetFullPath(_screenshot);
                string? directory = Path.GetDirectoryName(path);
                if (directory is not null) Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(_window.ActualWidth),
                    (int)Math.Ceiling(_window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(_window);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(path);
                png.Save(output);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
        finally { ExitApplication(code); }
    }

    private void ExitApplication(int code)
    {
        Cleanup();
        if (_cleanupFailed) code = 1;
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        Shutdown(code);
    }

    internal void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) != 0) return;
        Release(() => _smokeTimer?.Stop());
        if (_smokeTimer is not null) _smokeTimer.Tick -= SmokeFinished;
        Release(() => _window?.StopRefresh());
        Release(() => { if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); } });
        Release(() => _menu?.Dispose());
        Release(() => _icon?.Dispose());
        try
        {
            // Engine owns its worker, cancellation source, and provider after construction.
            if (_engine is not null) _engine.Dispose();
            else _provider?.Dispose();
        }
        catch (Exception ex)
        {
            _cleanupFailed = true;
            Console.Error.WriteLine("Engine cleanup: " + ex);
            Release(() => _provider?.Dispose());
        }
    }

    private void Release(Action release)
    {
        try { release(); }
        catch (Exception ex) { _cleanupFailed = true; Console.Error.WriteLine("UI cleanup: " + ex); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }
}
