# RamFlow — Reproducible Measurement Plan

> **Status:** Experimental protocol and blank reporting template. **No new benchmark results are claimed in this document.**
>
> For context, see [Engineering Case Study](ENGINEERING_CASE_STUDY.md) / [한국어 사례 연구](ENGINEERING_CASE_STUDY.ko.md) and the [previous validation record](VALIDATION.md).

The [on-demand and independent benchmark guide](BENCHMARK.md) documents the implementation and commands. Compare built-in measurement OFF versus ON separately from the A/B/C conditions below. Include the external observer's CPU, private memory and sampling time in every result; its own collection is additional experimental overhead. The external tool does not switch RamFlow modes and reports unverifiable labels explicitly.

## 1. Questions to test

1. Does enabling RamFlow improve *foreground task latency or completion time* relative to not running it?
2. Does it reduce memory pressure without increasing disk page-in or causing work interruption?
3. What CPU and memory overhead does the optimizer add when no intervention is needed?
4. Are restoration and safety guarantees maintained under rapid task switching and elevated memory pressure?

**Important:** More “available RAM” or a smaller background working set alone does not establish improved responsiveness.

## 2. Three comparison conditions

| Condition | Behavior | Reason |
| --- | --- | --- |
| A — Baseline | RamFlow not running | Measure the Windows behavior without tool overhead |
| B — Dry Run | RamFlow running; default Dry Run enabled | Distinguish observation/UI overhead from interventions |
| C — Enabled | RamFlow running with intentionally approved resource changes | Measure the net effect of the guarded policy |

Never include unsaved documents or production workloads in condition C. Read [README](../README.md) and the permissions/restore description before turning on real intervention. **No process-termination, pagefile, service-install or model-archive actions are required** for this protocol.

## 3. Repeatable workloads

Pick one workload, define the exact sequence, and keep it unchanged across A/B/C.

- **Constrained development (recommended):** On a documented low-RAM machine, open the same browser tabs, editor project and AI tool; switch between two known windows and complete the same scripted action.
- **Cold app launch:** Start the same installed editor/browser instance, measure the elapsed time until a defined ready state. Control cold/warm cache conditions rather than assuming they are identical.
- **Long-running mixed load:** Run a disposable compilation or media job while conducting a fixed foreground interaction sequence. Do not use user files or real work that could be disrupted.

A **working-set trim may make subsequent app activity slower** due to page-in. Record that failure mode even when free RAM increases.

## 4. Control variables and collection

Document before each run: Windows build, CPU/RAM/storage type, background startup apps, power plan/battery, RamFlow version, configuration, pagefile policy, screen resolution, cache/warm-up state, test time, and workload steps.

Use **at least five trials per condition** as a target for the next study; alternate or randomize A/B/C order to limit time-of-run effects. A single run is never a performance result.

Capture at minimum:
- Foreground task completion time (ms) and whether it succeeded.
- Page-input activity or hard faults, with the exact Windows counter name and sampling interval.
- Commit used/limit, available physical memory, CPU usage.
- RamFlow **peak working set and private memory** in B/C.
- Whether a policy adjustment occurred, when it occurred, and the recorded restoration status.
- Unexpected pauses, application errors, security warnings or changes to user settings.

Record raw samples in an independent CSV or JSON, not just screenshots. Hash or version the workload script when practical. Use the same version and measurement definition across comparisons.

## 5. Suggested result template (fill only after measurement)

| Trial | Workload | Condition | Foreground latency ms | Completed? | Page inputs/s (counter) | Available RAM MiB | RamFlow private MiB | Intervention & restore notes |
| --- | --- | --- | ---: | --- | ---: | ---: | ---: | --- |
| 1 | _TBD_ | A | — | — | — | — | n/a | — |
| 1 | _TBD_ | B | — | — | — | — | — | — |
| 1 | _TBD_ | C | — | — | — | — | — | — |

After collecting samples, report median and variability (range, interquartile range, or confidence interval) **per condition**, differences relative to baseline, and negative effects. Never conflate application private memory with total RAM or process page-input counts with individual hard-fault *events*.

## 6. Existing v0.3.3 → v0.3.4 process-footprint note

The [validation document](VALIDATION.md) reports a **three-run WPF launch and nine-page navigation comparison on one machine**, yielding medians:

| RamFlow process metric | v0.3.3 | v0.3.4 |
| --- | ---: | ---: |
| Peak resident memory | 347.6 MiB | 165.0 MiB |
| Private memory | 263.5 MiB | 80.6 MiB |

These are already-recorded observations, **not** results of the A/B/C experiment proposed here. Raw repetitions, exact hardware metadata and instrumentation details are not included in the current repository, so the published figures should remain clearly labeled as self-reported process measurements.

## 7. Verification steps before publishing new conclusions

From the repository root on a **Windows machine** with .NET 10 and Python:

```powershell
dotnet run --project native/RamFlow.Tests -c Release
dotnet run --project native/RamFlow.Tests -c Release -- --windows --ipc
dotnet run --project native/RamFlow.Storage.Tests -c Release
./scripts/build-native.ps1
```

These existing tests use mock or isolated operations as documented in the [README](../README.md). Do not equate a green automated test with demonstrated user-workload performance. Attach an actual CI run URL and date to any reported test outcome.

**Publication rule:** claims of improved responsiveness require both measured task latency and evidence that real work was not harmed; if an experiment shows regression, publish the regression and revise the policy.
