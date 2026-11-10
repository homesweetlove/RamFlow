using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using RamFlow.Core;

static class BackgroundJobsWindowsTests
{
    public static async Task RunAsync(Action<bool,string> check) {
        string root=Path.Combine(Path.GetTempPath(),"RamFlow.Queue-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string dotnet=Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),"..","..","..","dotnet.exe"));
        string assembly=Assembly.GetExecutingAssembly().Location;
        BackgroundJobRequest Request(string name)=>new(){Executable=dotnet,Arguments=[assembly,"--queue-echo",Path.Combine(root,name),"space argument","amp&literal","quote\"end"],EstimatedMemoryMiB=128};
        using var native=new WindowsResourceProvider();using var queue=new BackgroundJobQueue();
        var settings=new Settings{DryRun=false,BackgroundConcurrency=1};
        queue.Enqueue(Request("one.json"));queue.Enqueue(Request("two.json"));
        int maxRunning=0;var end=DateTime.UtcNow.AddSeconds(15);
        while(DateTime.UtcNow<end && queue.Summaries.Any(x=>x.Status is "Waiting" or "Running")) {
            queue.Tick(native.Sample(),settings);maxRunning=Math.Max(maxRunning,queue.Summaries.Count(x=>x.Status=="Running"));await Task.Delay(100);
        }
        check(queue.Summaries.All(x=>x.Status=="Completed") && maxRunning==1,"Real queued children did not complete within one slot");
        var echoed=JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(root,"one.json")))!;
        check(echoed.SequenceEqual(new[]{"space argument","amp&literal","quote\"end"}),"Argument array was shell-parsed or changed");
        using var detached=new BackgroundJobQueue();var running=detached.Enqueue(Request("continued.json"));detached.Tick(native.Sample(),settings);
        check(detached.Summaries[0].Status=="Running","Real shutdown fixture was not admitted");
        detached.Stop();await Task.Delay(1800);
        check(File.Exists(Path.Combine(root,"continued.json")),"Manager shutdown terminated a running child");
        Console.WriteLine("Native background queue children/arguments/shutdown passed.");
    }
}
