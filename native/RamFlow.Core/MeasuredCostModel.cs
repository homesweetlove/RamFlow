using System.Text.Json;
using System.Text.Json.Serialization;

namespace RamFlow.Core;

public sealed record CostFeatures(double CpuPercent, double AvailableFraction, double CommitFraction, double? PagesInputPerSecond)
{
    public static CostFeatures From(Snapshot s) => new(s.CpuPercent, (double)s.Available / Math.Max(1, s.RamTotal),
        (double)s.Commit / Math.Max(1, s.CommitLimit), s.PagesInput);
    public double[] Vector() => [CpuPercent / 100, AvailableFraction, CommitFraction,
        PagesInputPerSecond.HasValue ? Math.Log(1 + PagesInputPerSecond.Value) / Math.Log(10001) : -1];
    public bool Valid => double.IsFinite(CpuPercent) && CpuPercent is >= 0 and <= 100 &&
        double.IsFinite(AvailableFraction) && AvailableFraction is >= 0 and <= 1 &&
        double.IsFinite(CommitFraction) && CommitFraction is >= 0 and <= 1 &&
        (PagesInputPerSecond is null || double.IsFinite(PagesInputPerSecond.Value) && PagesInputPerSecond >= 0);
}
public sealed record CostObservation(string BlockId, string Split, string HardwareId, string WorkloadId,
    ContentionKind Contention, ResourceAction Action, CostFeatures Features, double ForegroundP95Ms,
    double BackgroundUnitsPerSecond, double ReaccessMs, double ControlCost, bool Valid);
public sealed record CostModelOptions
{
    public int MinFitBlocks { get; init; } = 3;
    public int MinValidationBlocks { get; init; } = 3;
    public int Neighbours { get; init; } = 3;
    public double SupportRadius { get; init; } = .15;
    public double MaxValidationMae { get; init; } = .15;
    public double MinQuality { get; init; } = .8;
}
public sealed record CostPoint(string BlockId, CostFeatures Features, double ForegroundGain, double BackgroundLoss, double ReaccessRisk, double ControlCost);
public sealed record CostModelCell(ContentionKind Contention, ResourceAction Action, CostPoint[] Fit,
    int ValidationBlocks, double? ForegroundMae, double? BackgroundMae, double? ReaccessMae, double? OverheadMae,
    double[]? ErrorBounds, double Quality, bool Accepted, string Reason)
{
    public double? ReferenceBackgroundUnitsPerSecond { get; init; }
}
public sealed record CostValidationPrediction(string BlockId, ContentionKind Contention, ResourceAction Action,
    double[] Actual, double[]? Predicted, double[]? AbsoluteErrors, string Status);
public sealed record CostModelDocument(int SchemaVersion, string ModelId, string HardwareId, string WorkloadId,
    CostModelOptions Options, string[] FitBlockIds, string[] ValidationBlockIds, CostModelCell[] Cells,
    string DatasetSha256, CostValidationPrediction[] ValidationPredictions,
    string Scope = "paired instrumented task effects; not physical user input latency; quality is not a probability");
public sealed record CostPrediction(IReadOnlyList<CandidateEstimate> Candidates, string Reason, string ModelId, int IndependentBlocks);

