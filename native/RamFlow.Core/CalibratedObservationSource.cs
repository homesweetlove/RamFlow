namespace RamFlow.Core;

public sealed record BackgroundProgressObservation(double CompletedUnits, string Epoch, DateTimeOffset ObservedAt, double? ReferenceUnitsPerSecond = null);

public interface IPolicyContextObservationSource : IPolicyObservationSource
{
    CarpEvidence? Read(ProcessSample process, Snapshot snapshot, ContentionKind contention);
    string Diagnostic { get; }
}
public sealed class CalibratedObservationSource : IPolicyContextObservationSource
{
    readonly CostModelDocument model;
    readonly string hardwareId, workloadId;
    readonly Func<ProcessSample, Snapshot, BackgroundProgressObservation?>? progress;
    public string Diagnostic { get; private set; } = "not-read";
    public CalibratedObservationSource(CostModelDocument model, string hardwareId, string workloadId,
        Func<ProcessSample, Snapshot, BackgroundProgressObservation?>? progress = null) {
        if (!CostModelTrainer.Verify(model)) throw new ArgumentException("Frozen model verification failed.");
        // Freeze input arrays: callers cannot silently rewrite a live model.
        this.model = System.Text.Json.JsonSerializer.Deserialize<CostModelDocument>(System.Text.Json.JsonSerializer.Serialize(model))!;
        this.hardwareId = hardwareId; this.workloadId = workloadId;
        this.progress = progress;
    }
    public CarpEvidence? Read(ProcessSample process, Snapshot snapshot) { Diagnostic = "engine-contention-context-required"; return null; }
    public CarpEvidence? Read(ProcessSample process, Snapshot snapshot, ContentionKind contention) {
        var result = CostModelPredictor.Predict(model, hardwareId, workloadId, contention, CostFeatures.From(snapshot)); Diagnostic = result.Reason;
        if (result.Candidates.Count == 0 || process.Identity <= 0) return null;
        var evidence = new CarpEvidence(process.Pid, process.Identity, snapshot.Time, result.ModelId, contention, result.IndependentBlocks, result.Candidates);
        var measured = progress?.Invoke(process, snapshot);
        if (measured is not null) evidence = evidence with { ObservedAt = measured.ObservedAt,
            BackgroundCompletedUnits = measured.CompletedUnits, BackgroundProgressEpoch = measured.Epoch,
            BackgroundReferenceUnitsPerSecond = measured.ReferenceUnitsPerSecond ?? model.Cells.FirstOrDefault(x => x.Accepted && x.Contention == contention)?.ReferenceBackgroundUnitsPerSecond };
        return evidence;
    }
}
