# External benchmark observer

This guide describes the implemented standalone `scripts/benchmark-observer.py` and native manual benchmark session. It contains an experiment protocol, not performance results. The observer needs only Python 3.10+ on Windows, with no pip packages, RamFlow imports, installation, or running engine. Its mocked tests also run on Linux.

Use this alongside [MEASUREMENT_PROTOCOL.md](MEASUREMENT_PROTOCOL.md). The external observer measures resource use around a workload independently of RamFlow's built-in benchmark. It never activates intervention, applies settings, invokes the built-in benchmark, trims memory, installs services, changes pagefile configuration, or stops RamFlow.

## Two independent controls

`--measurement off|on` declares whether the **built-in RamFlow benchmark** is externally disabled or enabled by the operator for this trial. It does not enable or disable the external observer. The observer cannot set or prove this state: the current native settings response contains no built-in measurement-state field. Always report measurement state as **USER DECLARED / unverified**, including when a settings pipe is readable. Record the operator's exact OFF/ON procedure and active time window.

External observer activity is controlled separately:

| Observer mode | Selection | Behavior |
| --- | --- | --- |
| Periodic, manual | Omit `--duration` | Enter starts the window; Enter stops it. Metrics and condition checks run periodically. |
| Periodic, timed | `--duration SECONDS`, greater than zero | Starts automatically and stops after the requested window. No Enter prompts are required. |
| Endpoint-only, manual | `--duration 0` | Enter starts the window; Enter stops it. No periodic metrics or periodic condition polling. Startup, start, first-sample, and end condition checks and two endpoint counter reads remain. |

`--duration 0` is not an immediate exit or a zero-cost observer. Do not combine it with an expectation of automatic start/stop. Measurement OFF still collects periodic external metrics unless endpoint-only mode is explicitly selected.

## Command interface

Run from the repository root on Windows with Python:

```text
python scripts/benchmark-observer.py
  --condition {absent,dry-run,intervention}
  --measurement {off,on}
  --output PREFIX
  [--ramflow-pid PID [PID ...]] ...
  [--settings-pipe NAME]
  [--acknowledge-existing-intervention]
  [--sample-seconds N]
  [--duration SECONDS]
  [--workload EXE ARG ...]
```

