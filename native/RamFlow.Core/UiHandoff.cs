namespace RamFlow.Core;
public static class UiHandoff
{
    private const string Prefix = @"Local\RamFlow.Ready.";
    public static string NewReadyName()=>Prefix+Guid.NewGuid().ToString("N");
    public static bool ValidReadyName(string name)=>name.StartsWith(Prefix,StringComparison.Ordinal) && Guid.TryParseExact(name[Prefix.Length..],"N",out var id) && id!=Guid.Empty;
    public static string DismissName=>@"Local\RamFlow.Tray.Dismiss."+EnginePipe.Name;
    public static void SignalReady(string? name) {
        if(name is null)return;
        if(!ValidReadyName(name))throw new ArgumentException("잘못된 준비 신호 이름입니다.");
        if(EventWaitHandle.TryOpenExisting(name,out var ready))using(ready)ready.Set();
    }
    public static void DismissTray(){if(EventWaitHandle.TryOpenExisting(DismissName,out var existing))using(existing)existing.Set();}
}
