using System.Diagnostics;
using System.Drawing;
using RamFlow.Core;
namespace RamFlow.Tray;
static class Program
{
    [STAThread] static int Main(string[] args) {
        string? ready=null,data=null;
        for(int i=0;i<args.Length;i++) {
            if(args[i]=="--ready-event" && i+1<args.Length && UiHandoff.ValidReadyName(args[i+1]))ready=args[++i];
            else if(args[i]=="--data-root" && i+1<args.Length)data=Path.GetFullPath(args[++i]);
            else if(args[i]=="--ipc-namespace" && i+1<args.Length && Guid.TryParseExact(args[++i],"N",out var scope) && scope!=Guid.Empty)EnginePipe.ConfigureVerificationNamespace(scope);
            else return 2;
        }
        if(data is not null && EnginePipe.VerificationNamespace is null)return 2;
        using var mutex=new Mutex(false,@"Local\RamFlow.Tray."+EnginePipe.Name);
        bool owns;try{owns=mutex.WaitOne(3000);}catch(AbandonedMutexException){owns=true;}
        if(!owns)return 1;
        try { ApplicationConfiguration.Initialize();using var context=new TrayContext(data);UiHandoff.SignalReady(ready);Application.Run(context);return 0; }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally{mutex.ReleaseMutex();}
    }
    sealed class TrayContext : ApplicationContext
    {
        readonly NotifyIcon tray; readonly ContextMenuStrip menu=new(); readonly Icon icon;
        readonly System.Windows.Forms.Timer timer=new(){Interval=2000}; readonly EventWaitHandle dismiss;
        readonly string? data; bool opening;
        public TrayContext(string? data) {
            this.data=data;
            using var stream=typeof(Program).Assembly.GetManifestResourceStream("RamFlow.Tray.ico")!;icon=new Icon(stream,32,32);
            tray=new(){Icon=icon,Text="RamFlow · 배경 관리",ContextMenuStrip=menu,Visible=true};
            menu.Items.Add("RamFlow 열기",null,(_,_)=>OpenUi());
            menu.Items.Add("일시정지 / 재개",null,(_,_)=>Change(s=>s with{Paused=!s.Paused}));
            menu.Items.Add("관찰 모드로 전환",null,(_,_)=>Change(s=>s with{DryRun=true}));
            menu.Items.Add(new ToolStripSeparator());menu.Items.Add("종료 · 원래 상태 복원",null,(_,_)=>Stop());
            tray.DoubleClick+=(_,_)=>OpenUi();
            dismiss=new EventWaitHandle(false,EventResetMode.AutoReset,UiHandoff.DismissName);
            timer.Tick+=(_,_)=>{if(dismiss.WaitOne(0)){ExitThread();return;}try{var s=EnginePipe.Request<Settings>(new("settings"),500);tray.Text="RamFlow · "+(s.Paused?"일시정지":s.DryRun?"관찰 모드":"배경 관리");}catch{tray.Text="RamFlow · 엔진 연결 확인 필요";}};timer.Start();
        }
        void Change(Func<Settings,Settings> change){try{EnginePipe.Request<Settings>(new("apply",change(EnginePipe.Request<Settings>(new("settings")))));}catch(Exception e){tray.ShowBalloonTip(4000,"설정 변경 실패",e.Message,ToolTipIcon.Warning);}}
        async void OpenUi() {
            if(opening)return;opening=true;
            try {
                string name=UiHandoff.NewReadyName();using var ready=new EventWaitHandle(false,EventResetMode.ManualReset,name);
                var info=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"RamFlow.exe")){UseShellExecute=false};
                info.ArgumentList.Add("--ready-event");info.ArgumentList.Add(name);
                if(data is not null){info.ArgumentList.Add("--data-root");info.ArgumentList.Add(data);}
                if(EnginePipe.VerificationNamespace is {} scope){info.ArgumentList.Add("--ipc-namespace");info.ArgumentList.Add(scope);}
                using var child=Process.Start(info) ?? throw new IOException("화면 실행 실패");
                if(await Task.Run(()=>ready.WaitOne(8000)))ExitThread();else tray.ShowBalloonTip(4000,"화면 준비 대기","트레이를 유지합니다. 다시 열어 주세요.",ToolTipIcon.Info);
            }catch(Exception e){tray.ShowBalloonTip(4000,"화면 실행 실패",e.Message,ToolTipIcon.Warning);}
            finally{opening=false;}
        }
        void Stop(){try{EnginePipe.Request<bool>(new("stop"));ExitThread();}catch(Exception e){tray.ShowBalloonTip(4000,"복원 확인 필요",e.Message,ToolTipIcon.Warning);}}
        protected override void Dispose(bool disposing){if(disposing){timer.Stop();timer.Dispose();tray.Visible=false;tray.Dispose();menu.Dispose();icon.Dispose();dismiss.Dispose();}base.Dispose(disposing);}
    }
}
