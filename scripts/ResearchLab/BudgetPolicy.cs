using System.Text.Json;
using RamFlow.Core;

namespace ResearchLab;

// Research-only placement experiment: identical 9-of-20 requested ON epochs.
// Tail allocation deliberately spends the common quota, even at low observed
// risk. This tests placement, not optimal abstention, SLOs, or full CARP.
sealed class BudgetPolicy(string mode,int rotation,int pid,long identity,Func<double>? clock=null) : IResourcePolicy
{
    public const string Protocol="real-app-budget-v2-readback-clock";
    public const int Epochs=20,Quota=9;
    public const double EpochSeconds=.5,MaxAge=1.5;
    readonly object gate=new();
    readonly Dictionary<string,double> baseline=new(),ema=new(),seen=new();
    readonly Dictionary<string,int> ids=new();
    static readonly int[] FixedOn=[0,1,2,5,6,10,11,15,16];
    string phase="setup",reason="not-in-contention";
    int epoch=-1,used,runLength;
    bool selected,latched;
    public string Name=>"budget-"+mode;
    public string Version=>"2.0-research";
    public int Used {get {lock(gate)return used;}}
    public bool Selected {get {lock(gate)return selected;}}
    public bool QuotaComplete=>Used==Quota;
    public bool CanApplyAt(double now) {lock(gate)return phase=="Contention"&&selected&&baseline.Count==2&&seen.All(x=>now>=x.Value&&now-x.Value<=MaxAge);}
    public void Baseline(IReadOnlyDictionary<string,double> values,IReadOnlyDictionary<string,int> counts,double now) {
        if(mode is not ("fixed" or "feedback") || rotation is <0 or >=Epochs || pid<=0 || identity<=0 ||
            values.Count!=2 || values.Any(x=>!double.IsFinite(x.Value)||x.Value<=0||counts.GetValueOrDefault(x.Key)<20))
            throw new ArgumentException("Valid two-feature baseline >=20 samples each and owned identity required.");
        lock(gate)foreach(var x in values){baseline[x.Key]=x.Value;ema[x.Key]=1;seen[x.Key]=now;ids[x.Key]=0;}
    }
    public void Observe(string feature,double milliseconds,double now,int id) {
        lock(gate) {
            if(!baseline.ContainsKey(feature))return;
            if(!double.IsFinite(milliseconds)||milliseconds<0||!double.IsFinite(now)||now<seen[feature]||id<=ids[feature])
                throw new ArgumentException("Invalid, reordered or replayed response observation.");
            ema[feature]=.3*(milliseconds/baseline[feature])+.7*ema[feature];seen[feature]=now;ids[feature]=id;
        }
    }
    public void Begin(string value) {lock(gate){phase=value;epoch=-1;used=0;runLength=0;selected=false;latched=false;}}
    public void End() {lock(gate){phase="stopped";selected=false;}}
    public object Advance(int value,double now) {
        lock(gate) {
            if(phase!="Contention"||value!=epoch+1||value>=Epochs)throw new ArgumentException("Epoch skipped, duplicated, or outside contention.");
            epoch=value;bool prior=selected;selected=false;
            bool fresh=baseline.Count==2&&seen.All(x=>now>=x.Value&&now-x.Value<=MaxAge);
            double risk=ema.Count>0?ema.Values.Max():0;
            if(!fresh)reason="missing-or-stale-response";
            else if(used>=Quota)reason="quota-exhausted";
            else {
                if(risk>=1.25)latched=true;else if(risk<=1.10)latched=false;
                if(mode=="fixed") {selected=FixedOn.Contains((epoch+rotation)%Epochs);reason="fixed-periodic";}
                else {
                    selected=latched||(prior&&runLength<2);reason=selected?(latched?"response-risk":"minimum-hold"):"response-low";
                    if(Quota-used>=Epochs-epoch){selected=true;reason="common-quota-tail";}
                }
                if(selected)used++;
            }
            runLength=selected?(prior?runLength+1:1):0;
            return new {epoch,selected,used,quota=Quota,reason,risk,fresh,mode,rotation,
                observations=baseline.Keys.ToDictionary(k=>k,k=>new {baseline_p95_ms=baseline[k],normalized_ema=ema[k],seen_seconds=seen[k],source_request_id=ids[k]})};
        }
    }
    public PolicyDecision Decide(PolicyInput input) {
        lock(gate) {
            string? guard=PolicySafety.Protection(input,explicitOnly:true);
            if(input.Process.Pid!=pid||input.Process.Identity!=identity)guard??="owned-identity-mismatch";
            if(input.MemoryPressure>=PressureLevel.Moderate)guard??="outside-cpu-study-scope";
            bool on=guard is null&&CanApplyAt(clock?.Invoke()??BudgetControl.Now);
            return new(on?AppStudy.MatchedBundle:ResourceAction.None,guard??reason,PolicySafety.Classify(input),[]);
        }
    }
    public static int Replay(string[] args) {
        using var doc=JsonDocument.Parse(File.ReadAllText(Options.Get(args,"--input","")));var r=doc.RootElement;
        double replayNow=0;var policy=new BudgetPolicy(r.GetProperty("mode").GetString()!,0,987650,12,()=>replayNow);
        var values=r.GetProperty("baseline").EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.GetDouble());
        policy.Baseline(values,values.Keys.ToDictionary(k=>k,k=>r.GetProperty("baseline_count").GetInt32()),0);
        policy.Begin("Contention");var result=new List<object>();int sequence=0;
        foreach(var row in r.GetProperty("epochs").EnumerateArray()) {
            double now=row.GetProperty("time").GetDouble();replayNow=now;sequence++;
            if(row.GetProperty("observations").ValueKind==JsonValueKind.Object)foreach(var o in row.GetProperty("observations").EnumerateObject())policy.Observe(o.Name,o.Value.GetDouble(),now,sequence);
            var step=policy.Advance(row.GetProperty("epoch").GetInt32(),now);
            var system=new Snapshot {Time=DateTimeOffset.UtcNow,RamTotal=16UL<<30,Available=10UL<<30,Commit=8UL<<30,CommitLimit=32UL<<30,CpuPercent=90};
            var process=new ProcessSample(987650,12,"fixture.exe",@"D:\Fixtures\fixture.exe",1,32UL<<20,32UL<<20,20,0,true);
            var input=new PolicyInput(system,process,new Settings(),r.GetProperty("protected").GetBoolean()?987650:987651,"foreground.exe",false,"background",PressureLevel.Normal,true,0,true,false);
            result.Add(new {selected=policy.Selected,step,action=(int)policy.Decide(input).Action,
                write_allowed=policy.CanApplyAt(now+(r.TryGetProperty("write_delay",out var delay)?delay.GetDouble():0)),applied=false});
        }
        Options.WriteNew(Options.Get(args,"--output",""),new {scope="Pure SYNTHETIC replay; no OS setters",epochs=result,quota_complete=policy.QuotaComplete});return 0;
    }
}
