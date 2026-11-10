using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RamFlow.Core;
namespace RamFlow.UI;
internal sealed partial class App
{
    private async void MoveToLightweightTray()
    {
        if(_handoffBusy || _engine?.IsRemote!=true)return;
        string tray=Path.Combine(AppContext.BaseDirectory,"RamFlow.Tray.exe");
        if(!File.Exists(tray))return;
        _handoffBusy=true;
        try {
            string name=UiHandoff.NewReadyName();
            using var ready=new EventWaitHandle(false,EventResetMode.ManualReset,name);
            var info=new ProcessStartInfo(tray){UseShellExecute=false,CreateNoWindow=true};
            info.ArgumentList.Add("--ready-event");info.ArgumentList.Add(name);
            if(_dataRoot is not null){info.ArgumentList.Add("--data-root");info.ArgumentList.Add(_dataRoot);}
            if(EnginePipe.VerificationNamespace is {} scope){info.ArgumentList.Add("--ipc-namespace");info.ArgumentList.Add(scope);}
            using var child=Process.Start(info) ?? throw new IOException("트레이 프로세스를 시작하지 못했습니다.");
            if(!await Task.Run(()=>ready.WaitOne(8000)))throw new IOException("트레이 준비를 확인하지 못했습니다. 화면을 유지합니다.");
            _detachEngine=true;ExitApplication(0);
        }catch(Exception e){_window?.ShowFromTray();MessageBox.Show(_window!,e.Message,"트레이 전환 실패",MessageBoxButton.OK,MessageBoxImage.Warning);}
        finally{_handoffBusy=false;}
    }
}
