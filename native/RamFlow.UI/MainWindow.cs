using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using RamFlow.Core;

namespace RamFlow.UI;

public sealed class MainWindow : Window
{
    private const double GiB = 1073741824d;
    private readonly Engine _engine;
    private readonly Action _exit;
    private readonly Action? _restartElevated;
    private readonly bool _smokeTest;
    private readonly DispatcherTimer _timer;
    private readonly TabControl _tabs = new();
    private readonly TextBlock _status = Label("상태를 읽는 중", 13);
    private readonly TextBlock _footer = Label("측정 대기", 12, UiTheme.Muted);
    private readonly TextBlock _ram = Label("—", 26);
    private readonly TextBlock _cpu = Label("—", 26);
    private readonly TextBlock _disk = Label("—", 26);
    private readonly TextBlock _commit = Label("—", 26);
    private readonly ProgressBar _ramMeter = Meter(UiTheme.Accent);
    private readonly ProgressBar _cpuMeter = Meter(UiTheme.Good);
    private readonly ProgressBar _diskMeter = Meter(UiTheme.Warning);
    private readonly ProgressBar _commitMeter = Meter(UiTheme.Danger);
    private readonly TextBlock _memoryDetail = Label("", 13, UiTheme.Muted);
    private readonly TextBlock _insight = Label("분석 대기", 14);
    private readonly TextBlock _aiResult = Label("메모리 샘플을 기다리는 중", 16);
    private readonly HistoryChart _ramChart = new("RAM 사용률", UiTheme.Accent);
    private readonly HistoryChart _cpuChart = new("CPU 사용률", UiTheme.Good);
    private readonly HistoryChart _diskChart = new("디스크 활성 시간", UiTheme.Warning);
    private readonly HistoryChart _commitChart = new("커밋 사용률", UiTheme.Danger);
    private readonly DataGrid _processes = Table();
    private readonly DataGrid _groups = Table();
    private readonly DataGrid _logs = Table();
    private readonly TextBox _filter = Input("");
    private readonly TextBox _aiSize = Input("4");
    private readonly TextBox _whitelist = Input("");
    private readonly CheckBox _dryRun = Check("DryRun · 변경 없이 분석만 수행");
    private readonly CheckBox _paused = Check("Paused · 자동 관리 일시정지");
    private readonly CheckBox _cpuManagement = Check("CPU 관리");
    private readonly CheckBox _ecoQos = Check("EcoQoS · 백그라운드 전력 절약");
    private readonly CheckBox _cpuSets = Check("CPU Sets · 코어 배치 (기본 꺼짐)");
    private readonly CheckBox _learning = Check("학습 · 사용 패턴 분석");
    private readonly CheckBox _trim = Check("Trim · 작업 집합 정리");
    private readonly ComboBox _mode = new() { MinWidth = 230, Margin = new Thickness(0, 8, 0, 16) };
    private readonly TextBlock _settingsMessage = Label("", 13, UiTheme.Muted);
    private readonly Button _pauseButton;
    private TabItem _settingsTab = null!;
    private State? _state;
    private Settings _loadedSettings = new();

    public bool AllowClose { get; set; }
    public bool LastRefreshSucceeded { get; private set; }
    public event EventHandler? SettingsChanged;

