namespace RamFlow.Core;

public sealed record CarpOptions
{
    public double ForegroundWeight { get; init; } = 1;
    public double BackgroundWeight { get; init; } = 1;
    public double ReaccessWeight { get; init; } = 1;
    public double OverheadWeight { get; init; } = 1;
    public double EntryMargin { get; init; } = .02;
    public double MinConfidence { get; init; } = .8;
    public int StableSamples { get; init; } = 3;
    public int CooldownSeconds { get; init; } = 30;
    public int MaxEvidenceAgeSeconds { get; init; } = 10;
    public int MinIndependentBlocks { get; init; } = 3;
    public static void Validate(CarpOptions value) {
        double[] weights = [value.ForegroundWeight, value.BackgroundWeight, value.ReaccessWeight, value.OverheadWeight];
        if (weights.Any(x => !double.IsFinite(x) || x < 0 || x > 10) || weights.Sum() <= 0 ||
            !double.IsFinite(value.EntryMargin) || value.EntryMargin is < 0 or > 1 ||
            !double.IsFinite(value.MinConfidence) || value.MinConfidence is < .5 or > 1 ||
            value.StableSamples is < 1 or > 60 || value.CooldownSeconds is < 0 or > 3600 ||
            value.MaxEvidenceAgeSeconds is < 1 or > 60 || value.MinIndependentBlocks is < 3 or > 10000)
            throw new ArgumentException("Invalid CARP research configuration.");
    }
}

