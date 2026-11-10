namespace RamFlow.Core;

[Flags]
public enum ResourceAction { None = 0, LowerCpu = 1, EcoQos = 2, LowerMemory = 4, CpuSets = 8 }
public enum ContentionKind { Unknown, Healthy, Cpu, Ram, Mixed }
public sealed record CandidateEstimate(ResourceAction Action, double ForegroundGain, double BackgroundLoss,
    double ReaccessRisk, double ControlCost, double Confidence);
public sealed record CandidateEvaluation(ResourceAction Action, double? Cost, CandidateEstimate? Estimate, string Reason);
// All four cost terms are model estimates in [0,1], not measured latency.
// The source must document how the model was calibrated in independent pilot blocks.
public sealed record CarpEvidence(int Pid, long Identity, DateTimeOffset ObservedAt, string ModelId,
    ContentionKind Contention, int IndependentBlocks, IReadOnlyList<CandidateEstimate> Candidates)
{
    public double? MeasuredForegroundP95Ms { get; init; }
    public double? MeasuredBackgroundUnitsPerSecond { get; init; }
}
public interface IPolicyObservationSource { CarpEvidence? Read(ProcessSample process, Snapshot snapshot); }
public sealed record PolicyInput(Snapshot System, ProcessSample Process, Settings Settings,
    int ForegroundPid, string ForegroundName, bool Fullscreen, string Mode, PressureLevel MemoryPressure,
    bool CpuPressure, double IdleSeconds, bool Designated, bool OwnedJob,
    ResourceAction CurrentAction = ResourceAction.None, CarpEvidence? Evidence = null);
public sealed record PolicyDecision(ResourceAction Action, string Reason, ContentionKind Contention,
    IReadOnlyList<CandidateEvaluation> Candidates);
public interface IResourcePolicy
{
    string Name { get; }
    string Version { get; }
    PolicyDecision Decide(PolicyInput input);
    void OnActionChanged(int pid, long identity, ResourceAction action, DateTimeOffset time) { }
}
public static class PolicySafety
{
    public static string? Protection(PolicyInput input, bool explicitOnly = false) {
        if (input.Settings.Paused) return "paused";
        if (input.ForegroundPid <= 0 || string.IsNullOrWhiteSpace(input.ForegroundName)) return "foreground-unknown";
        if (explicitOnly && !input.Designated) return "explicit-background-required";
        if (input.Fullscreen && !input.Designated) return "fullscreen";
        return Policy.Protect(input.Process, (input.ForegroundPid, input.ForegroundName), input.Settings, input.Mode, input.Designated);
    }
    public static ContentionKind Classify(PolicyInput input) {
        bool ram = input.MemoryPressure >= PressureLevel.Moderate;
        return (input.CpuPressure, ram) switch { (true, true) => ContentionKind.Mixed,
            (true, false) => ContentionKind.Cpu, (false, true) => ContentionKind.Ram, _ => ContentionKind.Healthy };
    }
}
// Preserves V1 eligibility and the original bundled action; eviction remains a
// separately gated V1-only path in Engine, outside CARP's candidate space.
public sealed class V1ResourcePolicy : IResourcePolicy
{
    public string Name => "v1";
    public string Version => "1.0-baseline-53cd18a";
    public PolicyDecision Decide(PolicyInput input) {
        string? protection = PolicySafety.Protection(input);
        bool eligible = protection is null && (input.Designated || input.IdleSeconds >= 1800) &&
            (input.OwnedJob || input.MemoryPressure >= PressureLevel.Moderate || input.CpuPressure || input.System.OnBattery);
        ResourceAction action = eligible ? ResourceAction.LowerMemory |
            (input.Settings.CpuManagement ? ResourceAction.LowerCpu : 0) |
            (input.Settings.EcoQos ? ResourceAction.EcoQos : 0) |
            (input.Settings.CpuSets ? ResourceAction.CpuSets : 0) : ResourceAction.None;
        return new(action, protection ?? (eligible ? "v1-eligible" : "v1-idle-or-pressure-gate"), PolicySafety.Classify(input), []);
    }
}
public sealed class FixedResourcePolicy : IResourcePolicy
{
    public string Name => "fixed";
    public string Version => "1.0";
    public PolicyDecision Decide(PolicyInput input) {
        string? guard = PolicySafety.Protection(input, explicitOnly: true);
        var action = guard is null ? (input.Settings.CpuManagement ? ResourceAction.LowerCpu : 0) |
            (input.Settings.EcoQos ? ResourceAction.EcoQos : 0) : ResourceAction.None;
        return new(action, guard ?? "fixed-explicit-cpu-eco", PolicySafety.Classify(input), []);
    }
}
