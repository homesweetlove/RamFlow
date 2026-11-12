using ResearchLab;
using System.Diagnostics;

static class ObserverControlTests
{
    public static void Run(Action<bool,string> check) {
        string directory=Path.Combine(Path.GetTempPath(),"RamFlow.ObserverControl-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"phase.json");ObserverProtocol.WritePhase(path,new("idle",0));
        var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var release=Task.Run(()=>{Thread.Sleep(70);held.Dispose();});
        try { ObserverProtocol.WritePhase(path,new("observe",1)); }
        finally { release.GetAwaiter().GetResult(); }
        check(ObserverProtocol.ReadPhase(path)==new ObserverPhase("observe",1),"Transient Windows phase replacement contention was not retried");
        bool denied=false;var timer=Stopwatch.StartNew();
        using(var persistent=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            try { ObserverProtocol.WritePhase(path,new("evaluate",2)); }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException) {denied=true;}
        }
        check(denied && timer.Elapsed<TimeSpan.FromSeconds(3),"Permanent observer control denial did not fail within its bound");
        check(ObserverProtocol.ReadPhase(path)==new ObserverPhase("observe",1),"Failed replacement corrupted the previous phase");
        check(!Directory.GetFiles(directory,"*.tmp").Any(),"Failed phase replacement leaked temporary controls");
        Console.WriteLine("Observer control transient contention and bounded permanent-denial regressions passed.");
    }
}
