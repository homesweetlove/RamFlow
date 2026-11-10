using RamFlow.Core;

namespace ResearchLab;

// Frozen before Engine construction. Post-treatment observations never enter
// eligibility, cost-model features or the cohort label.
sealed record PreInterventionWindow(Snapshot[] Samples, ContentionKind[] KindSamples,
    ContentionKind Regime, CostFeatures Features, bool Stable, bool Attained)
{
    public const string Basis = "pre-intervention-window-v1";
    public DateTimeOffset StartedUtc => Samples[0].Time;
    public DateTimeOffset EndedUtc => Samples[^1].Time;
    public bool Eligible => Stable && Attained;
    public static PreInterventionWindow Freeze(IReadOnlyList<Snapshot> input, string intended) {
        if (intended is not ("cpu" or "ram" or "mixed" or "healthy") || input.Count < 5 ||
            (input[^1].Time-input[0].Time).TotalSeconds < 2 ||
            input.Zip(input.Skip(1),(a,b)=>a.Time < b.Time).Any(x=>!x) ||
            input.Any(s=>s.RamTotal==0 || s.CommitLimit==0 || !CostFeatures.From(s).Valid))
            throw new ArgumentException("A baseline needs ordered valid sensors, >=5 samples and >=2 seconds before intervention.");
        var samples=input.Select(x=>x with {}).ToArray(); var kinds=new List<ContentionKind>();
        var pressure=new Pressure(); bool cpu=false;
        foreach(var s in samples) {
            pressure.Update(s,s.RamTotal>=32UL*1073741824 ? -5 : s.RamTotal<=8UL*1073741824 ? 2 : 0);
            cpu=s.CpuPercent>=80 || cpu && s.CpuPercent>=60;
            kinds.Add((cpu,pressure.Level>=PressureLevel.Moderate) switch {
                (true,true)=>ContentionKind.Mixed,(true,false)=>ContentionKind.Cpu,
                (false,true)=>ContentionKind.Ram,_=>ContentionKind.Healthy });
        }
        var majority=kinds.GroupBy(x=>x).OrderByDescending(x=>x.Count()).ThenBy(x=>x.Key).First();
        double Median(IEnumerable<double> data) { var a=data.Order().ToArray(); return (a[(a.Length-1)/2]+a[a.Length/2])/2; }
        var features=samples.Select(CostFeatures.From).ToArray();
        // Missing page-input is kept missing, never replaced by an observed zero.
        var aggregate=new CostFeatures(Median(features.Select(x=>x.CpuPercent)),Median(features.Select(x=>x.AvailableFraction)),
            Median(features.Select(x=>x.CommitFraction)),features.All(x=>x.PagesInputPerSecond.HasValue) ? Median(features.Select(x=>x.PagesInputPerSecond!.Value)) : null);
        return new(samples,kinds.ToArray(),majority.Key,aggregate,majority.Count()>=.8*samples.Length,
            majority.Key.ToString().Equals(intended,StringComparison.OrdinalIgnoreCase));
    }
}
