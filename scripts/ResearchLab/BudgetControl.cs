using System.Diagnostics;
using RamFlow.Core;

namespace ResearchLab;

// A separate absolute-clock actuator prevents slow app requests from shifting
// the requested intervention schedule. All writes still go through Engine.
sealed class BudgetControl(BudgetPolicy policy,Engine engine,AppProvider provider,EvidenceJournal evidence,int pid)
{
    public static double Now=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
    public double Start {get;private set;}
    public double End {get;private set;}
    public double MaxLag {get;private set;}
    public async Task Run(CancellationToken cancel) {
        Start=Now;policy.Begin("Contention");
        evidence.Write("budget-start",new {phase="Contention",clock_seconds=Start,epochs=BudgetPolicy.Epochs,quota=BudgetPolicy.Quota,epoch_seconds=BudgetPolicy.EpochSeconds},true);
        try {
            for(int epoch=0;epoch<=BudgetPolicy.Epochs;epoch++) {
                double due=Start+epoch*BudgetPolicy.EpochSeconds;
                while(Now<due)await Task.Delay(TimeSpan.FromSeconds(Math.Max(.001,due-Now)),cancel);
                cancel.ThrowIfCancellationRequested();double actual=Now;MaxLag=Math.Max(MaxLag,actual-due);
                if(epoch<BudgetPolicy.Epochs) {
                    var decision=policy.Advance(epoch,actual);
                    evidence.Write("budget-epoch",new {phase="Contention",due_seconds=due,actual_seconds=actual,decision});
                } else {policy.End();evidence.Write("budget-stop-deadline",new {phase="Contention",due_seconds=due,actual_seconds=actual});}
                var sample=engine.Tick().Snapshot;var readback=provider.ReadValues(pid);
                evidence.Write("system-sample",new {phase="Contention",sample,readback,per_process_hard_faults=(double?)null,pages_input_scope="system-wide only"});
                if(sample.Available<512UL*1048576||(double)sample.Commit/Math.Max(1,sample.CommitLimit)>.9)throw new IOException("Budget control safety headroom exhausted.");
            }
        } finally {
            policy.End();End=Now;
            evidence.Write("budget-end",new {phase="Contention",clock_seconds=End,used=policy.Used,quota_complete=policy.QuotaComplete,max_deadline_lag_seconds=MaxLag},true);
        }
    }
    public object Result(ResourceValues original) {
        double on=0,cursor=Start;bool active=false;
        foreach(var row in provider.PolicyReadbacks.Where(x=>x.Time>=Start&&x.Time<=End).OrderBy(x=>x.Time)) {
            if(active)on+=row.Time-cursor;
            active=!AppStudy.Same(row.Values,original);cursor=row.Time;
        }
        if(active)on+=End-cursor;
        return new {protocol=BudgetPolicy.Protocol,requested_on_seconds=BudgetPolicy.Quota*BudgetPolicy.EpochSeconds,actual_readback_on_seconds=on,
            scope="Readback-state wall time; not CPU share or equal background throughput loss",used=policy.Used,quota_complete=policy.QuotaComplete,
            start_seconds=Start,end_seconds=End,max_deadline_lag_seconds=MaxLag,
            matched_session=policy.QuotaComplete&&Math.Abs(on-4.5)<=.1&&MaxLag<=.1&&!active};
    }
}
