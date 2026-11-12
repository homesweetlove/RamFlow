using RamFlow.Core;

static class BackgroundJobsTests
{
    public static void Run(Action<bool,string> check) {
        var launcher = new Launcher(); using var queue = new BackgroundJobQueue(launcher);
        var request = new BackgroundJobRequest { Executable = @"D:\Apps\worker.exe", Arguments = ["a b", "literal&argument"], EstimatedMemoryMiB = 512 };
        var settings = new Settings { DryRun = false, BackgroundConcurrency = 2, BackgroundReserveMiB = 1024 };
        var memory = new Snapshot { Available = 1900 * 1048576UL, Commit = 4UL<<30, CommitLimit = 16UL<<30 };
        var first = queue.Enqueue(request); var second = queue.Enqueue(request);
        queue.Tick(memory,settings);
        check(launcher.Children.Count == 1,"Queue oversubscribed reserved startup memory");
        queue.Tick(memory,settings);
        check(launcher.Children.Count == 1,"Queue lost outstanding reservation on next sample");
        check(!queue.Cancel(first.Id),"Queue cancellation terminated running work");
        check(queue.Cancel(second.Id),"Waiting queue cancellation failed");
        launcher.Children[0].Finished = true; queue.Tick(memory,settings);
        check(queue.Summaries.Single(x=>x.Id==first.Id).Status == "Completed","Exited job not reaped");
        queue.Enqueue(request); queue.Tick(memory,settings with { DryRun = true });
        check(launcher.Children.Count==1,"Dry Run started queued work");
        queue.Tick(memory,settings with { Paused = true }); check(launcher.Children.Count==1,"Pause started queued work");
        queue.Tick(memory with { Commit = 14UL<<30 },settings); check(launcher.Children.Count==1,"Commit budget ignored");
        queue.Tick(memory with { Available = 800*1048576UL },settings); check(launcher.Children.Count==1,"Physical reserve ignored");
        queue.Tick(memory,settings); check(launcher.Children.Count==2,"Recovered budget did not admit waiting job");
        queue.Stop(); check(!launcher.Children[1].Finished,"Engine shutdown killed running work");
        bool rejected=false;try { queue.Enqueue(request with { Executable="worker.exe" }); } catch(ArgumentException){rejected=true;}
        check(rejected,"Relative queued executable accepted");
        foreach(int bad in new[]{0,17}) { rejected=false;try{Engine.Validate(settings with{BackgroundConcurrency=bad});}catch(ArgumentException){rejected=true;}check(rejected,"Queue concurrency validation failed"); }
        using var failed = new BackgroundJobQueue(new BrokenLauncher()); failed.Enqueue(request); failed.Tick(memory,settings);
        check(failed.Summaries[0].Status=="Failed","Launch failure not recorded");
        var brokenStatusLauncher=new Launcher();using var uncertain=new BackgroundJobQueue(brokenStatusLauncher);
        uncertain.Enqueue(request);uncertain.Tick(memory,settings with { BackgroundConcurrency=1 });
        brokenStatusLauncher.Children[0].Unknown=true;uncertain.Enqueue(request);uncertain.Tick(memory,settings with { BackgroundConcurrency=1 });
        check(brokenStatusLauncher.Children.Count==1 && uncertain.Summaries[0].Status=="Running","Unknown child exit status freed a running slot");
        var deferredLauncher = new Launcher(); using var deferred = new BackgroundJobQueue(deferredLauncher);
        var deferredJob = deferred.Enqueue(request); deferred.Tick(memory, settings, deferStarts: true);
        check(deferredLauncher.Children.Count == 0, "CARP admission guard launched a deferred job");
        deferred.Tick(memory, settings); check(deferredLauncher.Children.Count == 1, "Admission guard never released a waiting job");
        deferredLauncher.Children[0].Finished = true; deferred.Enqueue(request); deferred.Tick(memory, settings, deferStarts: true);
        check(deferred.Summaries.Single(x => x.Id == deferredJob.Id).Status == "Completed" && deferredLauncher.Children.Count == 1,
            "Admission deferral skipped completion accounting or launched new work");
        var blockedLauncher = new Launcher(); using var provider = new MockResourceProvider();
        using (var engine = new Engine(provider, persist: false, jobLauncher: blockedLauncher, audit: new BrokenAudit())) {
            engine.ApplySettings(new() { PolicyName = "carp", DryRun = false }); engine.EnqueueBackgroundJob(request); engine.Tick();
            check(blockedLauncher.Children.Count == 0, "Audit failure started a new queued research job");
        }
        Console.WriteLine("Background queue regressions passed.");
    }
    sealed class Launcher : IBackgroundJobLauncher {
        public List<Child> Children=[];
        public IBackgroundJobProcess Start(BackgroundJobRequest request) { var c=new Child(100+Children.Count);Children.Add(c);return c; }
    }
    sealed class BrokenLauncher : IBackgroundJobLauncher { public IBackgroundJobProcess Start(BackgroundJobRequest request)=>throw new IOException("test launch failure"); }
    sealed class BrokenAudit : IResearchAuditSink { public void Write(ResearchAuditEvent entry) => throw new IOException("test audit failure"); }
    sealed class Child(int pid) : IBackgroundJobProcess {
        public bool Finished,Unknown; public int Id=>pid; public long Identity=>pid; public bool HasExited=>Unknown?throw new IOException("temporary status failure"):Finished; public int ExitCode=>0; public ulong PrivateBytes=>0; public void Dispose(){}
    }
}
