using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RamFlow.Core;
using RamFlow.Storage;

namespace RamFlow.UI;

internal static class FeaturePages
{
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RamFlowNative");
    public static void Add(TabControl tabs, Engine engine)
    {
        tabs.Items.Add(Storage(engine)); tabs.Items.Add(PagefilePage(engine)); tabs.Items.Add(BenchmarkPage(engine)); tabs.Items.Add(UpdatePage());
    }
    private static StackPanel Panel() => new() { Margin = new Thickness(24), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left };
    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8), Foreground = UiTheme.Text };
    private static TextBox Input(string value = "") => new() { Text = value, MinWidth = 350, Margin = new Thickness(0, 4, 0, 8) };
    private static TabItem Tab(string title, StackPanel panel) => new() { Header = title, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
    private static void Button(StackPanel panel, string title, Func<Task> action)
    {
        var button = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 6), Padding = new Thickness(16, 9, 16, 9) };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } catch (Exception e) { MessageBox.Show(e.Message, "RamFlow", MessageBoxButton.OK, MessageBoxImage.Error); } finally { button.IsEnabled = true; } };
        panel.Children.Add(button);
    }
    private static bool Confirm(string text) => MessageBox.Show(text, "작업 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    private static string? Folder() { var picker = new OpenFolderDialog(); return picker.ShowDialog() == true ? picker.FolderName : null; }
    private static TabItem Storage(Engine engine)
    {
        var panel = Panel(); panel.Children.Add(Text("모델 · 파일 콜드 저장소"));
        panel.Children.Add(Text("선택한 폴더의 모델·데이터·프로젝트·아카이브를 분석합니다. 동기화 폴더의 클라우드 업로드 완료는 확인할 수 없으므로 원본 삭제는 별도로 확인합니다. 로컬에 없는 보관 모델도 메타데이터로 표시합니다."));
        var source = Input(); var target = Input();
        panel.Children.Add(Text("원본 폴더")); panel.Children.Add(source); Button(panel, "원본 폴더 선택", () => { source.Text = Folder() ?? source.Text; return Task.CompletedTask; });
        var backend = new ComboBox { ItemsSource = new[] { "연결된 클라우드/보관 폴더", "HTTPS WebDAV" }, SelectedIndex = 0, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(backend); panel.Children.Add(Text("보관 폴더 또는 WebDAV 컬렉션 URL")); panel.Children.Add(target);
        Button(panel, "보관 폴더 선택", () => { target.Text = Folder() ?? target.Text; return Task.CompletedTask; });
        var username = Input(); var password = new PasswordBox { MinWidth = 350, Margin = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(Text("WebDAV 사용자 / 암호 (파일에 저장하지 않음)")); panel.Children.Add(username); panel.Children.Add(password);
        var table = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true, Height = 270, Margin = new Thickness(0, 12, 0, 12), SelectionMode = DataGridSelectionMode.Single };
        var status = Text("분석은 읽기 전용입니다. Dry Run 상태에서는 보관·삭제·복원·실행을 시뮬레이션합니다.");
        ColdStorageService? service = null; IDisposable? remote = null;
        void Connect()
        {
            string original = Path.GetFullPath(source.Text).TrimEnd(Path.DirectorySeparatorChar);
            if (original == Path.GetPathRoot(original)!.TrimEnd(Path.DirectorySeparatorChar)) throw new ArgumentException("모델·데이터 폴더를 선택하세요. 드라이브 전체는 선택할 수 없습니다.");
            foreach (string folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) }) {
                if (folder.Length > 0 && (original.Equals(folder, StringComparison.OrdinalIgnoreCase) || original.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Windows·설치·시스템 데이터 폴더는 보관 대상으로 선택할 수 없습니다.");
            }
            remote?.Dispose(); remote = null;
            IColdStorageBackend destination;
            if (backend.SelectedIndex == 1) {
                var web = new WebDavColdStorageBackend(new Uri(target.Text), "webdav:" + target.Text.TrimEnd('/'), new NetworkCredential(username.Text, password.Password)); destination = web; remote = web;
            } else destination = new FolderColdStorageBackend(target.Text, "folder:" + Path.GetFullPath(target.Text).ToLowerInvariant());
            service = new ColdStorageService(source.Text, Path.Combine(Root, "manifests"), destination);
        }
        Button(panel, "HOT / WARM / COLD 분석", async () => { Connect(); table.ItemsSource = await service!.ScanAsync(); status.Text = "분석 완료 · 파일 수정 시각 기반 추정"; });
        panel.Children.Add(table); panel.Children.Add(status);
        StorageScanEntry Selected() => table.SelectedItem as StorageScanEntry ?? throw new ArgumentException("목록에서 파일을 선택하세요.");
        ColdStorageService Connected() => service ?? throw new ArgumentException("먼저 분석하세요.");
        Button(panel, "선택 파일 보관 · 원본 유지", async () => {
            var item = Selected(); var manager = Connected(); bool dry = engine.Settings.DryRun;
            if (!dry && !Confirm($"보관 대상: {item.Path}\n크기: {Display.Bytes(item.Size)}\n저장소: {manager.BackendId}\n전송 및 SHA-256 검증 후 원본은 유지합니다.")) return;
            status.Text = "전송/해시 검증 중…";
            var result = await manager.ArchiveAsync(item.Path, new() { DryRun = dry, Confirmed = true });
            status.Text = result.DryRun ? "[Dry Run] 보관 예상" : "보관/검증 완료 · 원본 유지"; table.ItemsSource = await manager.ScanAsync();
        });
        Button(panel, "검증한 원본 공간 회수", async () => {
            var item = Selected(); var manager = Connected(); Guid id = item.ManifestId ?? throw new ArgumentException("먼저 파일을 보관하세요."); bool dry = engine.Settings.DryRun;
            string warning = manager.LocalRemovalWarning ?? "원격 전송 응답과 전체 SHA-256 검증을 확인했습니다.";
            if (!dry && !Confirm($"원본 제거: {item.Path}\n{warning}\n복구할 보관 파일과 업로드 완료를 직접 확인했습니까?")) return;
            var result = await manager.RemoveLocalAsync(id, new() { DryRun = dry, Confirmed = true, AllowUnconfirmedRemoteUploadRemoval = manager.LocalRemovalWarning is not null });
            status.Text = result.DryRun ? "[Dry Run] 공간 회수 예상" : "원본 제거 완료 · 메타데이터 유지"; table.ItemsSource = await manager.ScanAsync();
        });
        Button(panel, "선택 모델/파일 복원", async () => {
            var item = Selected(); var manager = Connected(); Guid id = item.ManifestId ?? throw new ArgumentException("보관 기록이 없습니다."); bool dry = engine.Settings.DryRun;
            if (!dry && !Confirm($"복원: {item.Path}\n전체 다운로드·해시 검증 후 원래 경로에 복원합니다.")) return;
            status.Text = "복원/해시 검증 중…";
            var result = await manager.EnsureRestoredAsync(id, new() { DryRun = dry, Confirmed = true }); status.Text = result.DryRun ? "[Dry Run] 복원 예상" : "복원 완료"; table.ItemsSource = await manager.ScanAsync();
        });
        Button(panel, "중단된 보관 기록 복구", async () => {
            var item = Selected(); var manager = Connected(); Guid id = item.ManifestId ?? throw new ArgumentException("보관 기록을 선택하세요."); bool dry = engine.Settings.DryRun;
            if (!dry && !Confirm("중단된 전송/제거 기록을 검증하고 복구할까요? 원본은 자동 삭제하지 않습니다.\n" + item.Path)) return;
            await manager.RecoverAsync(id, new() { DryRun = dry, Confirmed = true }); status.Text = dry ? "[Dry Run] 기록 복구 예상" : "기록 복구 완료"; table.ItemsSource = await manager.ScanAsync();
        });
        var executable = Input(); var arguments = Input(); panel.Children.Add(Text("모델 실행 프로그램 / 인수 JSON 배열 (예: [\"-m\"])")); panel.Children.Add(executable); panel.Children.Add(arguments);
        Button(panel, "실행 프로그램 선택", () => { var picker = new OpenFileDialog { Filter = "프로그램|*.exe" }; if (picker.ShowDialog() == true) executable.Text = picker.FileName; return Task.CompletedTask; });
        Button(panel, "모델 자동 복원 후 실행", async () => {
            var item = Selected(); var manager = Connected(); Guid id = item.ManifestId ?? throw new ArgumentException("보관된 모델을 선택하세요."); bool dry = engine.Settings.DryRun;
            if (!dry && !Confirm($"모델: {item.Path}\n프로그램: {executable.Text}\n필요한 모델을 복원한 뒤 실행합니다.")) return;
            string[] args = string.IsNullOrWhiteSpace(arguments.Text) ? [] : System.Text.Json.JsonSerializer.Deserialize<string[]>(arguments.Text) ?? [];
            var result = await manager.LaunchModelAsync(id, new() { DryRun = dry, Confirmed = true, ExecutablePath = executable.Text, Arguments = args }); status.Text = result.DryRun ? "[Dry Run] 모델 복원/실행 예상" : "모델 실행됨 · PID " + result.ProcessId;
        });
        return Tab("모델 저장소", panel);
    }
    private static TabItem PagefilePage(Engine engine)
    {
        var panel = Panel(); panel.Children.Add(Text("Pagefile · 권장 · 백업 · 복원"));
        var modes = new ComboBox { ItemsSource = new[] { "windows", "balanced", "lowram", "developer", "gaming", "ai", "custom" }, SelectedIndex = 0 };
        panel.Children.Add(modes); var model = Input("0"); panel.Children.Add(Text("모델 크기 GB")); panel.Children.Add(model);
        var initial = Input("2048"); var maximum = Input("4096"); var drive = Input("C");
        panel.Children.Add(Text("대상 드라이브 문자 / 초기 MB / 최대 MB")); panel.Children.Add(drive); panel.Children.Add(initial); panel.Children.Add(maximum);
        var output = Text("변경은 관리자 권한과 확인이 필요하며 재부팅 후 반영될 수 있습니다."); panel.Children.Add(output);
        Button(panel, "현재 설정 조회", async () => { output.Text = System.Text.Json.JsonSerializer.Serialize(await Core.Pagefile.ReadAsync()); });
        Button(panel, "관측 수요 기반 권장", () => { var advice = Core.Pagefile.Recommend(engine.GetState().Snapshot, (string)modes.SelectedItem, double.Parse(model.Text, System.Globalization.CultureInfo.InvariantCulture)); initial.Text = advice.InitialMb.ToString(); maximum.Text = advice.MaximumMb.ToString(); output.Text = advice.Reason; return Task.CompletedTask; });
        string backup = Path.Combine(Root, "pagefile-original.json");
        Button(panel, "권장/수동 설정 적용", async () => {
            bool dry = engine.Settings.DryRun; string letter = drive.Text.ToUpperInvariant();
            if (letter.Length != 1 || letter[0] < 'A' || letter[0] > 'Z') throw new ArgumentException("드라이브 문자를 입력하세요.");
            bool automatic = (string)modes.SelectedItem == "windows";
            var desired = new PagefileConfiguration(automatic, automatic ? [] : [new(letter + ":\\pagefile.sys", uint.Parse(initial.Text), uint.Parse(maximum.Text))]); Core.Pagefile.Validate(desired);
            if (!automatic && new DriveInfo(letter + ":\\").AvailableFreeSpace < ((long)desired.Entries[0].MaximumSize + 2048) * 1048576) throw new IOException("최대 Pagefile과 추가 2GB를 위한 디스크 여유가 부족합니다.");
            if (!dry && !Confirm("기존 Pagefile 배치를 교체합니다. 최초 설정은 백업합니다. 재부팅이 필요할 수 있습니다.\n" + System.Text.Json.JsonSerializer.Serialize(desired))) return;
            await Core.Pagefile.ApplyAsync(desired, backup, dry); output.Text = dry ? "[Dry Run] Pagefile 적용 예상" : "적용 완료 · 재부팅 후 확인하세요.";
        });
        Button(panel, "최초 설정 복원", async () => { bool dry = engine.Settings.DryRun; if (!dry && !Confirm("최초 Pagefile 백업을 복원할까요?")) return; await Core.Pagefile.RestoreAsync(backup, dry); output.Text = dry ? "[Dry Run] 복원 예상" : "복원 완료"; });
        return Tab("Pagefile", panel);
    }
    private static TabItem BenchmarkPage(Engine engine)
    {
        var panel = Panel(); panel.Children.Add(Text("벤치마크 · 측정값을 JSON/CSV로 저장"));
        panel.Children.Add(Text("기본 측정은 3초 동안 시스템 변화를 관측합니다. 실행 파일을 선택하면 시작/종료 시간을 추가합니다. 벤치마크는 최적화를 강제로 발생시키거나 사용자 작업을 종료하지 않습니다."));
        var executable = Input(); panel.Children.Add(executable); Button(panel, "측정할 실행 파일 선택", () => { var picker = new OpenFileDialog { Filter = "프로그램|*.exe" }; if (picker.ShowDialog() == true) executable.Text = picker.FileName; return Task.CompletedTask; });
        var output = Text(""); panel.Children.Add(output);
        var column = Input("0"); panel.Children.Add(Text("CSV 측정 열 번호 (첫 열 = 0)")); panel.Children.Add(column);
        Button(panel, "측정 시작", async () => {
            if (executable.Text.Length > 0 && !Confirm("측정 프로그램을 실행합니다:\n" + executable.Text)) return;
            output.Text = "측정 중…"; var result = await Core.Benchmark.RunAsync(engine, executable.Text.Length == 0 ? null : executable.Text);
            var picker = new SaveFileDialog { FileName = "RamFlow-benchmark", Filter = "JSON/CSV 보고서|*.report" }; if (picker.ShowDialog() == true) Core.Benchmark.Export(result, picker.FileName);
            output.Text = $"가용 RAM {Display.Bytes(result.Before.Available)} → {Display.Bytes(result.After.Available)}\n시작(ms): {result.ApplicationLaunchMs?.ToString("F1") ?? "미측정"}\n작업 종료(ms): {result.WorkloadDurationMs?.ToString("F1") ?? "미측정"}\n스케줄링 지터: {result.SchedulingJitterMs:F2} ms\n" + string.Join("\n", result.Notes);
        });
        Button(panel, "Frame Time / tokens/sec CSV 가져오기", () => { var picker = new OpenFileDialog { Filter = "CSV|*.csv" }; if (picker.ShowDialog() == true) { var stats = Core.Benchmark.ImportMetricCsv(picker.FileName, int.Parse(column.Text)); output.Text = $"{stats.Samples}개 · 평균 {stats.Mean:F3} · P95 {stats.P95:F3}\n선택한 숫자 열을 사용합니다. 단위는 원본 런타임 기준입니다."; } return Task.CompletedTask; });
        return Tab("벤치마크", panel);
    }
    private static TabItem UpdatePage()
    {
        var panel = Panel(); panel.Children.Add(Text("설치 · 업데이트"));
        panel.Children.Add(Text("현재 버전 " + Updates.CurrentVersion + " · .NET 10 · Windows x64\n업데이트 확인은 GitHub 공식 저장소에만 접속합니다. 실행파일을 다운로드한 뒤 SHA-256을 확인하고, 설치는 사용자가 직접 시작합니다."));
        var output = Text(""); panel.Children.Add(output); ReleaseInfo? release = null;
        Button(panel, "최신 릴리스 확인", async () => { release = await Updates.CheckAsync(); output.Text = $"최신 공개 버전: {release.Version}\n{release.Page}"; });
        Button(panel, "검증된 ZIP 다운로드", async () => { release ??= await Updates.CheckAsync(); string? folder = Folder(); if (folder is null) return; output.Text = "다운로드/검증 중…"; output.Text = await Updates.DownloadAsync(release, folder); });
        Button(panel, "새 버전 검증 후 설치 · 다시 시작", async () => {
            if (Core.Pagefile.IsAdministrator()) throw new InvalidOperationException("일반 권한으로 RamFlow를 다시 열어 업데이트하세요. 업데이트가 관리자 권한을 이어받지 않게 합니다.");
            release ??= await Updates.CheckAsync();
            if (!Confirm($"새 버전 {release.Version}을 다운로드·SHA-256 검증한 뒤 설치하고 다시 시작할까요?\n현재 자원 설정은 종료 전에 복원합니다.")) return;
            output.Text = "업데이트 다운로드/검증 중…";
            string source = await Updates.StageAsync(release, Path.Combine(Root, "updates"));
            var info = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(source, "Install.ps1"), "-WaitPid", Environment.ProcessId.ToString(), "-Launch" }) info.ArgumentList.Add(arg);
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "RamFlow-install.json"))) { info.ArgumentList.Add("-TargetDirectory"); info.ArgumentList.Add(AppContext.BaseDirectory); }
            System.Diagnostics.Process.Start(info)?.Dispose(); System.Windows.Application.Current.Shutdown();
        });
        Button(panel, "현재 버전 사용자 설치", () => {
            if (!Confirm("현재 버전을 사용자 Programs 폴더에 설치하고 시작 메뉴에 등록할까요? 시스템 서비스는 별도 선택입니다.")) return Task.CompletedTask;
            string script = Path.Combine(AppContext.BaseDirectory, "Install.ps1");
            if (!File.Exists(script)) throw new IOException("포터블 배포 폴더의 Install.ps1을 실행하세요.");
            var info = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }) info.ArgumentList.Add(arg);
            System.Diagnostics.Process.Start(info)?.Dispose(); output.Text = "사용자 설치를 시작했습니다. 설치 후 시작 메뉴에서 RamFlow를 여세요."; return Task.CompletedTask;
        });
        panel.Children.Add(Text("업데이트 ZIP을 풀고 Install.ps1로 설치하세요. Uninstall.ps1은 실행 중인 엔진과 시작 메뉴·자동 시작을 제거하며, 사용자의 보관 모델/클라우드 파일은 유지합니다."));
        return Tab("설치/업데이트", panel);
    }
}
