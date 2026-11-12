using RamFlow.Core;
using System.Text.Json;

static class CostModelTests
{
    static readonly CostFeatures Features = new(50, .35, .5, 0);
    static List<CostObservation> Dataset() {
        var rows = new List<CostObservation>();
        foreach (string split in new[] { "fit", "validation" }) for (int n = 0; n < 3; n++) {
            string block = split + n;
            rows.Add(new(block, split, "hardware", "fixture", ContentionKind.Cpu, ResourceAction.None, Features, 100, 100, 10, .01, true));
            rows.Add(new(block, split, "hardware", "fixture", ContentionKind.Cpu, ResourceAction.LowerCpu, Features, 80, 90, 10, .02, true));
        }
        return rows;
    }
    public static void Run(Action<bool, string> check) {
        var model = CostModelTrainer.Train(Dataset());
        var result = CostModelPredictor.Predict(model, "hardware", "fixture", ContentionKind.Cpu, Features);
        check(result.Candidates.Count == 1 && Math.Abs(result.Candidates[0].ForegroundGain - .2) < 1e-8 &&
            Math.Abs(result.Candidates[0].BackgroundLoss - .1) < 1e-8, "Measured paired effects did not reach CARP predictions");
        check(model.Cells[0].ValidationBlocks == 3 && model.Cells[0].ForegroundMae < 1e-8, "Held-out model errors were not evaluated");
        check(CostModelPredictor.Predict(model, "another-machine", "fixture", ContentionKind.Cpu, Features).Candidates.Count == 0, "Cost model crossed hardware identity");
        check(CostModelPredictor.Predict(model, "hardware", "another-task", ContentionKind.Cpu, Features).Candidates.Count == 0, "Cost model crossed workload identity");
        check(CostModelPredictor.Predict(model, "hardware", "fixture", ContentionKind.Cpu, Features with { CpuPercent = 99 }).Candidates.Count == 0, "Cost model extrapolated outside observed feature support");
        check(CostModelPredictor.Predict(model, "hardware", "fixture", ContentionKind.Ram, Features).Candidates.Count == 0, "Cost model invented an unobserved regime");
        var wrong = Dataset(); wrong.Add(wrong[0] with { Split = "validation" });
        bool overlap = false; try { CostModelTrainer.Train(wrong); } catch (ArgumentException) { overlap = true; }
        check(overlap, "Model accepted calibration/validation session leakage");
        bool final = false; try { CostModelTrainer.Train(Dataset().Append(Dataset()[0] with { Split = "test", BlockId = "untouched-final" })); } catch (ArgumentException) { final = true; }
        check(final, "Model trainer accepted final test data");
        var missing = Dataset().Where(x => !(x.Split == "fit" && x.BlockId == "fit2"));
        check(CostModelTrainer.Train(missing).Cells.All(x => !x.Accepted), "Insufficient independent blocks produced a trusted model");
        var noisy = Dataset().Select(x => x.Split == "validation" && x.Action != ResourceAction.None ? x with { ForegroundP95Ms = 200 } : x);
        check(CostModelPredictor.Predict(CostModelTrainer.Train(noisy), "hardware", "fixture", ContentionKind.Cpu, Features).Candidates.Count == 0, "Low validation accuracy still enabled intervention");
        var invalid = Dataset(); invalid[1] = invalid[1] with { ForegroundP95Ms = double.NaN };
        bool rejected = false; try { CostModelTrainer.Train(invalid); } catch (ArgumentException) { rejected = true; }
        check(rejected, "Nonfinite measurement reached training");
        string directory = Path.Combine(Path.GetTempPath(), "RamFlow.CostModel-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "model.json"); CostModelTrainer.SaveNew(model, path);
        var loaded = CostModelTrainer.Load(path);
        check(loaded.ModelId == model.ModelId, "Frozen model roundtrip changed model identity");
        bool overwritten = false; try { CostModelTrainer.SaveNew(model, path); } catch (IOException) { overwritten = true; }
        check(overwritten, "Model freeze overwrote an existing file");
        var altered = model with { HardwareId = "tampered" };
        check(CostModelPredictor.Predict(altered, "tampered", "fixture", ContentionKind.Cpu, Features).Candidates.Count == 0, "Unverified modified model was trusted");
        var source = new CalibratedObservationSource(model, "hardware", "fixture");
        var p = new MockResourceProvider().Items[0];
        var snapshot = new Snapshot { Time = DateTimeOffset.UtcNow, CpuPercent = 50, RamTotal = 100000, Available = 35000, Commit = 50000, CommitLimit = 100000, PagesInput = 0 };
        var evidence = source.Read(p, snapshot, ContentionKind.Cpu);
        check(evidence is not null && evidence.Pid == p.Pid && evidence.Identity == p.Identity && evidence.IndependentBlocks == 3,
            "Validated model was not bound to the current PID creation identity");
        check(source.Read(p, snapshot) is null, "Observation source guessed missing engine contention context");
        var progressSource = new CalibratedObservationSource(model, "hardware", "fixture", (process, sample) =>
            new(42, "owned-job-epoch", sample.Time.AddMilliseconds(-100)));
        var measured = progressSource.Read(p, snapshot, ContentionKind.Cpu)!;
        check(measured.BackgroundCompletedUnits == 42 && measured.ObservedAt == snapshot.Time.AddMilliseconds(-100) && measured.MeasuredForegroundP95Ms is null,
            "Progress bridge restamped old progress or manufactured measured foreground latency");
        var outsideValidation = Dataset().Select(x => x.Split == "validation" ? x with { Features = Features with { CpuPercent = 99 } } : x);
        var unsupported = CostModelTrainer.Train(outsideValidation);
        check(unsupported.Cells.All(x => x.ForegroundMae is null && !x.Accepted) && unsupported.ValidationPredictions.All(x => x.Predicted is null && x.AbsoluteErrors is null),
            "Unpredictable validation rows were assigned invented prediction errors");
        Console.WriteLine("Measured cost model, held-out validation, OOD and evidence bridge regressions passed.");
    }
}
