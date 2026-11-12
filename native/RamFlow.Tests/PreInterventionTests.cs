using RamFlow.Core;
using ResearchLab;

static class PreInterventionTests
{
    public static void Run(Action<bool,string> check) {
        var time=DateTimeOffset.UtcNow;
        Snapshot[] Samples(params double[] cpu) => cpu.Select((v,i)=>new Snapshot { Time=time.AddMilliseconds(500*i),
            CpuPercent=v,RamTotal=24UL*1073741824,Available=12UL*1073741824,Commit=8UL*1073741824,CommitLimit=32UL*1073741824,PagesInput=0 }).ToArray();
        var baseline=PreInterventionWindow.Freeze(Samples(90,91,92,93,94),"cpu");
        check(baseline.Eligible && baseline.Regime==ContentionKind.Cpu,"Pre-intervention CPU window was not eligible");
        check(baseline.Features.CpuPercent==92 && baseline.Samples.Length==5,"Baseline did not freeze measured pre-intervention features");
        var wrong=PreInterventionWindow.Freeze(Samples(10,11,12,13,14),"cpu");
        check(wrong.Stable && !wrong.Attained && !wrong.Eligible,"Intended label replaced observed baseline pressure");
        var changing=PreInterventionWindow.Freeze(Samples(90,90,20,20,20,90),"cpu");
        check(!changing.Stable && !changing.Eligible,"Unstable pre-intervention window became eligible");
        bool rejected=false;try { PreInterventionWindow.Freeze(Samples(90,91,92,93),"cpu"); }catch(ArgumentException){rejected=true;}
        check(rejected,"Insufficient baseline sample window accepted");
        rejected=false;try { PreInterventionWindow.Freeze(Samples(90,91,92,93,94).Reverse().ToArray(),"cpu"); }catch(ArgumentException){rejected=true;}
        check(rejected,"Reversed baseline timestamps accepted");
        rejected=false;try { PreInterventionWindow.Freeze(Samples(90,91,double.NaN,93,94),"cpu"); }catch(ArgumentException){rejected=true;}
        check(rejected,"Invalid pre-intervention sensors accepted");
        check(baseline.EndedUtc>baseline.StartedUtc && baseline.KindSamples.All(x=>x==ContentionKind.Cpu),"Baseline timing/classification provenance missing");
        Console.WriteLine("Pre-intervention eligibility, sensor features and timing regressions passed.");
    }
}
