# Policy replay (no Windows actions)

Build with .NET 10 on Windows:

```powershell
dotnet run --project scripts/ResearchReplay -c Release -- --input snapshot-inputs.jsonl --output work/replay-unique
```

Input is one serialized `RamFlow.Core.PolicyInput` per line. Enums accept names or numbers. Capture the exact shared input, settings, current action and model evidence from a research host. The output directory must not exist. V1, fixed and CARP each receive the same input; CARP's history is reset when its options change. The tool never calls a Windows setter or starts a workload. Its `DecisionWallMs` is replay computation time, not user input latency. Engine-only learned protection, final live foreground checks, queue admission and original-value restoration are outside this replay.

Output includes all inputs, policy/version, candidates/reasons, `Applied=false`, input/Core hashes and a completion manifest. Parse failures preserve preceding output and `failure.json`; an existing directory is refused. ResourceAction flags: None=0, LowerCpu=1, EcoQos=2, LowerMemory=4, CpuSets=8. CARP disallows CpuSets. ContentionKind: Unknown=0, Healthy=1, Cpu=2, Ram=3, Mixed=4.

The live engine also notifies CARP of actual apply/restore transitions through `OnActionChanged`, including foreground restoration between ticks. This snapshot-only tool infers changes from `CurrentAction` and does not replay intervening lifecycle events. It is a decision comparison tool, not an exact engine-history reconstruction.

The models must be supplied and independently validated. Synthetic fixtures used by unit tests are never calibration or observed performance evidence. CARP with missing evidence returns no action. See [the specification](../../docs/research/CARP.ko.md) and [experiment runner](../research/README.md).