| Argument | Meaning |
| --- | --- |
| `--condition` | Required declared RamFlow condition; see verification rules below. |
| `--measurement` | Required operator declaration of built-in measurement OFF/ON; always unverified. |
| `--output PREFIX` | Required output prefix. Use a unique prefix per trial and retain the emitted artifacts. Report serialization happens only after observation stops. |
| `--ramflow-pid` | One or more explicit process IDs per occurrence; repeatable append lists (`nargs='+'`). For example, `--ramflow-pid 4100 4200 --ramflow-pid 4300`. These identify the processes to measure and the allowed pipe-server PID list. |
| `--settings-pipe NAME` | Optional explicit local `RamFlow.Native.*` name, without `\\.\pipe\`. Record the actual name and server PID before the trial. |
| `--acknowledge-existing-intervention` | Required for `intervention`. Acknowledges the warning that the observer watches an already enabled configuration; grants no activation or settings-change behavior. |
| `--sample-seconds N` | Requested periodic interval in seconds; default `2`, minimum `2`. Actual gaps may be longer under load. Does not create periodic collection in endpoint-only mode. |
| `--duration SECONDS` | Nonnegative observation duration. Omitted means manual; positive means automatic; zero means the special manual endpoint-only mode. |
| `--workload EXE ARG ...` | Optional workload command. Uses argument remainder parsing (`REMAINDER`); put this option **last**, because every following token belongs to the workload. Quote paths and arguments for the invoking shell. |

Example: manually observe a baseline with periodic external collection:

```powershell
python scripts/benchmark-observer.py --condition absent --measurement off --output results/b01-absent-off
```

Example: automatically observe an existing dry-run instance for 120 seconds. Replace the example PID and SID with the real values:

```powershell
python scripts/benchmark-observer.py --condition dry-run --measurement on --output results/b01-dry-on --ramflow-pid 4100 4200 --settings-pipe 'RamFlow.Native.S-1-5-21-REPLACE' --sample-seconds 2 --duration 120
```

Example: endpoint-only external collection around an already configured intervention condition:

```powershell
python scripts/benchmark-observer.py --condition intervention --measurement off --output results/b01-intervention-off-endpoints --ramflow-pid 4100 --acknowledge-existing-intervention --duration 0
```

Example: a timed baseline launching a disposable workload, with all observer options before `--workload`:

```powershell
python scripts/benchmark-observer.py --condition absent --measurement off --output results/b01-workload --duration 120 --workload 'C:\Bench\workload.exe' --input 'C:\Bench\fixture.dat'
```

These commands are examples, not evidence of completed trials. Use unique prefixes: existing report files with the same prefix are overwritten after stop.

## Condition checks and limits

Check conditions at startup, immediately before the observation starts, at periodic checkpoints where enabled, and at the end. Rechecking at start matters because an operator can change state while a manual run waits for Enter. Preserve the outcome and verification scope of each checkpoint in bounded report state.

| Condition | Required check | Failure or uncertainty |
| --- | --- | --- |
| `absent` | Enumerate running processes and scan names `RamFlow` and `RamFlow.Service` (case-insensitive, optional `.exe` suffix). Repeat at startup/start/first-sample/periodic/end. | A match or unavailable enumeration fails the condition. Never treat failed enumeration as an empty process list. Endpoint-only mode has startup/start/first-sample/end checks only. |
| `dry-run` | If readable, the named server must report Boolean `DryRun=true` and `Paused=false`; its OS-reported server PID must belong to the explicit `--ramflow-pid` list. | Missing pipe or denied access leaves the condition **USER DECLARED / unverified**. Observed contradictory mode, pause, or server identity mismatch invalidates the trial. |
| `intervention` | Require acknowledgement and warn that intervention must already exist. If readable, require Boolean `DryRun=false`, `Paused=false`, and pipe-server PID membership in the explicit list. | Missing acknowledgement rejects the invocation. Missing pipe or denied access remains **USER DECLARED / unverified**. Observed contradiction invalidates the trial; the observer never repairs it. |

An unusable, malformed, or incomplete reply is not affirmative verification. Record the reason and checkpoint coverage; never upgrade an unverified run to verified. A contradictory observation cannot be erased by a later successful check. Stop observation and mark a failed condition invalid; the workload is still left running. Report condition validity independently of metric availability.

The current native protocol uses a message-mode named pipe. Its read-only request is `{"Command":"settings"}`, with a successful response shaped as `{"Ok":true,"Data":{"DryRun":true,"Paused":false,...}}`. An error uses `Ok=false` and `Error`. The normal pipe name is `RamFlow.Native.<user SID>`; isolated verification namespaces add `.Test.<namespace>`. See [EnginePipe.cs](../native/RamFlow.Core/EnginePipe.cs) and [Contracts.cs](../native/RamFlow.Core/Contracts.cs). The observer uses client-handle nonblocking message reads with a one-second polling deadline and a 65,536-byte reply limit. Oversized, malformed, denied, or timed-out replies cannot verify a condition.

Request only settings, never `apply` or `stop`. Obtain the server PID from the connected pipe handle using [GetNamedPipeServerProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeserverprocessid), rather than trusting a PID in JSON. A successful check describes **that named server at that checkpoint**. It does not establish the mode of other listed processes, other sessions, other engines, or the whole machine. Neither periodic checks nor endpoint checks detect every transient change between checkpoints. A dry-run check also does not certify that earlier interventions have finished restoring.

Absence has no meaningful built-in-measurement ON state. Use `--condition absent --measurement off` for that baseline. The parser permits `on` as a declaration but it cannot mean a built-in benchmark ran while RamFlow was absent. A process-name scan does not detect renamed executables, Python-hosted engines, or other differently named harnesses. If existing user RamFlow processes prevent the absent baseline, record it as unavailable; do not stop those processes for this experiment.

## Collection cost and workload lifetime

The observer uses Python `ctypes` calls to Windows APIs and constant-space online aggregates. It accepts at most 64 unique explicit PIDs, stores two endpoint frames, first/latest verification evidence plus first/latest failures, and latest process identities/status. It never retains a sample history. JSON and CSV suffixes are appended to `--output PREFIX`; writes happen only after stop. The CSV has one row per metric, at most 267 rows for 64 PIDs plus the observer; it is **not a time series**, has no sample-row truncation, and includes condition/measurement labels. Unexpected termination can lose the report.

Lower only the observer's own priority. Launch an optional workload through `Popen` with explicit `creationflags=...NORMAL_PRIORITY_CLASS`, so the observer's lowered priority is not inherited by the child. This is launch configuration, not permission to reprioritize existing applications. Windows documents low-priority inheritance in [Scheduling Priorities](https://learn.microsoft.com/en-us/windows/win32/procthread/scheduling-priorities). Record a failure to lower observer priority; do not claim the requested priority was achieved.

When sampled CPU is at least 85%, memory load at least 90%, or a collection pass costs at least one quarter of the requested interval, the next periodic wait is `max(requested interval, 10 seconds, 10 * collection cost)`. Otherwise it returns to the requested interval. No catch-up burst replaces missed samples. Timed mode sleeps until the next sample or duration boundary; manual mode checks Enter at most every 250 ms. Startup/end checks, endpoint reads, pipe requests, process scans, wakeups, and workload launch still cost resources.

JSON `observer` records `sampling_wall_seconds` moments, `sampling_wall_total_seconds`, `endpoint_cpu_delta_seconds`, `endpoint_span_seconds`, `sample_gaps_seconds`, `load_backoffs`, priority outcome, and latest per-process status. The wall-cost measurements cover periodic condition checks/counter reads and the final check/read, not initial process startup, initial verification, workload launch, serialization, or disk writes. The last endpoint is read immediately after stop and **before final condition verification**; `last_endpoint_started_seconds_after_stop`, `final_counter_read_wall_seconds`, and the endpoint span expose boundary slack separately from `elapsed_seconds`. Self-measurement is incomplete and cannot establish total observer disturbance.

Start/end define an observation window, not workload completion. Never kill the workload on elapsed duration, Enter stop, validation failure, or observer cancellation. Record its PID and observed exit status. Completion duration is `null` if it is still running, the observation ends early, or completion cannot be established. `completed_by_stop` requires the OS-recorded exit timestamp to be no later than the UTC stop cutoff; an exit racing the stop poll cannot become a completed result. Completed duration uses OS exit minus creation time, and unavailable timing leaves completion unknown. A process exiting is not proof of successful application work, and a launcher exiting is not proof that its children finished. Do not label process-creation time as application-ready latency. Use a workload-owned completion/result marker for meaningful task timing. UTC adjustments can affect OS timestamp comparisons; monotonic observation elapsed remains separate.

## Metrics, units, and missing data

Use a monotonic clock for elapsed seconds and interval denominators; wall-clock timestamps identify the trial. Store bytes as bytes, CPU time as seconds, rates with their denominators, and percentages with an explicit normalization. Convert bytes to MiB using `2^20` only for presentation.

| Metric | Source and interpretation |
| --- | --- |
| System CPU utilization | [GetSystemTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes). For valid deltas, `100 * (delta(kernel + user) - delta(idle)) / delta(kernel + user)`. Kernel includes idle; do not count it twice. On machines above 64 logical processors, the API covers the caller's primary processor group, not necessarily the entire machine. |
| Physical memory | [GlobalMemoryStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex): total/available physical bytes and approximate memory-load percent. These are changing snapshots, not memory attributable to RamFlow. |
| Commit-related estimate, if emitted | `ullTotalPageFile` and `ullAvailPageFile` describe commit availability with process-related limits. Their difference must be labeled an estimate, not a guaranteed system-wide committed-bytes counter or disk pagefile occupancy. See [MEMORYSTATUSEX](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/ns-sysinfoapi-memorystatusex). |
| Page-input rate | PDH `\Memory\Pages Input/sec`, in pages/second, system-wide. Pages read to resolve hard faults are not a per-process hard-fault event count, disk bytes/second, or exclusively pagefile reads. Use [PdhAddEnglishCounterW](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhaddenglishcounterw) for the English path and validate [formatted counter status](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhgetformattedcountervalue). Rate counters require a valid collection interval; unavailable or unprimed rates stay missing. |
| Observer and explicit RamFlow PID CPU | [GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes), kernel plus user seconds, with endpoint deltas. If reported as `100 * delta(cpu seconds) / elapsed seconds`, 100% means one logical CPU and multithreaded processes can exceed 100%. Any division by logical-CPU count must be stated separately. |
| Per-process working set/private memory | [GetProcessMemoryInfo](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getprocessmemoryinfo) and [PROCESS_MEMORY_COUNTERS_EX](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex): resident working-set bytes and private committed bytes. Private commit is not private resident RAM. Working sets can share pages; summing processes can double-count physical memory. |
| Endpoint and sampled peaks | Start/end gauges describe endpoints only. The maximum sampled value misses between-sample peaks; an API lifetime peak may predate the observation. Label each explicitly. Endpoint-only mode cannot produce a sampled trajectory. |

Missing, denied, unsupported, exited-process, invalid-counter, or invalid-delta values are `null` in JSON and empty in CSV, never fabricated zeros. Preserve a bounded reason summary and valid observation counts. Do not silently sum a subset of readable PIDs as a complete RamFlow total. Keep observer counters separate from RamFlow counters. PIDs may be reused; compare process identity/creation time and do not combine counters across process lifetimes.

Each JSON `metrics` entry and CSV metric row contains valid `count`, `missing`, arithmetic `mean`, `sample_stdev`, `min`, `max`, and first/last valid values. These are **sample-weighted**, including endpoints; they are not time-weighted averages. CPU interval values and PDH rates describe their own intervals. After backoff, averaging them arithmetically does not establish whole-window utilization. Gap moments are recorded; valid time coverage and gauge integration are not computed. Do not bridge unavailable intervals as though observed. No exact sample percentiles or discarded trajectory can be reconstructed; use independent trial-level workload outcomes for percentile analysis. Raw first/last endpoint frames remain available separately in `endpoints`.

## Native manual session

The native UI benchmark is OFF at startup. Constructing `BenchmarkSession` retains the engine reference and starts no timer, sampler thread, PDH query, or log writer. In the benchmark tab, use the explicit Start and Stop controls to observe the already selected mode; the controls do not enable intervention. Sampling runs on a lowest-priority worker thread every two seconds. At CPU >=85% or physical availability <10%, it skips detailed process/PDH reads and row retention and backs off ten seconds; the cheap memory/CPU pressure probe still has a cost. Sessions stop after at most 30 minutes and retain at most the latest 256 detailed samples; `SampleCount` can exceed the retained collection length. Settings changes invalidate comparison. A workload exit can stop the session; an unfinished workload is never terminated and has a null completion duration.

The native JSON `Before` and `After` snapshots are the **first and last sampled values**, with their own `Time` fields. The first can follow workload launch, and the last can precede Stop by the sampling/backoff interval. They are not exact launch/stop boundaries. The UI labels and timestamps them accordingly; workload duration uses the OS-recorded exit time separately.

A completed session is kept in memory until the user explicitly exports JSON/CSV. Opening the tab and idle operation do not export reports. Shutdown requests session disposal before disposing the engine. Session overhead reports sampling-thread CPU milliseconds, allocation bytes, and wall milliseconds; these exclude worker wait intervals and do not account for every remote engine-side cost. An isolated `scripts/BenchmarkLab` dry-run harness can select `--measurement off|on --seconds 30 --report PATH`; it uses the Windows provider without WPF or settings persistence. Its ON/OFF branch supplies code-level provenance for those artifacts, but the external observer still labels its own `--measurement` declaration unverified because IPC has no such field.

Native process CPU percentages divide by `Environment.ProcessorCount`: 100% represents all logical CPUs in that normalization. External `*.cpu_percent_one_core` uses **one logical CPU = 100%** and can exceed 100%; divide by the same processor count before comparing those percentages. Native and external system CPU both inherit GetSystemTimes processor-group limitations. Do not sum native UI and engine rows when they identify the same PID.

## Repeated experiment

Plan randomized repeated trials across **five meaningful cells**: absent baseline, dry-run OFF/ON, and existing intervention OFF/ON. Keep periodic external observation configured identically. The built-in ON/OFF procedure is operator-controlled and recorded separately from observer flags. Run intervention cells only when already explicitly authorized and configured; otherwise record them as unavailable.

| Cell | RamFlow condition | Built-in measurement declaration |
| --- | --- | --- |
| Baseline | Absent, checked | OFF; no running built-in benchmark |
| Dry OFF | Existing dry run | OFF |
| Dry ON | Same dry-run setup | ON |
| Intervention OFF | Existing authorized intervention | OFF |
| Intervention ON | Same intervention setup | ON |

1. Fix the workload, fixture hash, ready/completion definition, configuration, and observation-window policy before collecting data. Record Windows build, CPU/RAM/storage, power mode, battery state, build/commit, Python version, background load, cache/warm-up policy, and foreground sequence.
2. Prepare each condition outside the observer. Record exact PIDs, pipe, all enabled policy options, built-in measurement procedure, and restoration status. This observer never enables real intervention for the operator.
3. Use randomized complete blocks containing all available cells. Target at least five repetitions per cell; increase repetitions based on uncertainty rather than selecting favorable runs. Record unavailable cells, randomization seed/order, reset/cooldown procedure, and incomplete trials. A sequential alternating pilot is useful for feasibility but must be described as such; it is not a randomized experiment. Treat warm and cold cache studies separately.
4. Preserve every report, condition-check outcome, missing-data summary, workload result, and operator note. Do not pool verified and unverified conditions without identifying them. Invalid trials are excluded from condition estimates but remain in the audit record with reasons; rerun the affected cell under the predefined policy.
5. Compare trial-level task completion/latency and resource measures within blocks. Report medians, spread, paired differences, and confidence intervals with the method and number of independent trials. Repeated samples inside one run are not independent repetitions. Predeclare any acceptable regression budget; do not invent a passing result from an arbitrary target.

Measure **external observer impact in a separate paired study**, holding built-in measurement declaration, RamFlow condition, workload, and window policy fixed. Randomize endpoint-only `--duration 0` versus periodic observation (`--duration` positive, or normal manual mode). Match actual workload boundaries; use the same manual protocol on both sides when comparing manual stops. If timed versus manual boundaries differ, record that confound and do not attribute the difference solely to sampling.

Use workload-owned or another fixed external completion marker in both arms. Compare the observer's own CPU and footprint too, but recognize that self-measurement cannot capture every scheduling, cache, power, or I/O disturbance. Endpoint-only still has checks and counters and is only a reduced-observation reference. Name these arms “external endpoint-only” and “external periodic” to avoid confusing them with built-in measurement OFF/ON.

Report all observations with uncertainty. **Do not subtract estimated observer overhead from results or make causal performance claims from these comparisons.** A lower working set or increased available RAM alone does not establish faster work; include completion correctness, task latency, page-input activity, regressions, and interruption/restoration notes. This document supplies a procedure, not measured speedups or an all-machine safety guarantee.

## Run record

Use this blank checklist for each trial or an equivalent structured manifest.

| Field | Record |
| --- | --- |
| Identity | Trial/block ID, randomized order, timestamp, build and workload hashes |
| Declarations | Condition; built-in measurement OFF/ON marked USER DECLARED / unverified; operator procedure |
| Observation | Endpoint-only/manual periodic/timed periodic; requested and actual window; interval/backoff/gaps |
| Verification | Explicit PIDs and identities, pipe/server PID, startup/start/periodic/end outcomes, unverified reasons, validity |
| Outcomes | Workload result, exit/completion evidence, nullable completion duration, observation stop reason |
| Resources | Units/normalization, bounded aggregate definitions, valid coverage, missing counts, retained-row cap |
| Context | Hardware/OS/power/cache/background state, policy settings, intervention/restoration notes |
| Evidence | Raw emitted artifacts, separate workload timing/result log, deviations and invalid trials |

Run deterministic tests on Linux or Windows with `python -B -m unittest discover -s scripts -p test_benchmark_observer.py -v`. They mock APIs, clocks, workloads, and report streams; they neither launch workloads nor change live Windows state. Mock tests establish control-flow behavior; real Windows counter availability, native process state, observer impact, and performance require separately recorded native trials.
