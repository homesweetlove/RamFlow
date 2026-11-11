using System.Diagnostics;
using System.Text.Json;
using RamFlow.Core;
using ResearchLab;

if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("ResearchLab requires Windows; no measurements were generated."); return 2; }
try {
    if (args.Length == 0) throw new ArgumentException("Use worker | trial | calibrate | train | validate.");
    if (args[0] == "observer") return await ObserverWorker.Run(args);
    if (args[0] == "worker") return await FixtureWorker.Run(args);
    if (args[0] == "app-session") return await AppStudy.Run(args);
    if (args[0] == "train") {
        string root = Path.GetFullPath(Options.Get(args, "--input", "")), output = Options.Get(args, "--model", "");
        var rows = Directory.GetFiles(root, "cost-observation.json", SearchOption.AllDirectories)
            .Select(path => { var row=JsonSerializer.Deserialize<CostObservation>(File.ReadAllText(path))!;
                EvidenceJournal.AssertSealedForTraining(Path.GetDirectoryName(path)!,row.WorkloadId.EndsWith(":evidence-v1",StringComparison.Ordinal));return row; }).ToArray();
        if (rows.Length == 0) throw new ArgumentException("No measured cost observations found; no model was produced.");
        var groups = rows.GroupBy(x => (x.HardwareId, x.WorkloadId)).ToArray();
        string modelDirectory = Options.Get(args, "--output", "");
        if (output.Length == 0 && modelDirectory.Length == 0) throw new ArgumentException("Use --model NEW-file for one workload, or --output NEW-directory for all workload models.");
        if (output.Length > 0 && groups.Length != 1) throw new ArgumentException("Multiple workload/hardware identities: use --output NEW-model-directory.");
        if (modelDirectory.Length > 0) Options.NewDirectory(modelDirectory);
        var index = new List<object>();
        foreach (var group in groups) {
            var model = CostModelTrainer.Train(group);
            string destination = output.Length > 0 ? Path.GetFullPath(output) : Path.Combine(modelDirectory, model.ModelId + ".json");
            CostModelTrainer.SaveNew(model, destination);
            var entry = new { model.ModelId, model.HardwareId, model.WorkloadId, Path = destination, Cells = model.Cells.Length, Accepted = model.Cells.Count(x => x.Accepted),
                Note = "Zero accepted cells is a preserved calibration failure, not a useful controller." };
            index.Add(entry); Console.WriteLine(JsonSerializer.Serialize(entry));
        }
        if (modelDirectory.Length > 0) Options.WriteNew(Path.Combine(modelDirectory, "index.json"), index);
        return 0;
    }
    if (args[0] == "validate") {
        var model = CostModelTrainer.Load(Options.Get(args, "--model", ""));
        string directory = Path.GetFullPath(Options.Get(args, "--output", "")); Options.NewDirectory(directory);
        Options.WriteNew(Path.Combine(directory, "validation.json"), new { model.ModelId, model.DatasetSha256, model.FitBlockIds,
            model.ValidationBlockIds, model.Cells, model.ValidationPredictions, Note = "Held-out calibration diagnostics, not final policy superiority." });
        return 0;
    }
    if (args[0] == "trial") { var result = await Trial.Run(args); Console.WriteLine(JsonSerializer.Serialize(result)); return result.Status == "completed" ? 0 : 1; }
    if (args[0] == "calibrate") {
        string root = Path.GetFullPath(Options.Get(args, "--output", "")); Options.NewDirectory(root);
        using var batchEvidence=new EvidenceJournal(root,args);
        try {
        int fit = Options.Int(args, "--fit-blocks", 3, 3, 100), validation = Options.Int(args, "--validation-blocks", 3, 3, 100);
        string[] regimes = Options.Get(args, "--regimes", "cpu,ram,mixed,healthy").Split(',');
        if (regimes.Any(x => x is not ("cpu" or "ram" or "mixed" or "healthy"))) throw new ArgumentException("Unknown intended regime.");
        int[] actions = Options.Get(args, "--actions", "0,1,3,4,5,7").Split(',').Select(int.Parse).ToArray();
        if (!actions.Contains(0) || actions.Distinct().Count() != actions.Length || actions.Any(x => x is not (0 or 1 or 3 or 4 or 5 or 7))) throw new ArgumentException("Include a unique no-action baseline and valid candidates.");
        int seed = Options.Int(args, "--seed", 20261110, 0, int.MaxValue); var random = new Random(seed);
        int maxFailures = Options.Int(args, "--max-failures", 2, 1, 100);
        var schedule = new List<(string Regime, string Split, string Block, int BlockIndex, int Seed, int Action, string Directory)>();
        foreach (string regime in regimes) for (int n = 0; n < fit + validation; n++) {
            string split = n < fit ? "fit" : "validation", block = Guid.NewGuid().ToString("N"); int blockSeed = random.Next();
            foreach (int action in actions.OrderBy(_ => random.Next())) schedule.Add((regime, split, block, n, blockSeed, action, $"{regime}-{split}-{n}-{action}"));
        }
        Options.WriteNew(Path.Combine(root, "manifest.json"), new { Seed = seed, FitBlocks = fit, ValidationBlocks = validation, Regimes = regimes, Actions = actions, MaxFailures = maxFailures,
            Schedule = schedule.Select(x => new { x.Regime, x.Split, x.Block, x.BlockIndex, x.Seed, x.Action, x.Directory }),
            Scope = "Disposable synthetic task calibration; observed regime may differ from intended pressure. No global memory purge or user app modification." });
        var results = new List<object>(); int failure = 0;bool allCleanup=true;
        int attempted = 0;
        foreach (var trial in schedule) {
                var childArgs = args.Concat(new[] { "--condition", "calibration", "--candidate", trial.Action.ToString(), "--block", trial.Block,
                    "--split", trial.Split, "--regime", trial.Regime, "--seed", trial.Seed.ToString() }).ToArray();
                // Last occurrence wins for generated immutable trial options.
                childArgs = Options.Replace(childArgs, "--output", Path.Combine(root, trial.Directory));
                batchEvidence.Write("trial-start",new { trial.Directory,trial.Block,trial.Split,trial.Action },true);
                var result = await Trial.Run(childArgs); results.Add(new { result.Status, result.Directory, result.Regime, result.RegimeAttained,result.CleanupCompleted });
                allCleanup &= result.CleanupCompleted;
                batchEvidence.Write("trial-end",new { result.Status,result.Directory,result.CleanupCompleted },true);
                attempted++;
                if (result.Status != "completed") failure++;
                Console.WriteLine($"{trial.Regime} {trial.Split} block {trial.BlockIndex + 1} action {trial.Action}: {result.Status}; observed {result.Regime}");
                if (failure >= maxFailures) break;
        }
        Options.WriteNew(Path.Combine(root, "run.result.json"), new { Status = failure == 0 ? "completed" : attempted < schedule.Count ? "aborted" : "incomplete", Failures = failure,
            Planned = schedule.Count, Attempted = attempted, Unstarted = schedule.Skip(attempted).Select(x => new { x.Directory, x.Regime, x.Split, x.Action }), Trials = results });
        batchEvidence.Seal(failure==0?"completed":"aborted",allCleanup);return failure == 0 ? 0 : 1;
        } catch(Exception error) {
            batchEvidence.TryWrite("failure",new { error=error.ToString() });
            if(!File.Exists(Path.Combine(root,"batch.failure.json"))) EvidenceJournal.AtomicNew(Path.Combine(root,"batch.failure.json"),
                new { status="failed",error=error.ToString(),plan_created=File.Exists(Path.Combine(root,"manifest.json")),time_utc=DateTimeOffset.UtcNow });
            if(!File.Exists(Path.Combine(root,"completion.json"))) batchEvidence.Seal("failed",false);
            throw;
        }
    }
    throw new ArgumentException("Unknown ResearchLab mode.");
} catch (Exception error) { Console.Error.WriteLine(error); return 2; }
