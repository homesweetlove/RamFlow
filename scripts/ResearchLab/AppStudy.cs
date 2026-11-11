using System.Diagnostics;
using System.Text.Json;
using RamFlow.Core;

namespace ResearchLab;

// These A/B/C labels belong ONLY to real-app-abc-v1, not the CARP A--F matrix.
static class AppStudy
{
    internal static readonly ResourceAction MatchedBundle=ResourceAction.LowerCpu|ResourceAction.EcoQos|ResourceAction.LowerMemory;
    internal static bool Same(ResourceValues? a,ResourceValues? b)=>a is not null && b is not null &&
        a.MemoryPriority==b.MemoryPriority && a.CpuPriority==b.CpuPriority && a.PowerControl==b.PowerControl && a.PowerState==b.PowerState && a.CpuSets.SequenceEqual(b.CpuSets);
    static double Quantile(double[] values,double p) {
        var sorted=values.Order().ToArray();double position=(sorted.Length-1)*p;int lo=(int)position,hi=(int)Math.Ceiling(position);
        return sorted[lo]+(sorted[hi]-sorted[lo])*(position-lo);
    }
    public static async Task<int> Run(string[] args) {
        string root=Path.GetFullPath(Options.Get(args,"--output",""));Options.NewDirectory(root);
        using var evidence=new EvidenceJournal(root,args,"real-app-abc-v1");
        var failures=new List<string>();var summaries=new List<object>();var observations=new List<JsonElement>();
        FixtureChild? background=null;AppDriver? app=null;AppProvider? provider=null;Engine? engine=null;AppAudit? audit=null;
        IndependentObserver? observer=null;Process? tray=null;Task? pipe=null;CancellationTokenSource? lifetime=null;
        ResourceValues? original=null,final=null;bool cleanup=true,restored=false;string phase="setup";
        string condition=Options.Get(args,"--condition",""),application=Options.Get(args,"--app","");
        using var self=Process.GetCurrentProcess();
        try {
            if(condition is not ("A" or "B" or "C") || application is not ("chrome" or "code"))throw new ArgumentException("Use --app chrome|code --condition A|B|C.");
            int seconds=Options.Int(args,"--phase-seconds",10,5,120);
            int threads=Options.Int(args,"--contention-threads",Math.Min(12,Environment.ProcessorCount),1,Math.Min(16,Environment.ProcessorCount));
            string driver=Path.GetFullPath(Options.Get(args,"--driver","scripts/real_apps/driver.py"));
            string runtime=Path.Combine(Path.GetDirectoryName(root)!,"runtime-"+evidence.RunId);
            Options.WriteNew(Path.Combine(root,"started.json"),new { application,condition,protocol="real-app-abc-v1",started_utc=DateTimeOffset.UtcNow,
                planned_phases=new[]{"Normal","Contention","Recovery"},seconds,threads,scope="One smoke session, synthetic CPU background, real generated app tasks; not efficacy evidence",runtime });
            Options.WriteNew(Path.Combine(root,"source-manifest.json"),Options.CaptureSources(root));
            string binary=Path.Combine(root,"fixture-bin");Directory.CreateDirectory(binary);
            foreach(string file in Directory.GetFiles(AppContext.BaseDirectory).Where(x=>x.EndsWith(".dll")||x.EndsWith(".json")))File.Copy(file,Path.Combine(binary,Path.GetFileName(file)));
            string appHost=Path.Combine(AppContext.BaseDirectory,"ResearchLab.exe"),backgroundExe=Path.Combine(binary,"BackgroundFixture.exe");File.Copy(appHost,backgroundExe);
            if(self.SessionId<=0)throw new InvalidOperationException("Interactive Windows session required.");
            if(new[]{"RamFlow","RamFlow.Service","RamFlow.Tray"}.Any(name=>{var ps=Process.GetProcessesByName(name);try{return ps.Length>0;}finally{foreach(var p in ps)p.Dispose();}}))
                throw new InvalidOperationException("Existing RAM Flow left untouched; isolated pilot cannot start.");
            using(var sensor=new WindowsResourceProvider()) {
                var initial=sensor.Sample();evidence.Write("system-sample",new {phase,sample=initial});
                if(initial.Available<2UL*1073741824||initial.CommitLimit==0||(double)initial.Commit/initial.CommitLimit>.85)throw new IOException("Insufficient safe headroom.");
            }
            app=await AppDriver.Start(Options.Get(args,"--python","python"),driver,application,runtime,root,evidence);
            background=new(backgroundExe,"background","cpu",32,threads,20261111,0,Path.Combine(root,"background-raw.jsonl"),evidence,initialThreads:0);
            await background.Ready();provider=new(background,evidence,()=>phase);
            original=provider.ReadValues(background.Process.Id)??throw new IOException("Missing original readback.");
            provider.Processes(); // Prime CPU/IO activity before policy eligibility; unknown activity remains protected.
            Options.WriteNew(Path.Combine(root,"resource-original.json"),new {pid=background.Process.Id,creation_filetime=background.Identity,values=original});
            var config=new Settings {DryRun=false,PolicyName=condition=="B"?"fixed":"v1",CpuManagement=true,EcoQos=true,CpuSets=false,WorkingSetTrim=false,Learning=false,BackgroundApps=[backgroundExe]};
            Options.WriteNew(Path.Combine(root,"effective-settings.json"),new {condition,labels=new {A="no-engine-or-tray",B="fixed-v1-matched-bundle",C="adaptive-v1"},
                settings=config,settings_sha256=FileResearchAuditSink.Hash(config),matched_action=(int)MatchedBundle,
                target=new {CpuPriority=0x4000,MemoryPriority=3,EcoControlBit=1,EcoStateBit=1},
                readback_rule="Compare requested and observed after EVERY write. B and C active writes must equal the same V1 transformation of each original; restored/no-action values are separate."});
            for(int n=0;n<3;n++)foreach(string feature in app.Features) {
                var warmup=await app.Request(feature,"warmup");
                evidence.Write("app-warmup",new {phase="warmup",reply=warmup});
                if(!warmup.GetProperty("correct").GetBoolean())throw new IOException("App warmup correctness failed.");
            }
            if(condition!="A") {
                audit=new(root,evidence,()=>phase);
                engine=new(provider,Path.Combine(root,"engine-data"),persist:true,allowRemote:false,audit:audit,
                    researchPolicy:condition=="B"?new CandidatePolicy(MatchedBundle):null);
                engine.ApplySettings(config);
                EnginePipe.ConfigureVerificationNamespace(Guid.NewGuid());lifetime=new();pipe=EnginePipe.ServeAsync(engine,lifetime);
                string exe=Path.GetFullPath(Options.Get(args,"--tray-exe","native/RamFlow.Tray/bin/Release/net10.0-windows/RamFlow.Tray.exe"));
                string readyName=UiHandoff.NewReadyName();using var ready=new EventWaitHandle(false,EventResetMode.ManualReset,readyName);
                var start=new ProcessStartInfo(exe) {UseShellExecute=false,CreateNoWindow=true};
                foreach(string arg in new[]{"--ipc-namespace",EnginePipe.VerificationNamespace!,"--data-root",Path.Combine(root,"engine-data"),"--ready-event",readyName})start.ArgumentList.Add(arg);
                tray=Process.Start(start)??throw new IOException("Isolated tray launch failed.");
                evidence.Write("process-owned",new {role="tray",pid=tray.Id,creation_filetime=tray.StartTime.ToFileTimeUtc(),executable=exe,sha256=FileResearchAuditSink.FileHash(exe),arguments=start.ArgumentList.ToArray()},true);
                if(!ready.WaitOne(8000)||tray.HasExited)throw new IOException("Isolated tray readiness failed.");
            }
            var targets=new Dictionary<string,(int,long)> {
                [condition=="A"?"controller-no-engine":"engine-and-controller"]=(self.Id,self.StartTime.ToFileTimeUtc()),
                ["app-driver"]=(app.Process.Id,app.Process.StartTime.ToFileTimeUtc()),["background"]=(background.Process.Id,background.Identity)};
            if(tray is not null)targets["tray"]=(tray.Id,tray.StartTime.ToFileTimeUtc());
            observer=await IndependentObserver.StartAsync(appHost,Path.Combine(root,"independent"),targets);
            evidence.Write("component-scope",new {phase,engine_host=condition=="A"?"not-started; controller measured separately":"in-process engine plus controller; not isolated engine CPU",
                tray=tray is null?"not-started":"isolated actual tray",optional_service="not-started",observer="independent observer samples itself",app_driver="measured separately"},true);
            foreach(string nextPhase in new[]{"Normal","Contention","Recovery"}) {
                phase=nextPhase;observer.SetPhase(phase=="Contention"?"intervene":"observe");
                int active=phase=="Contention"?threads:1;
                var first=await background.Request("load",active,phase);
                evidence.Write("phase-start",new {phase,active_threads=active,units=first.Units,seconds=first.Seconds,observed_utc=first.ObservedUtc},true);
                var phaseRows=new List<JsonElement>();var watch=Stopwatch.StartNew();double nextTick=0;int rounds=0;
                while(watch.Elapsed.TotalSeconds<seconds || rounds<12) {
                    if(File.Exists(Path.Combine(root,"cancel.request")))throw new OperationCanceledException("Owner cancellation; restore and seal as failure.");
                    if(watch.Elapsed.TotalSeconds>seconds+45)throw new TimeoutException("Bounded phase timeout.");
                    if(watch.Elapsed.TotalSeconds>=nextTick) {
                        // Reuse the exact decision snapshot: a second immediate sensor
                        // read would reset CPU deltas and log a different interval.
                        var sample=engine is null?provider.Sample():engine.Tick().Snapshot;var readback=provider.ReadValues(background.Process.Id);
                        evidence.Write("system-sample",new {phase,sample,readback,per_process_hard_faults=(double?)null,pages_input_scope="system-wide only"});
                        if(sample.Available<512UL*1048576||(double)sample.Commit/Math.Max(1,sample.CommitLimit)>.9)throw new IOException("Safety headroom exhausted.");
                        nextTick=watch.Elapsed.TotalSeconds+.5;
                    }
                    foreach(string feature in app.Features) {
                        var reply=await app.Request(feature,phase);phaseRows.Add(reply);observations.Add(reply);
                        evidence.Write("app-observation",reply);
                        if(!reply.GetProperty("correct").GetBoolean())throw new IOException("App correctness failed.");
                    }
                    rounds++;await Task.Delay(100);
                }
                var last=await background.Request("progress");
                double duration=last.Seconds-first.Seconds;long units=last.Units-first.Units;
                var metrics=app.Features.Select(feature=> {
                    var rows=phaseRows.Where(r=>r.GetProperty("feature").GetString()==feature).ToArray();
                    var latency=rows.Select(r=>r.GetProperty("latency_ms").GetDouble()).ToArray();
                    return new {feature,count=latency.Length,latencies_ms=latency,p50_ms=Quantile(latency,.5),p95_ms=Quantile(latency,.95),
                        p99_ms=(double?)null,roundtrip_ms=rows.Select(r=>r.GetProperty("roundtrip_ms").GetDouble()).ToArray()};
                }).ToArray();
                var summary=new {phase,metrics,background_units=units,background_seconds=duration,background_units_per_second=units/duration};summaries.Add(summary);
                evidence.Write("phase-end",new {phase,units=last.Units,seconds=last.Seconds,observed_utc=last.ObservedUtc},true);
                if(phase=="Recovery" && condition=="C" && !Same(provider.ReadValues(background.Process.Id),original))
                    throw new IOException("V1 did not restore during Recovery; cleanup will retry. Phase transition smoke is not successful.");
            }
        } catch(Exception error) {failures.Add(error.ToString());evidence.TryWrite("app-failure",new {phase,error=error.ToString()});}
        finally {
            phase="cleanup";
            if(observer is not null)try {observer.SetPhase("restore");}catch(Exception e){failures.Add(e.ToString());cleanup=false;}
            if(engine is not null)try {engine.Stop();}catch(Exception e){failures.Add(e.ToString());cleanup=false;}
            if(provider is not null && background is not null)try {
                final=provider.ReadValues(background.Process.Id);restored=Same(original,final) && (engine?.GetState().PendingRestores??0)==0;
            }catch(Exception e){failures.Add(e.ToString());cleanup=false;}
            if(tray is not null)try {
                using var dismiss=new EventWaitHandle(false,EventResetMode.AutoReset,UiHandoff.DismissName);dismiss.Set();
                await tray.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if(tray.ExitCode!=0)throw new IOException("Tray nonzero exit");
                evidence.TryWrite("process-exited",new {role="tray",pid=tray.Id,exit_code=tray.ExitCode});
            }catch(Exception e){failures.Add(e.ToString());cleanup=false;if(!tray.HasExited)tray.Kill();}
            if(lifetime is not null) {lifetime.Cancel();try {if(pipe is not null)await pipe.WaitAsync(TimeSpan.FromSeconds(6));}catch(Exception e){failures.Add(e.ToString());cleanup=false;}lifetime.Dispose();}
            if(observer is not null)try {
                await Task.Delay(250);await observer.StopAsync();
                var samples=observer.ReadSamples();Options.WriteNew(Path.Combine(root,"observer-control.json"),observer.ControlWrites);
                Options.WriteNew(Path.Combine(root,"overhead-summary.json"),new {scope="Independent process resource readings; engine shares host with orchestration. Semantic phase assigned by event UTC boundaries during analysis.",samples=samples.Length,
                    tray=tray is null?"not-started":"measured",optional_service="not-started",policy_timing_ms=audit?.DecisionMs.ToArray(),audit_write_ms=audit?.WriteMs.ToArray()});
                await observer.DisposeAsync();
            }catch(Exception e){failures.Add(e.ToString());cleanup=false;}
            if(app is not null)try {await app.DisposeAsync();}catch(Exception e){failures.Add(e.ToString());cleanup=false;}
            if(background is not null)try {await background.DisposeAsync();}catch(Exception e){failures.Add(e.ToString());cleanup=false;}
            try {engine?.Dispose();provider?.Dispose();audit?.Dispose();tray?.Dispose();}catch(Exception e){failures.Add(e.ToString());cleanup=false;}
        }
        Options.WriteNew(Path.Combine(root,"resource-final.json"),new {values=final,restored,cleanup_completed=cleanup,protected_misinterventions=provider?.OutOfScopeRequests??0});
        if(!restored)failures.Add("Original background settings were not confirmed restored.");
        if((provider?.ReadbackFailures??0)>0)failures.Add("One or more OS readbacks differed from requested settings.");
        if((provider?.OutOfScopeRequests??0)>0)failures.Add("Out-of-scope resource write attempt.");
        if(condition is "B" or "C" && (audit?.Applies??0)==0)failures.Add("No active application: matched B/C readback not demonstrated.");
        string status=failures.Count==0&&cleanup?"completed":"failed";
        Options.WriteNew(Path.Combine(root,"outcome.json"),new {status,evidence_run_id=evidence.RunId,application,condition,protocol="real-app-abc-v1",restored,cleanup_completed=cleanup,
            phases=summaries,observations=observations.Count,applies=audit?.Applies??0,readback_failures=provider?.ReadbackFailures??0,protected_misinterventions=provider?.OutOfScopeRequests??0,failures,
            inference="Smoke functionality only. One session per app/condition is not an independent efficacy replication; no superiority inference."});
        evidence.Seal(status,cleanup);Console.WriteLine(JsonSerializer.Serialize(new {root,status,restored,cleanup,failures}));return status=="completed"?0:1;
    }
}