public sealed class CarpPolicy : IResourcePolicy
{
    sealed record History(ResourceAction Candidate, int Count, DateTimeOffset Seen, DateTimeOffset Touched, DateTimeOffset Changed, ResourceAction Applied);
    readonly CarpOptions options;
    readonly Dictionary<(int, long), History> history = new();
    public string Name => "carp";
    public string Version => "0.1.0-research";
    public CarpPolicy(CarpOptions? options = null) { this.options = options ?? new(); CarpOptions.Validate(this.options); }
    public void OnActionChanged(int pid, long identity, ResourceAction action, DateTimeOffset time) {
        var key = (pid, identity); history.TryGetValue(key, out var old);
        if (old is not null || history.Count < 4096)
            history[key] = new(ResourceAction.None, 0, old?.Seen ?? DateTimeOffset.MinValue, time, time, action);
    }
    public PolicyDecision Decide(PolicyInput input) {
        var kind = PolicySafety.Classify(input);
        var key = (input.Process.Pid, input.Process.Identity);
        foreach (var stale in history.Where(x => input.System.Time - x.Value.Touched > TimeSpan.FromHours(1)).Select(x => x.Key).ToArray()) history.Remove(stale);
        history.TryGetValue(key, out var previous);
        // Track actual applied/restored transitions even when observations vanish.
        // The next decision observes successful engine application or restoration.
        DateTimeOffset lastChange = previous?.Changed ?? DateTimeOffset.MinValue;
        if (previous is not null && previous.Applied != input.CurrentAction) lastChange = input.System.Time;
        PolicyDecision None(string reason, IReadOnlyList<CandidateEvaluation>? candidates = null) {
            if (previous is not null || history.Count < 4096)
                history[key] = new(ResourceAction.None, 0, previous?.Seen ?? DateTimeOffset.MinValue, input.System.Time, lastChange, input.CurrentAction);
            return new(ResourceAction.None, reason, kind, candidates ?? [new(ResourceAction.None, 0, null, "reference-no-intervention")]);
        }
        var guard = PolicySafety.Protection(input, explicitOnly: true);
        if (guard is not null) return None(guard);
        if (kind == ContentionKind.Healthy) return None("healthy-restore");
        if (!double.IsFinite(input.System.CpuPercent) || input.System.CpuPercent is < 0 or > 100 ||
            input.System.RamTotal == 0 || input.System.CommitLimit == 0 || input.System.Available > input.System.RamTotal || input.Process.Identity <= 0)
            return None("invalid-system-or-identity");
        var evidence = input.Evidence;
        if (evidence is null || evidence.Pid != input.Process.Pid || evidence.Identity != input.Process.Identity ||
            string.IsNullOrWhiteSpace(evidence.ModelId) || evidence.ModelId.Length > 256 || evidence.IndependentBlocks < options.MinIndependentBlocks ||
            evidence.ObservedAt > input.System.Time || (input.System.Time - evidence.ObservedAt).TotalSeconds > options.MaxEvidenceAgeSeconds ||
            evidence.Contention != kind || evidence.Candidates is null || evidence.Candidates.Count > 16)
            return None("missing-stale-or-incompatible-evidence");
        if (evidence.MeasuredForegroundP95Ms is double latency && (!double.IsFinite(latency) || latency < 0) ||
            evidence.MeasuredBackgroundUnitsPerSecond is double throughput && (!double.IsFinite(throughput) || throughput < 0))
            return None("invalid-measured-observation");
        var evaluations = new List<CandidateEvaluation> { new(ResourceAction.None, 0, null, "reference-no-intervention") };
        foreach (var candidate in evidence.Candidates) {
            if (candidate is null) return None("null-candidate");
            bool cpu = candidate.Action.HasFlag(ResourceAction.LowerCpu), memory = candidate.Action.HasFlag(ResourceAction.LowerMemory);
            bool allowed = candidate.Action != ResourceAction.None && (candidate.Action & ~(ResourceAction.LowerCpu | ResourceAction.EcoQos | ResourceAction.LowerMemory)) == 0 &&
                (!cpu || input.Settings.CpuManagement && kind is ContentionKind.Cpu or ContentionKind.Mixed) &&
                (!memory || kind is ContentionKind.Ram or ContentionKind.Mixed) &&
                (!candidate.Action.HasFlag(ResourceAction.EcoQos) || cpu && input.Settings.EcoQos) &&
                new[] { candidate.ForegroundGain, candidate.BackgroundLoss, candidate.ReaccessRisk, candidate.ControlCost, candidate.Confidence }.All(x => double.IsFinite(x) && x is >= 0 and <= 1) &&
                candidate.Confidence >= options.MinConfidence;
            // Avoid serializing NaN/Infinity into audit JSON. Invalid evidence is
            // rejected as a whole, so no other estimate can mask corrupt input.
            if (!allowed) return None("invalid-or-disabled-candidate");
            double cost = -options.ForegroundWeight * candidate.ForegroundGain + options.BackgroundWeight * candidate.BackgroundLoss +
                options.ReaccessWeight * candidate.ReaccessRisk + options.OverheadWeight * candidate.ControlCost;
            evaluations.Add(new(candidate.Action, cost, candidate, "model-estimate-not-measured-effect"));
        }
        if (evaluations.GroupBy(x => x.Action).Any(x => x.Count() > 1)) return None("duplicate-candidate");
        var best = evaluations.OrderBy(x => x.Cost).ThenBy(x => Strength(x.Action)).ThenBy(x => (int)x.Action).First();
        if (best.Action == ResourceAction.None || best.Cost >= -options.EntryMargin) return None("no-net-benefit-above-margin", evaluations);
        // Escalation can add only one control at a time, and only when that
        // intermediate candidate itself has evidence for positive utility.
        var reachable = evaluations.Where(x => x.Action != ResourceAction.None && x.Cost < -options.EntryMargin &&
            Strength(x.Action) <= Strength(input.CurrentAction) + 1 &&
            (input.CurrentAction == ResourceAction.None || (x.Action & input.CurrentAction) == input.CurrentAction || Strength(x.Action) <= Strength(input.CurrentAction)))
            .OrderBy(x => x.Cost).ThenBy(x => Strength(x.Action)).ThenBy(x => (int)x.Action).FirstOrDefault();
        if (reachable is null) return None("no-supported-minimal-stage", evaluations);
        var action = reachable.Action;
        history.TryGetValue(key, out var old);
        if (old is not null && evidence.ObservedAt < old.Seen) return None("observation-time-regressed", evaluations);
        bool distinct = old is null || evidence.ObservedAt > old.Seen;
        int stable = old is not null && old.Candidate == action ? old.Count + (distinct ? 1 : 0) : 1;
        DateTimeOffset changed = lastChange;
        if (history.Count >= 4096 && old is null) return None("policy-history-capacity", evaluations);
        history[key] = new(action, stable, evidence.ObservedAt, input.System.Time, changed, input.CurrentAction);
        bool escalate = (action & ~input.CurrentAction) != 0;
        if (escalate && (stable < options.StableSamples || (input.System.Time - changed).TotalSeconds < options.CooldownSeconds)) {
            bool supportedCurrent = evaluations.Any(x => x.Action == input.CurrentAction && x.Cost <= 0);
            return new(supportedCurrent ? input.CurrentAction : ResourceAction.None, "entry-hysteresis-or-cooldown", kind, evaluations);
        }
        return new(action, action == input.CurrentAction ? "retain-supported-minimal-cost" : "select-supported-minimal-cost", kind, evaluations);
    }
    static int Strength(ResourceAction action) => System.Numerics.BitOperations.PopCount((uint)action);
}