public static class CostModelTrainer
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() } };
    public static CostModelDocument Train(IEnumerable<CostObservation> observations, CostModelOptions? options = null) {
        var rows = observations.ToArray(); var config = options ?? new(); ValidateOptions(config);
        if (rows.Length == 0 || rows.Length > 100000 || rows.Any(x => x is null || x.Split is not ("fit" or "validation")))
            throw new ArgumentException("Training accepts only explicit fit/validation rows, never final test rows.");
        if (rows.Select(x => x.HardwareId).Distinct().Count() != 1 || rows.Select(x => x.WorkloadId).Distinct().Count() != 1 ||
            rows.Any(x => string.IsNullOrWhiteSpace(x.BlockId) || string.IsNullOrWhiteSpace(x.HardwareId) || string.IsNullOrWhiteSpace(x.WorkloadId)))
            throw new ArgumentException("Freeze one hardware and workload identity per model.");
        if (rows.GroupBy(x => x.BlockId).Any(x => x.Select(y => y.Split).Distinct().Count() != 1) ||
            rows.GroupBy(x => (x.BlockId, x.Contention, x.Action)).Any(x => x.Count() != 1))
            throw new ArgumentException("Session block leakage or duplicate candidate measurements.");
        if (rows.Any(x => !x.Features.Valid || !double.IsFinite(x.ForegroundP95Ms) || x.ForegroundP95Ms <= 0 ||
            !double.IsFinite(x.BackgroundUnitsPerSecond) || x.BackgroundUnitsPerSecond < 0 ||
            !double.IsFinite(x.ReaccessMs) || x.ReaccessMs <= 0 || !double.IsFinite(x.ControlCost) || x.ControlCost is < 0 or > 1 ||
            x.Action is not (ResourceAction.None or ResourceAction.LowerCpu or (ResourceAction.LowerCpu | ResourceAction.EcoQos) or
                ResourceAction.LowerMemory or (ResourceAction.LowerCpu | ResourceAction.LowerMemory) or (ResourceAction.LowerCpu | ResourceAction.EcoQos | ResourceAction.LowerMemory))))
            throw new ArgumentException("Invalid measured cost input.");
        var points = new List<(CostObservation Row, CostPoint Point)>();
        foreach (var row in rows.Where(x => x.Valid && x.Action != ResourceAction.None)) {
            if (row.Contention == ContentionKind.Cpu && row.Action.HasFlag(ResourceAction.LowerMemory) ||
                row.Contention == ContentionKind.Ram && row.Action.HasFlag(ResourceAction.LowerCpu) || row.Contention == ContentionKind.Unknown) continue;
            var baseline = rows.SingleOrDefault(x => x.BlockId == row.BlockId && x.Contention == row.Contention && x.Action == ResourceAction.None && x.Valid);
            // Missing pairs and zero baseline progress are retained in raw data,
            // but cannot yield a well-defined relative throughput response.
            if (baseline is null || baseline.BackgroundUnitsPerSecond <= 0) continue;
            points.Add((row, new(row.BlockId, row.Features,
                Math.Clamp(1 - row.ForegroundP95Ms / baseline.ForegroundP95Ms, -1, 1),
                Math.Clamp(1 - row.BackgroundUnitsPerSecond / baseline.BackgroundUnitsPerSecond, -1, 1),
                Math.Clamp(row.ReaccessMs / baseline.ReaccessMs - 1, 0, 1), row.ControlCost)));
        }
        var cells = new List<CostModelCell>();
        var diagnostics = new List<CostValidationPrediction>();
        foreach (var group in points.GroupBy(x => (x.Row.Contention, x.Row.Action)).OrderBy(x => x.Key.Contention).ThenBy(x => x.Key.Action)) {
            var fit = group.Where(x => x.Row.Split == "fit").Select(x => x.Point).ToArray();
            var validation = group.Where(x => x.Row.Split == "validation").Select(x => x.Point).ToArray();
            var residuals = new List<double[]>(); bool supported = true;
            foreach (var item in validation) {
                var prediction = CostModelPredictor.Nearest(fit, item.Features, config);
                if (prediction is null) { supported = false; diagnostics.Add(new(item.BlockId, group.Key.Contention, group.Key.Action, Values(item), null, null, "outside-fit-feature-support")); continue; }
                var errors = Values(item).Zip(prediction, (actual, predicted) => Math.Abs(actual - predicted)).ToArray();
                residuals.Add(errors); diagnostics.Add(new(item.BlockId, group.Key.Contention, group.Key.Action, Values(item), prediction, errors, "predicted-held-out-block"));
            }
            double?[] mae = Enumerable.Range(0, 4).Select(i => residuals.Count == 0 ? (double?)null : residuals.Average(x => x[i])).ToArray();
            double[]? bounds = residuals.Count == 0 ? null : Enumerable.Range(0, 4).Select(i => Quantile(residuals.Select(x => x[i]).ToArray(), .9)).ToArray();
            double quality = bounds is null ? 0 : Math.Clamp(1 - bounds.Max(), 0, 1);
            bool enough = fit.Length >= config.MinFitBlocks && validation.Length >= config.MinValidationBlocks;
            bool accepted = enough && supported && residuals.Count == validation.Length && mae.All(x => x.HasValue && x <= config.MaxValidationMae) && quality >= config.MinQuality;
            string reason = !enough ? "insufficient-independent-fit-or-validation-blocks" : !supported ? "validation-outside-fit-support" :
                !accepted ? "validation-error-or-quality-rejected" : "held-out-validation-accepted";
            var referenceRates = rows.Where(x => x.Valid && x.Split == "fit" && x.Contention == group.Key.Contention && x.Action == ResourceAction.None)
                .Select(x => x.BackgroundUnitsPerSecond).Where(x => x > 0).ToArray();
            cells.Add(new(group.Key.Contention, group.Key.Action, fit, validation.Length, mae[0], mae[1], mae[2], mae[3], bounds, quality, accepted, reason) {
                ReferenceBackgroundUnitsPerSecond = referenceRates.Length == 0 ? null : referenceRates.Average() });
        }
        var model = new CostModelDocument(3, "", rows[0].HardwareId, rows[0].WorkloadId, config,
            rows.Where(x => x.Split == "fit").Select(x => x.BlockId).Distinct().Order().ToArray(),
            rows.Where(x => x.Split == "validation").Select(x => x.BlockId).Distinct().Order().ToArray(), cells.ToArray(), FileResearchAuditSink.Hash(rows), diagnostics.ToArray());
        return model with { ModelId = FileResearchAuditSink.Hash(model) };
    }
    internal static double[] Values(CostPoint p) => [p.ForegroundGain, p.BackgroundLoss, p.ReaccessRisk, p.ControlCost];
    internal static double Quantile(double[] values, double q) {
        if (values.Length == 0) return 1;
        var ordered = values.Order().ToArray(); int at = Math.Clamp((int)Math.Ceiling(q * ordered.Length) - 1, 0, ordered.Length - 1); return ordered[at];
    }
    internal static void ValidateOptions(CostModelOptions o) {
        if (o.MinFitBlocks is < 3 or > 10000 || o.MinValidationBlocks is < 3 or > 10000 || o.Neighbours is < 3 or > 100 ||
            !double.IsFinite(o.SupportRadius) || o.SupportRadius is <= 0 or > .5 || !double.IsFinite(o.MaxValidationMae) || o.MaxValidationMae is <= 0 or > 1 ||
            !double.IsFinite(o.MinQuality) || o.MinQuality is < .5 or > 1) throw new ArgumentException("Invalid cost-model support/quality configuration.");
    }
    public static bool Verify(CostModelDocument model) {
        try {
            ValidateOptions(model.Options);
            return model.SchemaVersion == 3 && model.Cells is not null && model.FitBlockIds is not null && model.ValidationBlockIds is not null && model.ValidationPredictions is not null &&
                !model.FitBlockIds.Intersect(model.ValidationBlockIds).Any() && model.ModelId == FileResearchAuditSink.Hash(model with { ModelId = "" }) &&
                model.Cells.All(c => c.Fit is not null && c.Fit.Select(p => p.BlockId).Distinct().Count() == c.Fit.Length &&
                    c.Fit.All(p => p.Features.Valid && Values(p).All(double.IsFinite)) && double.IsFinite(c.Quality) && c.Quality is >= 0 and <= 1 &&
                    (!c.Accepted || c.Fit.Length >= model.Options.MinFitBlocks && c.ValidationBlocks >= model.Options.MinValidationBlocks &&
                        c.ErrorBounds is { Length: 4 } && c.ErrorBounds.All(x => double.IsFinite(x) && x >= 0) && c.Quality >= model.Options.MinQuality));
        } catch (Exception) { return false; }
    }
    public static void SaveNew(CostModelDocument model, string path) {
        if (!Verify(model)) throw new ArgumentException("Unverified cost model.");
        using var file = new FileStream(path, FileMode.CreateNew); JsonSerializer.Serialize(file, model, Json); file.Flush(true);
    }
    public static CostModelDocument Load(string path) {
        using var file = File.OpenRead(path); if (file.Length > 32 * 1048576) throw new IOException("Cost model too large.");
        var model = JsonSerializer.Deserialize<CostModelDocument>(file, Json) ?? throw new JsonException("Null model.");
        if (!Verify(model)) throw new JsonException("Cost model identity or schema failed verification."); return model;
    }
}