sealed class AppProvider(FixtureChild background,EvidenceJournal evidence,Func<string> phase) : IResourceProvider
{
    readonly WindowsResourceProvider real=new();
    (double Cpu,ulong Io,long Stamp)? previous;
    public int OutOfScopeRequests {get;private set;}
    public int ReadbackFailures {get;private set;}
    public Snapshot Sample()=>real.Sample();
    public IReadOnlyList<ProcessSample> Processes() {
        ProcessSample Read(Process p,long identity)=>new(p.Id,identity,p.ProcessName+".exe",p.MainModule?.FileName??"",p.SessionId,(ulong)p.WorkingSet64,(ulong)p.PrivateMemorySize64,0,0,false);
        var bg=background.Process;bg.Refresh();double cpu=bg.TotalProcessorTime.TotalSeconds;long stamp=Stopwatch.GetTimestamp();
        bool ioKnown=OwnedIo.GetProcessIoCounters(bg.SafeHandle,out var io);ulong bytes=io.ReadBytes+io.WriteBytes;
        double seconds=previous is {} prior?(stamp-prior.Stamp)/(double)Stopwatch.Frequency:0;
        double cpuRate=previous is {} before&&seconds>0?Math.Max(0,(cpu-before.Cpu)/seconds/Environment.ProcessorCount*100):0;
        double ioRate=previous is {} old&&seconds>0&&bytes>=old.Io?(bytes-old.Io)/seconds:0;
        previous=(cpu,bytes,stamp);
        var rows=new List<ProcessSample>{Read(bg,background.Identity) with {CpuPercent=cpuRate,IoBytesPerSecond=ioRate,ActivityKnown=ioKnown&&seconds>0}};
        var fg=real.Foreground();
        if(fg.Pid>0 && fg.Pid!=background.Process.Id)try {using var p=Process.GetProcessById(fg.Pid);rows.Add(Read(p,p.StartTime.ToFileTimeUtc()));}catch(ArgumentException){}
        return rows;
    }
    public (int Pid,string Name) Foreground()=>real.Foreground();
    public bool Fullscreen()=>real.Fullscreen();
    public IReadOnlyList<CpuCore> Topology()=>real.Topology();
    public long? Identity(int pid)=>real.Identity(pid);
    public bool Exited(int pid)=>real.Exited(pid);
    bool Owns(int pid,long identity)=>pid==background.Process.Id && identity==background.Identity;
    public bool Validate(ProcessSample p,bool restoring=false)=>Owns(p.Pid,p.Identity)&&real.Validate(p,restoring);
    public ResourceValues? ReadValues(int pid)=>real.ReadValues(pid);
    public bool WriteValues(int pid,ResourceValues values,long identity) {
        if(!Owns(pid,identity)) {OutOfScopeRequests++;evidence.Write("out-of-scope-write",new {phase=phase(),pid,creation_filetime=identity});return false;}
        bool result=real.WriteValues(pid,values,identity);var observed=real.ReadValues(pid);
        bool matched=result&&AppStudy.Same(values,observed)&&real.Identity(pid)==identity;
        evidence.Write("resource-write-readback",new {phase=phase(),pid,creation_filetime=identity,requested=values,observed,api_result=result,matched},true);
        if(!matched)ReadbackFailures++;return matched;
    }
    public bool Trim(int pid,long identity) {OutOfScopeRequests++;return false;}
    public ulong? WorkingSet(int pid,long identity)=>Owns(pid,identity)?real.WorkingSet(pid,identity):null;
    public void Dispose()=>real.Dispose();
}