    public MainWindow(Engine engine, Action exit, bool smokeTest = false, Action? restartElevated = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _restartElevated = restartElevated;
        _smokeTest = smokeTest;
        Title = "RamFlow · 리소스 컨트롤 센터";
        Width = 1200;
        Height = 800;
        MinWidth = 900;
        MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Background = UiTheme.Background;
        Foreground = UiTheme.Text;
        FontFamily = new FontFamily("Malgun Gothic");
        FontSize = 13;

        var shell = new DockPanel { Margin = new Thickness(20, 16, 20, 14) };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 14), MinHeight = 46 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var logo = new Image { Source = BrandAssets.Logo, Width = 44, Height = 44, Margin = new Thickness(0, 0, 12, 0) };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(logo, "RamFlow 로고");
        brand.Children.Add(logo);
        var brandText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        TextBlock brandName = Label("RamFlow", 23);
        brandName.FontWeight = FontWeights.SemiBold;
        brandName.FontFamily = new FontFamily("Segoe UI");
        brandName.Margin = new Thickness(0, 0, 0, 3);
        brandText.Children.Add(brandName);
        TextBlock tagline = Label("내 PC의 리소스를 한눈에", 11, UiTheme.Muted);
        tagline.Margin = new Thickness(0);
        brandText.Children.Add(tagline);
        brand.Children.Add(brandText);
        header.Children.Add(brand);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Command("새로고침", RefreshState));
        _pauseButton = Command("일시정지", TogglePause);
        _pauseButton.IsEnabled = !smokeTest;
        actions.Children.Add(_pauseButton);
        actions.Children.Add(Command("설정", () => _tabs.SelectedItem = _settingsTab));
        actions.Children.Add(Command("트레이로", Hide));
        actions.Children.Add(Command("종료", RequestExit));
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        DockPanel.SetDock(header, Dock.Top);
        shell.Children.Add(header);

        _status.FontSize = 11;
        _status.TextWrapping = TextWrapping.NoWrap;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Margin = new Thickness(0);
        _status.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = _status });
        var statusContent = new DockPanel { LastChildFill = true };
        if (smokeTest)
        {
            TextBlock smokeLabel = Label("SMOKE · 읽기 전용", 10, UiTheme.Warning);
            smokeLabel.Margin = new Thickness(14, 0, 0, 0);
            DockPanel.SetDock(smokeLabel, Dock.Right);
            statusContent.Children.Add(smokeLabel);
        }
        statusContent.Children.Add(_status);
        var statusBar = new Border
        {
            Child = statusContent, Background = UiTheme.Surface, BorderBrush = UiTheme.Border,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 0, 14)
        };
        DockPanel.SetDock(statusBar, Dock.Top);
        shell.Children.Add(statusBar);
        _footer.FontSize = 10;
        _footer.TextWrapping = TextWrapping.NoWrap;
        _footer.TextTrimming = TextTrimming.CharacterEllipsis;
        _footer.Margin = new Thickness(194, 10, 0, 0);
        DockPanel.SetDock(_footer, Dock.Bottom);
        shell.Children.Add(_footer);
        _tabs.Background = UiTheme.Background;
        _tabs.BorderBrush = UiTheme.Border;
        _tabs.Foreground = UiTheme.Text;
        _tabs.Items.Add(Tab("대시보드", Dashboard()));
        _tabs.Items.Add(Tab("프로세스", ProcessesPage()));
        _tabs.Items.Add(Tab("AI 실행 점검", AiPage()));
        _settingsTab = Tab("설정", SettingsPage());
        _tabs.Items.Add(_settingsTab);
        _tabs.Items.Add(Tab("활동 로그", LogsPage()));
        FeaturePages.Add(_tabs, engine);
        if (smokeTest) foreach (TabItem feature in _tabs.Items.Cast<TabItem>().Skip(5)) if (feature.Content is UIElement element) element.IsEnabled = false;
        ConfigureNavigation();
        shell.Children.Add(_tabs);
        Content = shell;

        var refresh = new RoutedCommand();
        InputBindings.Add(new KeyBinding(refresh, Key.F5, ModifierKeys.None));
        CommandBindings.Add(new CommandBinding(refresh, (_, _) => RefreshState()));
        var settings = new RoutedCommand();
        InputBindings.Add(new KeyBinding(settings, Key.OemComma, ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(settings, (_, _) => _tabs.SelectedItem = _settingsTab));
        var quit = new RoutedCommand();
        InputBindings.Add(new KeyBinding(quit, Key.Q, ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(quit, (_, _) => RequestExit()));

        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += OnTick;
        Loaded += (_, _) => { RefreshState(); _timer.Start(); };
        Closing += OnClosing;
        Closed += (_, _) => StopRefresh();
        LoadSettings();
    }

    public void RefreshState()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(RefreshState); return; }
        try
        {
            State state = _engine.GetState();
            _state = state;
            Snapshot s = state.Snapshot;
            double ramPercent = Percent(s.RamTotal > s.Available ? s.RamTotal - s.Available : 0, s.RamTotal);
            double commitPercent = Percent(s.Commit, s.CommitLimit);
            _ram.Text = s.RamTotal > 0 ? $"{ramPercent:0.0}%" : "측정 대기";
            _cpu.Text = Number(s.CpuPercent);
            _disk.Text = s.DiskPercent.HasValue ? Number(s.DiskPercent.Value) : "미지원";
            _commit.Text = s.CommitLimit > 0 ? $"{commitPercent:0.0}%" : "측정 대기";
            _ramMeter.Value = ramPercent;
            _cpuMeter.Value = double.IsFinite(s.CpuPercent) ? Math.Clamp(s.CpuPercent, 0, 100) : 0;
            _diskMeter.Value = s.DiskPercent is double disk && double.IsFinite(disk) ? Math.Clamp(disk, 0, 100) : 0;
            _diskMeter.Opacity = s.DiskPercent.HasValue ? 1 : 0.3;
            _commitMeter.Value = commitPercent;
            _ramChart.AddSample(s.Time, s.RamTotal > 0 ? ramPercent : null);
            _cpuChart.AddSample(s.Time, s.RamTotal > 0 ? s.CpuPercent : null);
            _diskChart.AddSample(s.Time, s.DiskPercent);
            _commitChart.AddSample(s.Time, s.CommitLimit > 0 ? commitPercent : null);
            _memoryDetail.Text = $"사용 가능 {Display.Bytes(s.Available)} · 총 {Display.Bytes(s.RamTotal)} · 커밋 {Display.Bytes(s.Commit)} / {Display.Bytes(s.CommitLimit)}\n" +
                $"캐시 {Display.Bytes(s.Cache)} · 대기 {Display.Bytes(s.Standby)} · 수정 {Display.Bytes(s.Modified)} · 압축 {(s.CompressedBytes is double compressed ? Display.Bytes(compressed) : "미측정")}\n" +
                $"GPU 사용 {(s.GpuUsedBytes is double gpu ? Display.Bytes(gpu) : "미지원")} · 온도 {(s.ThermalCelsius is double thermal ? thermal.ToString("F1") + "°C" : "미지원")} · 배터리 {(s.BatteryPercent is int battery ? battery + "%" : "미지원")}\n" +
                $"CPU Sets {state.CpuTopology.Count}개 · 효율 클래스 {state.CpuTopology.Select(x => x.Efficiency).Distinct().Count()}개 · Page-in {s.PagesInput?.ToString("F0") ?? "미측정"} pages/sec";
            Settings current = _engine.Settings;
            string running = current.Paused ? "일시정지" : "모니터링 중";
            _status.Text = $"{running}   ·   {(current.DryRun ? "시뮬레이션" : "실제 적용")}   ·   " +
                $"메모리 {Pressure(state.Level)} ({state.PressureScore:0.0}) · 시스템 압박 {state.SystemPressure:0.0}   ·   {ModeLabel(state.Mode)}";
            _status.Foreground = state.Level >= PressureLevel.High ? UiTheme.Warning : UiTheme.Good;
            _pauseButton.Content = current.Paused ? "관리 재개" : "일시정지";
            _insight.Text = $"현재 작업: {(string.IsNullOrWhiteSpace(state.Foreground) ? "확인 중" : state.Foreground)}\n" +
                $"보호된 프로세스 {state.Processes.Count(p => !string.IsNullOrEmpty(p.ProtectedReason))}개   ·   복원 대기 {state.PendingRestores}개\n" +
                (state.Predictions.Count > 0 ? string.Join("\n", state.Predictions.Take(5)) : "예측 결과가 아직 없습니다.");
            UpdateTables();
            _logs.ItemsSource = state.Events.OrderByDescending(e => e.Time).Select(e => new LogView(
                e.Time.ToLocalTime().ToString("MM-dd HH:mm:ss"), e.DryRun ? "시뮬레이션" : "기록", e.Message)).ToArray();
            UpdateAi();
            double age = (DateTimeOffset.UtcNow - s.Time).TotalSeconds;
            _footer.Text = $"샘플 {s.Time.ToLocalTime():HH:mm:ss}   ·   2초마다 상태 조회   ·   F5 새로고침 / Ctrl+, 설정 / Ctrl+Q 종료" +
                (age > 10 ? "   ·   샘플 갱신 지연" : "");
            _footer.Foreground = age > 10 ? UiTheme.Warning : UiTheme.Muted;
            LastRefreshSucceeded = true;
        }
        catch (Exception error)
        {
            LastRefreshSucceeded = false;
            _footer.Text = $"상태 조회 실패: {error.Message}";
            _footer.Foreground = UiTheme.Danger;
        }
    }

    public void StopRefresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(StopRefresh); return; }
        _timer.Stop();
    }
    internal void VerifyPages(string? screenshot)
    {
        if (!_smokeTest) throw new InvalidOperationException("Smoke test 전용입니다.");
        if (screenshot is not null) System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(screenshot))!);
        for (int index = 0; index < _tabs.Items.Count; index++) {
            _tabs.SelectedIndex = index; UpdateLayout();
            if (_tabs.SelectedItem is not TabItem item || item.Content is not UIElement content || content.RenderSize.Width <= 0) throw new InvalidOperationException("페이지 렌더링 실패");
            if (screenshot is null) continue;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = System.IO.File.Create(System.IO.Path.ChangeExtension(screenshot, $"page-{index}.png")); png.Save(stream);
        }
        _tabs.SelectedIndex = 0; UpdateLayout();
        double originalWidth = Width, originalHeight = Height;
        try
        {
            Width = MinWidth; Height = MinHeight; UpdateLayout();
            for (int index = 0; index < _tabs.Items.Count; index++)
            {
                _tabs.SelectedIndex = index; UpdateLayout();
                if (_tabs.SelectedItem is not TabItem item || item.Content is not UIElement content || content.RenderSize.Width <= 0)
                    throw new InvalidOperationException("최소 크기에서 페이지 렌더링 실패");
            }
            _tabs.SelectedIndex = 0; UpdateLayout();
            if (screenshot is not null)
            {
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(this);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = System.IO.File.Create(System.IO.Path.ChangeExtension(screenshot, "minimum.png"));
                png.Save(stream);
            }
        }
        finally { Width = originalWidth; Height = originalHeight; _tabs.SelectedIndex = 0; UpdateLayout(); }
    }

    public void ShowFromTray()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(ShowFromTray); return; }
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        RefreshState();
    }

    private UIElement Dashboard()
    {
        var page = new StackPanel { Margin = new Thickness(0, 0, 2, 0) };
        var heading = new DockPanel { Margin = new Thickness(4, 0, 4, 10) };
        TextBlock period = Label("최근 6분 · 2초 간격", 11, UiTheme.Muted);
        period.VerticalAlignment = VerticalAlignment.Center;
        period.Margin = new Thickness(14, 0, 0, 0);
        DockPanel.SetDock(period, Dock.Right);
        heading.Children.Add(period);
        TextBlock title = Label("리소스 개요", 22);
        title.FontWeight = FontWeights.SemiBold;
        title.Margin = new Thickness(0);
        heading.Children.Add(title);
        page.Children.Add(heading);
        var metrics = new UniformGrid { Columns = 4 };
        metrics.Children.Add(Metric("RAM", _ram, "물리 메모리 사용률", _ramMeter, UiTheme.Accent));
        metrics.Children.Add(Metric("CPU", _cpu, "전체 프로세서 사용률", _cpuMeter, UiTheme.Good));
        metrics.Children.Add(Metric("디스크", _disk, "디스크 활성 시간", _diskMeter, UiTheme.Warning));
        metrics.Children.Add(Metric("커밋", _commit, "커밋 한도 대비 사용률", _commitMeter, UiTheme.Danger));
        page.Children.Add(metrics);
        var charts = new UniformGrid { Columns = 2 };
        foreach (HistoryChart chart in new[] { _ramChart, _cpuChart, _diskChart, _commitChart })
        {
            Border card = Card(chart);
            card.Padding = new Thickness(4);
            charts.Children.Add(card);
        }
        page.Children.Add(charts);
        _memoryDetail.FontSize = 11;
        _memoryDetail.LineHeight = 17;
        _insight.FontSize = 11;
        _insight.LineHeight = 17;
        var summaries = new UniformGrid { Columns = 2 };
        summaries.Children.Add(Summary("메모리 & 장치", _memoryDetail));
        summaries.Children.Add(Summary("작업 흐름", _insight));
        page.Children.Add(summaries);
        return Scroll(page);
    }

    private void ConfigureNavigation()
    {
        var navigation = (ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <SolidColorBrush x:Key="NavSurface" Color="NAV_SURFACE"/>
              <SolidColorBrush x:Key="NavLine" Color="NAV_LINE"/>
              <SolidColorBrush x:Key="NavAccent" Color="NAV_ACCENT"/>
              <SolidColorBrush x:Key="NavText" Color="NAV_TEXT"/>
              <SolidColorBrush x:Key="NavMuted" Color="NAV_MUTED"/>
              <Style x:Key="NavigationItem" TargetType="TabItem">
                <Setter Property="Foreground" Value="{StaticResource NavMuted}"/>
                <Setter Property="FontSize" Value="13"/>
                <Setter Property="Margin" Value="0,2"/>
                <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem">
                  <Border x:Name="Frame" Background="Transparent" CornerRadius="7" Padding="10,11" BorderThickness="1" BorderBrush="Transparent">
                    <Grid>
                      <Grid.ColumnDefinitions><ColumnDefinition Width="3"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                      <Border x:Name="Marker" Background="{StaticResource NavAccent}" CornerRadius="2" Height="18" Opacity="0"/>
                      <ContentPresenter Grid.Column="1" Margin="10,0,0,0" ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center"/>
                    </Grid>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsSelected" Value="True"><Setter Property="Foreground" Value="{StaticResource NavText}"/><Setter TargetName="Frame" Property="Background" Value="#163747"/><Setter TargetName="Marker" Property="Opacity" Value="1"/></Trigger>
                    <MultiTrigger><MultiTrigger.Conditions><Condition Property="IsMouseOver" Value="True"/><Condition Property="IsSelected" Value="False"/></MultiTrigger.Conditions><Setter TargetName="Frame" Property="Background" Value="#1B2D42"/></MultiTrigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{StaticResource NavAccent}"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style x:Key="NavigationShell" TargetType="TabControl">
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabControl">
                  <Grid>
                    <Grid.ColumnDefinitions><ColumnDefinition Width="176"/><ColumnDefinition Width="18"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                    <Border Background="{StaticResource NavSurface}" BorderBrush="{StaticResource NavLine}" BorderThickness="1" CornerRadius="12" Padding="7">
                      <DockPanel>
                        <TextBlock DockPanel.Dock="Top" Text="워크스페이스" FontSize="10" Foreground="{StaticResource NavMuted}" Margin="12,10,8,12"/>
                        <TextBlock DockPanel.Dock="Bottom" Text="Ctrl+Tab  페이지 전환" FontSize="10" Foreground="{StaticResource NavMuted}" Margin="12,12,8,8"/>
                        <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled" Focusable="False"><ItemsPresenter/></ScrollViewer>
                      </DockPanel>
                    </Border>
                    <ContentPresenter x:Name="PART_SelectedContentHost" Grid.Column="2" ContentSource="SelectedContent" SnapsToDevicePixels="{TemplateBinding SnapsToDevicePixels}"/>
                  </Grid>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
            </ResourceDictionary>
            """.Replace("NAV_SURFACE", UiTheme.Surface.Color.ToString())
                .Replace("NAV_LINE", UiTheme.Border.Color.ToString())
                .Replace("NAV_ACCENT", UiTheme.Accent.Color.ToString())
                .Replace("NAV_TEXT", UiTheme.Text.Color.ToString())
                .Replace("NAV_MUTED", UiTheme.Muted.Color.ToString()));
        _tabs.Resources.MergedDictionaries.Add(navigation);
        _tabs.Style = (Style)navigation["NavigationShell"];
        _tabs.ItemContainerStyle = (Style)navigation["NavigationItem"];
        _tabs.ItemsPanel = (ItemsPanelTemplate)XamlReader.Parse("""
            <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><StackPanel IsItemsHost="True"/></ItemsPanelTemplate>
            """);
        _tabs.TabStripPlacement = Dock.Left;
        _tabs.Padding = new Thickness(0);
        _tabs.BorderThickness = new Thickness(0);
        AutomationProperties.SetName(_tabs, "RamFlow 페이지 탐색");
    }

    private UIElement ProcessesPage()
    {
        var page = new DockPanel { Margin = new Thickness(12) };
        var search = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        search.Children.Add(Label("프로세스 검색 · 이름 / PID / 보호 사유", 13, UiTheme.Muted));
        AutomationProperties.SetName(_filter, "프로세스 검색: 이름, PID 또는 보호 사유");
        _filter.TextChanged += (_, _) => UpdateTables();
        search.Children.Add(_filter);
        DockPanel.SetDock(search, Dock.Top);
        page.Children.Add(search);
        Column(_processes, "PID", nameof(ProcessView.Pid), 80);
        Column(_processes, "프로세스", nameof(ProcessView.Name), 160);
        Column(_processes, "RAM", nameof(ProcessView.Ram), 95);
        Column(_processes, "전용 메모리", nameof(ProcessView.Private), 105);
        Column(_processes, "CPU", nameof(ProcessView.Cpu), 75);
        Column(_processes, "I/O /초", nameof(ProcessView.Io), 95);
        Column(_processes, "활동", nameof(ProcessView.Activity), 100);
        Column(_processes, "보호 사유", nameof(ProcessView.Protection), 200, true);
        Column(_groups, "프로세스 그룹", nameof(GroupView.Name), 160);
        Column(_groups, "개수", nameof(GroupView.Count), 60);
        Column(_groups, "RAM 합계", nameof(GroupView.Ram), 95);
        Column(_groups, "전용 합계", nameof(GroupView.Private), 100);
        Column(_groups, "CPU 합계", nameof(GroupView.Cpu), 80);
        Column(_groups, "I/O /초", nameof(GroupView.Io), 100);
        Column(_groups, "활동", nameof(GroupView.Activity), 95);
        Column(_groups, "보호 사유", nameof(GroupView.Protection), 180, true);
        var tabs = new TabControl { Background = UiTheme.Surface, BorderBrush = UiTheme.Border };
        tabs.Items.Add(Tab("개별 프로세스", _processes));
        tabs.Items.Add(Tab("이름별 그룹", _groups));
        page.Children.Add(tabs);
        return page;
    }

    private UIElement AiPage()
    {
        var page = PageStack();
        page.Children.Add(Label("AI 워크로드 메모리 점검", 24));
        page.Children.Add(Label("모델과 작업에 필요한 메모리 (GB)", 14, UiTheme.Muted));
        _aiSize.MaxWidth = 260;
        _aiSize.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(_aiSize, "AI 모델과 작업에 필요한 메모리 GB");
        _aiSize.TextChanged += (_, _) => UpdateAi();
        page.Children.Add(_aiSize);
        page.Children.Add(Card(_aiResult));
        page.Children.Add(Label("추정 필요량 = 입력 GB + 실행 오버헤드 1.25 GB\n" +
            "현재 Available과 남은 커밋 한도를 함께 비교합니다. GPU VRAM과 모델별 추가 비용은 별도이며, 실행 가능 여부를 보장하지 않습니다.",
            13, UiTheme.Muted));
        return Scroll(page);
    }

    private UIElement SettingsPage()
    {
        var page = PageStack();
        page.Children.Add(Label("관리 정책", 24));
        page.Children.Add(Label("변경한 설정은 저장 버튼을 누를 때 적용됩니다.", 13, UiTheme.Muted));
        page.Children.Add(Label("실제 리소스 관리 (DryRun 끄기)에는 관리자 권한이 필요합니다.\n" +
            "관리자 권한이 없다면 '관리자로 다시 열기'를 누른 뒤 Windows 권한 요청을 승인해 주세요.", 13, UiTheme.Warning));
        var options = new StackPanel();
        options.Children.Add(_dryRun);
        options.Children.Add(_paused);
        options.Children.Add(Label("워크로드", 14));
        foreach (string mode in Display.Modes) _mode.Items.Add(new ComboBoxItem { Content = ModeLabel(mode), Tag = mode });
        options.Children.Add(_mode);
        AutomationProperties.SetName(_mode, "워크로드 모드");
        options.Children.Add(_cpuManagement);
        options.Children.Add(_ecoQos);
        options.Children.Add(_cpuSets);
        options.Children.Add(_learning);
        options.Children.Add(_trim);
        _cpuSets.ToolTip = "기본값은 꺼짐입니다. 활성화하면 엔진이 CPU 코어 배치를 관리합니다.";
        options.Children.Add(Label("보호 목록 · 프로세스 이름을 한 줄에 하나씩 입력", 14));
        _whitelist.AcceptsReturn = true;
        _whitelist.AcceptsTab = false;
        AutomationProperties.SetName(_whitelist, "보호할 프로세스 이름: Enter로 줄 바꿈, Tab으로 다음 항목 이동");
        _whitelist.TextWrapping = TextWrapping.Wrap;
        _whitelist.Height = 110;
        _whitelist.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        options.Children.Add(_whitelist);
        options.IsEnabled = !_smokeTest;
        page.Children.Add(Card(options));
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        Button save = Command("설정 저장", SaveSettings);
        save.IsEnabled = !_smokeTest;
        actions.Children.Add(save);
        actions.Children.Add(Command("현재 설정 다시 읽기", LoadSettings));
        Button elevate = Command("관리자로 다시 열기", () =>
        {
            if (!_smokeTest && !UiPrivilege.IsAdministrator) _restartElevated?.Invoke();
        });
        elevate.IsEnabled = !_smokeTest && !UiPrivilege.IsAdministrator && _restartElevated != null;
        AutomationProperties.SetName(elevate, "관리자로 다시 열기");
        actions.Children.Add(elevate);
        page.Children.Add(actions);
        page.Children.Add(_settingsMessage);
        return Scroll(page);
    }

    private UIElement LogsPage()
    {
        Column(_logs, "시간", nameof(LogView.Time), 145);
        Column(_logs, "유형", nameof(LogView.Type), 105);
        Column(_logs, "내용", nameof(LogView.Message), 650, true);
        return _logs;
    }

    private void UpdateTables()
    {
        if (_state == null) return;
        string query = _filter.Text.Trim();
        ProcessRow[] rows = _state.Processes.Where(p => query.Length == 0 ||
            p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            p.Pid.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (p.ProtectedReason?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(p => p.WorkingSet).ToArray();
        _processes.ItemsSource = rows.Select(p => new ProcessView(p.Pid, p.Name, Display.Bytes(p.WorkingSet),
            Display.Bytes(p.PrivateBytes), Number(p.CpuPercent), Rate(p.IoBytesPerSecond),
            Activity(p.Activity), string.IsNullOrWhiteSpace(p.ProtectedReason) ? "—" : p.ProtectedReason)).ToArray();
        _groups.ItemsSource = rows.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(p => (double)p.WorkingSet))
            .Select(g => new GroupView(g.Key, g.Count(), Display.Bytes(g.Sum(p => (double)p.WorkingSet)),
                Display.Bytes(g.Sum(p => (double)p.PrivateBytes)), Number(g.Sum(p => p.CpuPercent)),
                Rate(g.Sum(p => p.IoBytesPerSecond)), string.Join(", ", g.Select(p => Activity(p.Activity)).Distinct()),
                ProtectionSummary(g))).ToArray();
    }

    private void UpdateAi()
    {
        string input = _aiSize.Text.Trim();
        bool valid = double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out double size) ||
            double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out size);
        if (!valid || !double.IsFinite(size) || size <= 0 || !double.IsFinite(size + 1.25))
        {
            _aiResult.Text = "0보다 큰 유효한 GB 값을 입력하세요.";
            _aiResult.Foreground = UiTheme.Warning;
            return;
        }
        Snapshot? s = _state?.Snapshot;
        if (s == null || s.RamTotal == 0 || s.CommitLimit == 0)
        {
            _aiResult.Text = "물리 메모리와 커밋 한도 샘플을 기다리는 중입니다.";
            _aiResult.Foreground = UiTheme.Muted;
            return;
        }
        double required = size + 1.25;
        double available = s.Available / GiB;
        double commitRoom = s.CommitLimit > s.Commit ? (s.CommitLimit - s.Commit) / GiB : 0;
        bool physicalFits = available >= required;
        bool commitFits = commitRoom >= required;
        string verdict = !commitFits ? "커밋 여유 부족 · 실행을 권장하지 않습니다" :
            !physicalFits ? "물리 메모리 부족 · 페이징과 속도 저하 가능" : "현재 메모리 기준 실행 여유가 있습니다";
        _aiResult.Foreground = !commitFits ? UiTheme.Danger : !physicalFits ? UiTheme.Warning : UiTheme.Good;
        _aiResult.Text = $"{verdict}\n\n" +
            $"추정 필요량 {required:0.00} GB = 입력 {size:0.00} GB + 오버헤드 1.25 GB\n" +
            $"사용 가능 (Available) {available:0.00} GB  ·  차이 {available - required:+0.00;-0.00;0.00} GB\n" +
            $"남은 커밋 {commitRoom:0.00} GB  ·  차이 {commitRoom - required:+0.00;-0.00;0.00} GB\n" +
            $"샘플 {s.Time.ToLocalTime():HH:mm:ss} · 실행 중 다른 앱의 사용량에 따라 달라집니다.";
    }

    private void LoadSettings()
    {
        Settings settings = _engine.Settings;
        _dryRun.IsChecked = settings.DryRun;
        _paused.IsChecked = settings.Paused;
        _cpuManagement.IsChecked = settings.CpuManagement;
        _ecoQos.IsChecked = settings.EcoQos;
        _cpuSets.IsChecked = settings.CpuSets;
        _learning.IsChecked = settings.Learning;
        _trim.IsChecked = settings.WorkingSetTrim;
        _mode.SelectedItem = _mode.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == settings.Mode);
        _whitelist.Text = string.Join(Environment.NewLine, settings.Whitelist);
        _loadedSettings = settings with { Whitelist = settings.Whitelist.ToArray() };
        _settingsMessage.Text = _smokeTest ? "Smoke test에서는 설정을 변경할 수 없습니다." : "현재 엔진 설정을 불러왔습니다.";
    }

    private void SaveSettings()
    {
        if (_smokeTest) return;
        Settings current = _engine.Settings;
        if (!SameSettings(current, _loadedSettings))
        {
            _settingsMessage.Text = "트레이 등에서 현재 설정이 변경되었습니다. 저장하지 않았습니다. '현재 설정 다시 읽기'를 누른 뒤 다시 편집해 주세요.";
            return;
        }
        var next = current with
        {
            DryRun = _dryRun.IsChecked == true,
            Paused = _paused.IsChecked == true,
            CpuManagement = _cpuManagement.IsChecked == true,
            EcoQos = _ecoQos.IsChecked == true,
            CpuSets = _cpuSets.IsChecked == true,
            Learning = _learning.IsChecked == true,
            WorkingSetTrim = _trim.IsChecked == true,
            Mode = (_mode.SelectedItem as ComboBoxItem)?.Tag as string ?? current.Mode,
            Whitelist = _whitelist.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
        ApplySettings(next);
    }

    private static bool SameSettings(Settings left, Settings right) =>
        left.DryRun == right.DryRun && left.Paused == right.Paused &&
        left.CpuManagement == right.CpuManagement && left.EcoQos == right.EcoQos &&
        left.CpuSets == right.CpuSets && left.Learning == right.Learning &&
        left.WorkingSetTrim == right.WorkingSetTrim &&
        string.Equals(left.Mode, right.Mode, StringComparison.Ordinal) &&
        left.Whitelist.SequenceEqual(right.Whitelist, StringComparer.Ordinal);

    private void ApplySettings(Settings next)
    {
        if (_smokeTest) return;
        if (!next.DryRun && !UiPrivilege.IsAdministrator)
        {
            _tabs.SelectedItem = _settingsTab;
            _settingsMessage.Text = "실제 설정 적용에는 관리자 권한이 필요합니다. 설정을 적용하지 않았습니다. '관리자로 다시 열기' 버튼을 이용해 주세요.";
            return;
        }
        if (!next.DryRun && MessageBox.Show(this,
            "DryRun이 꺼진 상태로 설정을 적용하면 엔진이 프로세스 우선순위, 전력 정책, CPU 배치 및 메모리 정리를 설정에 따라 실제 적용합니다.\n\n이 설정을 적용하시겠습니까?",
            "실제 리소스 관리 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            _settingsMessage.Text = "설정 적용을 취소했습니다. 엔진 설정은 변경되지 않았습니다.";
            return;
        }
        try { _engine.ApplySettings(next); }
        catch (Exception error)
        {
            _settingsMessage.Text = $"설정 적용 실패: {error.Message}";
            MessageBox.Show(this, _settingsMessage.Text, "설정 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        LoadSettings();
        _settingsMessage.Text = "설정을 저장하고 적용했습니다.";
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        RefreshState();
    }

    private void TogglePause()
    {
        if (!_smokeTest) ApplySettings(_engine.Settings with { Paused = !_engine.Settings.Paused });
    }

    private void RequestExit() => _exit();
    private void OnTick(object? sender, EventArgs e)
    {
        if (IsVisible && WindowState != WindowState.Minimized) RefreshState();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose) { StopRefresh(); return; }
        e.Cancel = true;
        WindowState = WindowState.Minimized;
    }

    private static string Number(double value) => double.IsFinite(value) ? $"{Math.Max(0, value):0.0}%" : "미지원";
    private static string Rate(double value) => double.IsFinite(value) && value >= 0 ? $"{Display.Bytes(value)}/s" : "미지원";
    private static double Percent(ulong value, ulong total) => total == 0 ? 0 : Math.Clamp(value / (double)total * 100, 0, 100);
    private static string Pressure(PressureLevel level) => level switch
    {
        PressureLevel.Normal => "정상", PressureLevel.Moderate => "보통", PressureLevel.High => "높음",
        PressureLevel.Severe => "매우 높음", PressureLevel.Critical => "위험", _ => level.ToString()
    };
    private static string ModeLabel(string mode) => mode switch
    {
        "auto" => "자동 감지", "developer" => "개발", "gaming" => "게임", "ai" => "AI / 모델 실행",
        "browser" => "웹 브라우징", "office" => "문서 / 업무", "media" => "미디어", "battery" => "배터리 절약",
        "background" => "백그라운드", _ => mode
    };
    private static string Activity(string activity) => activity.ToLowerInvariant() switch
    {
        "active" or "hot" => "활발", "warm" => "사용 중", "idle" => "유휴", "cold" => "낮음",
        "recently active" => "최근 사용", "deep idle" => "장기 유휴",
        "foreground" => "현재 작업", "background" => "백그라운드", "unknown" => "확인 불가", _ => activity
    };
    private static string ProtectionSummary(IGrouping<string, ProcessRow> group)
    {
        string reasons = string.Join(" / ", group.Select(p => p.ProtectedReason)
            .Where(r => !string.IsNullOrWhiteSpace(r)).Distinct());
        return reasons.Length > 0 ? reasons : "—";
    }

    private static TextBlock Label(string text, double size = 14, Brush? brush = null) => new()
    {
        Text = text, FontSize = size, Foreground = brush ?? UiTheme.Text, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 3, 0, 6)
    };
    private static TextBox Input(string text) => new()
    {
        Text = text, Background = UiTheme.Elevated, Foreground = UiTheme.Text, BorderBrush = UiTheme.Border,
        CaretBrush = UiTheme.Accent, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 8, 0, 12)
    };
    private static CheckBox Check(string text) => new()
    {
        Content = text, Foreground = UiTheme.Text, Margin = new Thickness(0, 7, 0, 10)
    };
    private static Button Command(string text, Action action)
    {
        var button = new Button
        {
            Content = text, Padding = new Thickness(13, 8, 13, 8), Margin = new Thickness(6, 0, 0, 0),
            Background = UiTheme.Elevated, Foreground = UiTheme.Text, BorderBrush = UiTheme.Border,
            Cursor = Cursors.Hand, MinHeight = 36
        };
        button.Click += (_, _) => action();
        return button;
    }
    private static StackPanel PageStack() => new() { Margin = new Thickness(14) };
    private static ScrollViewer Scroll(UIElement content) => new()
    { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private static TabItem Tab(string title, UIElement content) => new()
    { Header = title, Content = content, Padding = new Thickness(16, 10, 16, 10) };
    private static Border Card(UIElement content) => new()
    {
        Child = content, Background = UiTheme.Surface, BorderBrush = UiTheme.Border, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Margin = new Thickness(4, 4, 4, 8)
    };
    private static ProgressBar Meter(Brush color) => new()
    {
        Minimum = 0, Maximum = 100, Height = 3, Foreground = color,
        Background = UiTheme.Elevated, BorderThickness = new Thickness(0), IsHitTestVisible = false
    };
    private static Border Metric(string title, TextBlock value, string detail, ProgressBar meter, Brush color)
    {
        var stack = new StackPanel();
        TextBlock name = Label(title, 11, UiTheme.Muted);
        name.FontWeight = FontWeights.SemiBold;
        name.Margin = new Thickness(0);
        stack.Children.Add(name);
        value.Foreground = color;
        value.FontFamily = new FontFamily("Segoe UI");
        value.FontSize = 30;
        value.FontWeight = FontWeights.SemiBold;
        value.Margin = new Thickness(0, 2, 0, 2);
        stack.Children.Add(value);
        TextBlock description = Label(detail, 10, UiTheme.Muted);
        description.TextWrapping = TextWrapping.NoWrap;
        description.TextTrimming = TextTrimming.CharacterEllipsis;
        description.ToolTip = detail;
        description.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(description);
        stack.Children.Add(meter);
        Border card = Card(stack);
        card.Padding = new Thickness(12, 10, 12, 10);
        return card;
    }
    private static Border Summary(string title, TextBlock detail)
    {
        var stack = new DockPanel();
        TextBlock heading = Label(title, 12);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(heading, Dock.Top);
        stack.Children.Add(heading);
        detail.Margin = new Thickness(0);
        stack.Children.Add(new ScrollViewer
        {
            Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        Border card = Card(stack);
        card.Height = 112;
        card.Padding = new Thickness(12, 10, 12, 10);
        return card;
    }
    private static DataGrid Table()
    {
        var table = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
            CanUserReorderColumns = true, EnableRowVirtualization = true, EnableColumnVirtualization = true,
            Background = UiTheme.Surface, Foreground = UiTheme.Text, BorderBrush = UiTheme.Border,
            RowBackground = UiTheme.Surface, AlternatingRowBackground = UiTheme.Elevated,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = UiTheme.Border,
            HeadersVisibility = DataGridHeadersVisibility.Column, RowHeight = 38,
            SelectionMode = DataGridSelectionMode.Single, Margin = new Thickness(8)
        };
        var header = new Style(typeof(DataGridColumnHeader));
        header.Setters.Add(new Setter(Control.BackgroundProperty, UiTheme.Elevated));
        header.Setters.Add(new Setter(Control.ForegroundProperty, UiTheme.Muted));
        header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10)));
        header.Setters.Add(new Setter(Control.BorderBrushProperty, UiTheme.Border));
        table.ColumnHeaderStyle = header;
        return table;
    }
    private static void Column(DataGrid table, string title, string path, double width, bool stretch = false)
    {
        var textStyle = new Style(typeof(TextBlock));
        textStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(8, 0, 8, 0)));
        textStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        textStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(path)));
        table.Columns.Add(new DataGridTextColumn
        {
            Header = title, Binding = new Binding(path), ElementStyle = textStyle,
            Width = stretch ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
            MinWidth = width
        });
    }

    public sealed record ProcessView(int Pid, string Name, string Ram, string Private, string Cpu, string Io, string Activity, string Protection);
    public sealed record GroupView(string Name, int Count, string Ram, string Private, string Cpu, string Io, string Activity, string Protection);
    public sealed record LogView(string Time, string Type, string Message);
}