public static class CostModelPredictor
{
    // The radius uses fixed normalized sensor units, not a claimed gain or
    // learned success probability. Never extrapolate to an unobserved machine.
    internal static double[]? Nearest(CostPoint[] points, CostFeatures query, CostModelOptions options) {
        if (!query.Valid) return null;
        var q = query.Vector();
        double Distance(CostPoint point) {
            var v = point.Features.Vector(); if ((v[3] < 0) != (q[3] < 0)) return double.PositiveInfinity;
            return v.Zip(q, (x, y) => Math.Abs(x - y)).Max();
        }
        var near = points.Select(p => (Point: p, Distance: Distance(p))).Where(x => x.Distance <= options.SupportRadius)
            .OrderBy(x => x.Distance).ThenBy(x => x.Point.BlockId).Take(options.Neighbours).Select(x => x.Point).ToArray();
        if (near.Length < options.Neighbours || near.Select(x => x.BlockId).Distinct().Count() < options.Neighbours) return null;
        return Enumerable.Range(0, 4).Select(i => near.Average(x => CostModelTrainer.Values(x)[i])).ToArray();
    }
    public static CostPrediction Predict(CostModelDocument model, string hardwareId, string workloadId, ContentionKind kind, CostFeatures features) {
        CostPrediction Reject(string reason) => new([], reason, model.ModelId, 0);
        if (!CostModelTrainer.Verify(model)) return Reject("unverified-model");
        if (hardwareId != model.HardwareId || workloadId != model.WorkloadId) return Reject("hardware-or-workload-outside-training-domain");
        if (!features.Valid) return Reject("missing-or-invalid-sensors");
        var candidates = new List<CandidateEstimate>(); int blocks = int.MaxValue;
        foreach (var cell in model.Cells.Where(x => x.Contention == kind && x.Accepted)) {
            var point = Nearest(cell.Fit, features, model.Options); if (point is null) continue;
            // Conservative one-sided margins from held-out empirical errors.
            // These are calibration margins, not formal statistical CIs.
            double gain = Math.Clamp(point[0] - cell.ErrorBounds![0], 0, 1);
            double loss = Math.Clamp(point[1] + cell.ErrorBounds[1], 0, 1);
            double reaccess = Math.Clamp(point[2] + cell.ErrorBounds[2], 0, 1);
            double overhead = Math.Clamp(point[3] + cell.ErrorBounds[3], 0, 1);
            candidates.Add(new(cell.Action, gain, loss, reaccess, overhead, cell.Quality));
            blocks = Math.Min(blocks, cell.Fit.Select(x => x.BlockId).Distinct().Count());
        }
        return candidates.Count == 0 ? Reject("no-validated-candidate-within-feature-support") : new(candidates, "validated-local-neighbour-estimates", model.ModelId, blocks);
    }
}
