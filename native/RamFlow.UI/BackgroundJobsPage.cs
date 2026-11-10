using System;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using RamFlow.Core;

namespace RamFlow.UI;
public sealed partial class MainWindow
{
    private readonly TextBox _backgroundConcurrency = Input("1"), _backgroundReserve = Input("1024");
    private readonly DataGrid _jobsTable = Table();
    private TabItem? _jobsTab;
    private UIElement BackgroundJobsPage()
    {
        var page = PageStack();
        page.Children.Add(Label("배경 작업 실행",24));
        page.Children.Add(Label("양보 가능한 작업을 대기열에 넣습니다. 메모리 여유와 실행 슬롯이 확보되면 시작하며, 전경 작업을 위해 완료가 늦어질 수 있습니다.",13,UiTheme.Muted));
        page.Children.Add(Label("동시 실행 수와 남겨둘 메모리(MiB) · 설정 저장으로 적용",14));
        page.Children.Add(_backgroundConcurrency); page.Children.Add(_backgroundReserve);
        AutomationProperties.SetName(_backgroundConcurrency,"배경 작업 최대 동시 실행 수");
        AutomationProperties.SetName(_backgroundReserve,"남겨둘 메모리 MiB");
        var executable=Input(""); var arguments=Input("[]"); var estimate=Input("512"); var directory=Input("");
        var message=Label("실제 관리 모드와 관리자 권한이 필요합니다. 예상 메모리는 할당 한도가 아닌 시작 여부의 기준입니다.",13,UiTheme.Muted);
        page.Children.Add(Label("실행 파일",14)); page.Children.Add(executable);
        page.Children.Add(Command("실행 파일 선택",()=>{var picker=new Microsoft.Win32.OpenFileDialog{Filter="실행 파일|*.exe"};if(picker.ShowDialog(this)==true)executable.Text=picker.FileName;}));
        page.Children.Add(Label("인수 · JSON 문자열 배열 (예: [\"--threads\", \"2\"])",14));page.Children.Add(arguments);
        page.Children.Add(Label("작업 폴더 · 비우면 현재 폴더",14));page.Children.Add(directory);
        page.Children.Add(Label("예상 최대 메모리(MiB) · 자식 작업 포함",14));page.Children.Add(estimate);
        var submit=Command("대기열에 추가",()=>{
            try {
                if(!UiPrivilege.IsAdministrator)throw new InvalidOperationException("설정에서 관리자로 다시 열어 주세요.");
                if(!int.TryParse(estimate.Text,out int memory))throw new ArgumentException("예상 메모리를 정수로 입력하세요.");
                var job=new BackgroundJobRequest{Executable=executable.Text.Trim(),Arguments=JsonSerializer.Deserialize<string[]>(arguments.Text) ?? throw new ArgumentException("인수 배열이 필요합니다."),EstimatedMemoryMiB=memory,WorkingDirectory=string.IsNullOrWhiteSpace(directory.Text)?null:directory.Text.Trim()};
                BackgroundJobQueue.Validate(job);
                if(MessageBox.Show(this,"선택한 실행 파일을 배경 작업으로 실행합니다.\n대기열에 추가하시겠습니까?","배경 작업 실행",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
                var queued=_engine.EnqueueBackgroundJob(job);message.Text="대기열에 추가: "+queued.Label;RefreshState();
            }catch(Exception e){message.Text=e.Message;}
        });submit.IsEnabled=!_smokeTest;page.Children.Add(submit);
        var cancel=Command("선택한 대기 작업 취소",()=>{try{if(_jobsTable.SelectedItem is BackgroundJobSummary item){message.Text=_engine.CancelBackgroundJob(item.Id)?"대기 취소 완료":"이미 실행 중이거나 종료된 작업입니다. 실행 중인 작업은 종료하지 않습니다.";RefreshState();}}catch(Exception e){message.Text=e.Message;}});
        cancel.IsEnabled=!_smokeTest;page.Children.Add(cancel);page.Children.Add(message);
        Column(_jobsTable,"작업",nameof(BackgroundJobSummary.Label),190);Column(_jobsTable,"상태",nameof(BackgroundJobSummary.Status),100);Column(_jobsTable,"PID",nameof(BackgroundJobSummary.Pid),80);Column(_jobsTable,"설명",nameof(BackgroundJobSummary.Message),380);
        _jobsTable.Height=230;page.Children.Add(_jobsTable);return Scroll(page);
    }
}
