using System.Diagnostics;

namespace RamFlow.Core;

public sealed record BackgroundJobRequest
{
    public string Executable { get; init; } = "";
    public string[] Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public int EstimatedMemoryMiB { get; init; } = 512;
}
public sealed record BackgroundJobSummary(Guid Id, string Label, string Status, int? Pid, int? ExitCode, string Message);
public interface IBackgroundJobProcess : IDisposable
{
    int Id { get; } long Identity { get; } bool HasExited { get; } int ExitCode { get; } ulong PrivateBytes { get; }
}
public interface IBackgroundJobLauncher { IBackgroundJobProcess Start(BackgroundJobRequest request); }
public sealed class WindowsBackgroundJobLauncher : IBackgroundJobLauncher
{
    public IBackgroundJobProcess Start(BackgroundJobRequest request) {
        var info = new ProcessStartInfo(request.Executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in request.Arguments) info.ArgumentList.Add(argument);
        if (request.WorkingDirectory is string directory) info.WorkingDirectory = directory;
        return new Child(Process.Start(info) ?? throw new IOException("작업을 시작하지 못했습니다."));
    }
    private sealed class Child : IBackgroundJobProcess {
        readonly Process process; public long Identity { get; }
        public Child(Process value) { process=value; try { Identity=process.StartTime.ToFileTimeUtc(); } catch { Identity=0; } }
        public int Id=>process.Id; public bool HasExited=>process.HasExited; public int ExitCode=>process.ExitCode;
        public ulong PrivateBytes { get { try { process.Refresh(); return (ulong)process.PrivateMemorySize64; } catch { return 0; } } }
        public void Dispose()=>process.Dispose(); // Never terminate work on manager shutdown.
    }
}
public sealed class BackgroundJobQueue : IDisposable
{
    private sealed class Entry(BackgroundJobRequest request) {
        public Guid Id=Guid.NewGuid(); public BackgroundJobRequest Request=request; public string Status="Waiting", Message="메모리 여유와 실행 슬롯 대기";
        public IBackgroundJobProcess? Process; public int? Exit;
        public BackgroundJobSummary Summary=>new(Id,Path.GetFileName(Request.Executable),Status,Process?.Id,Exit,Message);
    }
    readonly IBackgroundJobLauncher launcher; readonly List<Entry> entries=[];
    bool stopped;
    public BackgroundJobQueue(IBackgroundJobLauncher? launcher=null) { this.launcher=launcher ?? new WindowsBackgroundJobLauncher(); }
    public IReadOnlyList<BackgroundJobSummary> Summaries=>entries.Select(x=>x.Summary).ToArray();
    public bool Owns(ProcessSample p)=>entries.Any(x=>x.Status=="Running" && x.Process is {} child && child.Id==p.Pid && child.Identity==p.Identity);
    public static void Validate(BackgroundJobRequest request) {
        if (string.IsNullOrWhiteSpace(request.Executable) || !Path.IsPathFullyQualified(request.Executable) || request.Executable.IndexOf('\0')>=0 || request.Executable.Length>32767 ||
            request.Arguments is null || request.Arguments.Length>64 || request.Arguments.Any(x=>x is null || x.IndexOf('\0')>=0) || request.Arguments.Sum(x=>(long)x.Length+3)+request.Executable.Length>30000 ||
            request.EstimatedMemoryMiB is <1 or >65536 || request.WorkingDirectory is {} dir && (!Path.IsPathFullyQualified(dir) || dir.IndexOf('\0')>=0))
            throw new ArgumentException("전체 실행 파일 경로, 인수 배열 및 메모리 예상량을 확인하세요.");
    }
    public BackgroundJobSummary Enqueue(BackgroundJobRequest request) {
        Validate(request);
        if (stopped) throw new InvalidOperationException("작업 큐가 중지되어 있습니다.");
        if (entries.Count(x=>x.Status=="Waiting")>=64) throw new InvalidOperationException("대기 작업은 최대 64개입니다.");
        while(entries.Count>=128) { var old=entries.FirstOrDefault(x=>x.Status is not ("Waiting" or "Running")); if(old is null) break; old.Process?.Dispose(); entries.Remove(old); }
        var entry=new Entry(request with { Arguments=request.Arguments.ToArray() }); entries.Add(entry); return entry.Summary;
    }
    public bool Cancel(Guid id) {
        var entry=entries.FirstOrDefault(x=>x.Id==id && x.Status=="Waiting"); if(entry is null) return false;
        entry.Status="Cancelled";entry.Message="대기 작업 취소";return true;
    }
    public void Tick(Snapshot snapshot,Settings settings) {
        foreach(var entry in entries.Where(x=>x.Status=="Running")) {
            try { if(!entry.Process!.HasExited) continue; entry.Exit=entry.Process.ExitCode; entry.Status=entry.Exit==0?"Completed":"Failed";entry.Message="종료 코드 "+entry.Exit; }
            catch(Exception e) { entry.Message="종료 확인 대기: "+e.Message; }
        }
        if(stopped || settings.DryRun || settings.Paused) return;
        ulong reserved=0;
        foreach(var entry in entries.Where(x=>x.Status=="Running")) {
            ulong estimate=(ulong)entry.Request.EstimatedMemoryMiB*1048576;
            ulong actual=entry.Process!.PrivateBytes; if(estimate>actual) reserved+=estimate-actual;
        }
        ulong available=snapshot.Available>reserved?snapshot.Available-reserved:0;
        double commit=snapshot.Commit+(double)reserved;
        while(entries.Count(x=>x.Status=="Running")<settings.BackgroundConcurrency) {
            var next=entries.FirstOrDefault(x=>x.Status=="Waiting"); if(next is null) break;
            ulong estimate=(ulong)next.Request.EstimatedMemoryMiB*1048576;
            if(available<estimate+(ulong)settings.BackgroundReserveMiB*1048576 || snapshot.CommitLimit==0 || commit+estimate>.85*snapshot.CommitLimit) break;
            try { next.Process=launcher.Start(next.Request);next.Status="Running";next.Message="배경 실행 중 · 완료가 늦어질 수 있음";available-=estimate;commit+=estimate; }
            catch(Exception e) { next.Status="Failed";next.Message=e.Message.Length>500?e.Message[..500]:e.Message; }
        }
    }
    public void Start()=>stopped=false;
    public void Stop() { stopped=true;foreach(var entry in entries.Where(x=>x.Status=="Waiting")){entry.Status="Cancelled";entry.Message="관리 종료로 대기 취소";} }
    public void Dispose(){Stop();foreach(var entry in entries)entry.Process?.Dispose();}
}