sealed class AppAudit : IResearchAuditSink,IDisposable
{
    readonly FileResearchAuditSink sink;readonly EvidenceJournal evidence;readonly Func<string> phase;
    public List<double> DecisionMs {get;}=[];public List<double> WriteMs {get;}=[];public int Applies {get;private set;}
    public AppAudit(string root,EvidenceJournal evidence,Func<string> phase) {this.evidence=evidence;this.phase=phase;sink=new(Path.Combine(root,"audit"),"see-source-manifest");}
    public void Write(ResearchAuditEvent entry) {
        var tagged=entry with {Detail=new {phase=phase(),original_detail=entry.Detail}};
        var clock=Stopwatch.StartNew();sink.Write(tagged);WriteMs.Add(clock.Elapsed.TotalMilliseconds);
        evidence.Write("engine-event",new {phase=phase(),entry});
        if(entry.Kind=="apply"&&entry.Result=="applied")Applies++;
        if(entry.Kind=="decision"&&entry.Detail is not null) {
            var data=JsonSerializer.SerializeToElement(entry.Detail);if(data.TryGetProperty("DecisionElapsedMs",out var ms))DecisionMs.Add(ms.GetDouble());
        }
    }
    public void Dispose()=>sink.Dispose();
}

sealed class AppDriver : IAsyncDisposable
{
    public Process Process {get;}readonly EvidenceJournal evidence;readonly StreamWriter raw;readonly Task errors;int nextId;bool stopped;
    public string[] Features {get;private set;}=[];
    AppDriver(Process p,EvidenceJournal evidence,StreamWriter raw,Task errors) {Process=p;this.evidence=evidence;this.raw=raw;this.errors=errors;}
    public static async Task<AppDriver> Start(string python,string driver,string app,string runtime,string root,EvidenceJournal evidence) {
        var start=new ProcessStartInfo(python) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"-u",driver,"--app",app,"--runtime",runtime})start.ArgumentList.Add(arg);
        var p=Process.Start(start)??throw new IOException("App driver launch failed.");
        var raw=new StreamWriter(new FileStream(Path.Combine(root,"app-driver.jsonl"),FileMode.CreateNew)) {AutoFlush=true};
        var errors=Task.Run(async()=> {using var error=new StreamWriter(new FileStream(Path.Combine(root,"app-driver.stderr.jsonl"),FileMode.CreateNew)) {AutoFlush=true};
            while(await p.StandardError.ReadLineAsync() is string line)await error.WriteLineAsync(JsonSerializer.Serialize(new {time_utc=DateTimeOffset.UtcNow,text=line}));});
        var adapter=new AppDriver(p,evidence,raw,errors);
        try {
            evidence.Write("process-owned",new {role="app-driver",pid=p.Id,creation_filetime=p.StartTime.ToFileTimeUtc(),arguments=start.ArgumentList.ToArray()},true);
            var ready=await adapter.Read(150);if(ready.GetProperty("kind").GetString()!="ready")throw new IOException("App adapter not ready");
            adapter.Features=ready.GetProperty("features").EnumerateArray().Select(x=>x.GetString()!).ToArray();evidence.Write("app-ready",ready,true);return adapter;
        }catch {try {await adapter.DisposeAsync();}catch {}throw;}
    }
    async Task<JsonElement> Read(int seconds=50) {
        string line=await Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(seconds))??throw new IOException("App driver output ended; inspect stderr.");
        await raw.WriteLineAsync(line);using var doc=JsonDocument.Parse(line);return doc.RootElement.Clone();
    }
    public async Task<JsonElement> Request(string feature,string phase) {
        int id=++nextId;var data=new {role="app-driver",phase,pid=Process.Id,creation_filetime=Process.StartTime.ToFileTimeUtc(),id,feature};
        evidence.Write("request-begin",data);var clock=Stopwatch.StartNew();
        try {
            await Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new {id,verb="request",feature}));await Process.StandardInput.FlushAsync();
            var reply=await Read();if(reply.GetProperty("id").GetInt32()!=id)throw new IOException("App response identity mismatch.");
            var tagged=JsonSerializer.SerializeToElement(new {id,phase,feature,latency_ms=reply.GetProperty("latency_ms").GetDouble(),roundtrip_ms=reply.GetProperty("roundtrip_ms").GetDouble(),
                controller_roundtrip_ms=clock.Elapsed.TotalMilliseconds,correct=reply.GetProperty("correct").GetBoolean(),reply});
            evidence.Write("request-end",new {data.role,data.phase,data.pid,data.creation_filetime,data.id,feature,reply=tagged});return tagged;
        }catch(Exception e){evidence.TryWrite("request-error",new {data.role,data.phase,data.pid,data.creation_filetime,data.id,error=e.ToString()});throw;}
    }
    public async ValueTask DisposeAsync() {
        try {
            if(!stopped && !Process.HasExited) {
                int id=++nextId;await Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new {id,verb="stop"}));await Process.StandardInput.FlushAsync();
                var reply=await Read(25);evidence.Write("app-cleanup",reply,true);
                if(reply.GetProperty("id").GetInt32()!=id||!reply.GetProperty("cleanup").GetProperty("completed").GetBoolean())throw new IOException("App cleanup not confirmed.");
                stopped=true;
            }
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));await errors;
            if(!stopped||Process.ExitCode!=0)throw new IOException("App driver failed or cleanup not confirmed.");
        }finally {
            if(!Process.HasExited) {evidence.TryWrite("driver-forced-stop",new {pid=Process.Id});Process.Kill();await Process.WaitForExitAsync();}
            await errors;raw.Dispose();Process.Dispose();
        }
    }
}
