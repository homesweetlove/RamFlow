# Paired comparison experiment skeleton

`../research_matrix.py` is a Python 3.10+ standard-library CLI for planning and
recording A–F comparisons. Its default action writes a plan. It starts no load,
engine, application or observer and changes no application settings. It does run
read-only Git queries to record source provenance. No experiment results or
latency measurements are included.

| Condition | Declared policy |
| --- | --- |
| A | No engine |
| B | Dry run |
| C | Fixed policy |
| D | V1 |
| E | CARP |
| F | CARP ablation, with the removed mechanism explicitly defined |

The supplied example marks **every native/workload adapter unimplemented** and
contains no commands. These labels do not configure or verify an engine. In
particular, this tool does not stop an existing engine to establish A.

## Plan and design

From the repository root, with a previously nonexistent output directory:

```powershell
python -B scripts/research_matrix.py --config scripts/research/plan.example.json --output work/research/plan-001
```

`--config` is optional for planning; its absence uses the same inert defaults.
`--seed INTEGER`, `--blocks INTEGER` and `--workloads cpu ram mixed healthy`
select the schedule. The default is six blocks **per workload**, six conditions
per block: 24 session blocks and 144 trials. `--blocks 12` gives two complete
counterbalanced cycles. A smaller positive count is permitted and explicitly
recorded as a partial cycle; it has no full carryover-balance guarantee.

Each workload uses a randomized Williams crossover design. Within each complete
six-block cycle, each condition occupies each position once and each directed
within-block predecessor pair occurs once. Treatment labels and row order are
randomized from the seed. Across workloads, cyclic order is randomized and
rotated in groups of four blocks (or the selected workload count). Transitions
between session blocks are outside the carryover-balance claim. A reset is
still needed: balancing does not remove persistent effects.

All six conditions in a block share a `block_id` and `session_seed`. Adapters
must use that seed to reproduce the same fixture/task mix and fresh state in
each condition; a common seed alone does not establish equivalence. Each block
has a different fixture seed. `cpu`, `ram`, `mixed` and `healthy` are declared
pressure regimes. Their generators and pressure verification are unimplemented.
Workload `kind` separately declares `synthetic` or `real`; a synthetic trial is
never relabeled a real-application result.

The **paired session block is the analysis unit**. Requests, frames or samples
within a session are correlated observations, not independent replications.
Compute workload-owned session summaries first, then paired condition contrasts
within blocks. Inspect period and carryover effects. Keep missing/failed blocks
visible; do not substitute zero, silently discard bad trials, pool request
counts as sample size, or infer efficacy from this planner's output. No
statistical estimator or latency extractor is implemented here.

## Explicit command contract

Execution requires both `--execute` and an explicitly supplied `--config`:

```powershell
python -B scripts/research_matrix.py --config work/research/local.json --execute --output work/research/execute-001
```

Copy and edit the example first. For each runnable condition, set
`adapter` to `"explicit-command"` and supply `command` as a JSON argv array,
for example the following **schema illustration**, whose adapter is not shipped:

```json
{
  "adapter": "explicit-command",
  "command": ["C:/Experiment/adapter.exe", "--condition", "{condition}", "--regime", "{workload}", "--seed", "{session_seed}", "--result", "{evidence_path}"],
  "definition": "Pin exact engine, policy parameters, fixture and correctness contract here"
}
```

No command is synthesized from a label. Commands use `shell=False`, receive
closed standard input and have separate binary stdout/stderr log files. Shell
strings and empty argv arrays are rejected. Shell metacharacters are ordinary
arguments; use an explicit interpreter executable if your own adapter needs one.

The default command working directory is the newly created trial directory.
An optional condition `cwd` is resolved relative to the supplied config's
directory. Executable lookup follows normal subprocess/PATH behavior; use
absolute executable and fixture paths for reproducibility.

Literal tokens in arguments are replaced: `{condition}`, `{workload}`,
`{block_id}`, `{block_index}`, `{cycle_index}`, `{position}`, `{trial_id}`,
`{session_seed}`, `{trial_dir}` and `{evidence_path}`. Block and cycle indexes
are zero-based; position is one-based. Other braces remain literal. No shell
expansion, expression evaluation or environment interpolation is performed.

Missing condition commands are recorded as `missing_command` for every affected
trial, even if every command is missing. Supplied commands with an
`unimplemented` adapter are recorded as `unimplemented_adapter` and skipped.
Partial configurations may execute their supplied adapters while recording all
missing conditions; their blocks remain incomplete. An execute request without
a config is rejected before a run directory is created.

## Reset, warmup and measurement

The per-trial sequence is **reset → warmup → optional observer launch → workload
command → observer finalization**. `protocol.timeout_seconds` is a finite
positive timeout per command phase. Reset and warmup each require a nonempty
description and one of these modes:

| Mode | Semantics |
| --- | --- |
| `unimplemented` | Preparation is unspecified; skip the workload as `unimplemented_setup`. |
| `none` | Deliberately perform no preparation. Record why, and the resulting carryover/cache limitation. |
| `external` | Preparation is user-declared and unverified; no command is executed by the runner. |
| `command` | Execute the supplied argv list before advancing; stop this trial on any phase failure. |

Only `command` mode may have a non-null command. The runner never invents a
cooldown, clears caches, restarts applications or verifies restoration. The
supplied adapter must own setup and final cleanup, including its own children,
and provide restoration/correctness evidence. If needed, a reset/warmup command
can choose condition-specific behavior using the condition token. An early
failure leaves later phases unrun and preserves the failing phase's exit code.

For reset and warmup, `{evidence_path}` names `reset.outcome.json` and
`warmup.outcome.json`; for the measured workload it names `outcome.json`.
Setup evidence cannot stand in for workload evidence. If setup has already
created `outcome.json`, the workload is skipped as `preexisting_evidence` and
that file is retained. No existing evidence file is deleted or overwritten by
the runner.

The workload must write a nonempty JSON object at its evidence path. Raw bytes
and their hash are retained as `recorded_unvalidated`; the runner does not
certify correctness or trust a reported latency. Include actual task completion,
correctness, fixture identity, timing source/units, raw outcomes and any
background-throughput/restoration evidence in your adapter's contract. A zero
exit code alone yields `missing_evidence`; malformed/nonfinite JSON yields
`invalid_evidence`. Failed commands retain any partial outcome, even when it is
malformed. `latency_ms` always remains `null`. Monotonic
`process_wall_seconds` describes orchestration, including process lifetime and
waiting/finalization; it is not application-ready or request latency.

## Optional independent observer

The implementation reuses `../benchmark-observer.py` unchanged as a sibling
process, with a unique per-trial output prefix. It is disabled by default and
requires Windows when enabled. It is launched only with an explicit executable
workload command; it never receives `--workload` and cannot own or terminate the
workload. `observer.duration_seconds` must be positive, `sample_seconds` at least
two, and `measurement` either `off` or `on`. The last value declares the built-in
benchmark's state; it does not switch it.

`observer.by_condition` may override observer settings for individual conditions,
including `enabled`, explicit `ramflow_pids` and `settings_pipe`. A maps to
`absent` with built-in measurement `off` and no engine PID/pipe. B maps to
`dry-run`; C–F map to `intervention` and each enabled intervention observer
requires `acknowledge_existing_intervention: true`. This acknowledges observation
of an already prepared condition; it performs no switch.

Preserve the observer's resource/overhead counters and checkpoint coverage.
Its coarse checks cannot distinguish fixed, V1, CARP or ablation policies;
the runner's condition labels remain `user_declared_unverified`. Observer
verification also does not prove restoration or the absence of differently
named engines. Supply separate policy provenance.

Observation has an independent fixed-duration window. Launch readiness is not
synchronized with the workload and full overlap is not guaranteed. Compare
timestamps before interpreting counter coverage. Finalization waits until the
observer's configured duration plus a ten-second startup/export allowance;
after that it records timeout and stops its direct observer child. Observer
errors, missing/rejected reports, logs and return codes are retained, and a
successful workload with unusable observer evidence is `observer_failed`.

## Run artifacts and failure handling

Each invocation reserves a **new** directory before executing any condition.
Existing directories, including incomplete or planned runs, are refused. There
is no resume, retry-in-place or conversion of an existing plan to execution.
Use a new path for every attempt; use the same config/seed for the same order.

The manifest lists exact SHA-256 hashes for `schedule.json`, `schedule.csv`, the
resolved `config.json`, the original config bytes in `config.supplied.json`,
selected source snapshots (including the existing observer and benchmark
scripts), and optional binary snapshots. It records Git HEAD, branch, tracked
dirty state, Python version and platform. Git failures, missing source files
and missing binaries have explicit status/reason fields. Git metadata is not a
clean-tree guarantee in a shared tree; source snapshots identify the actual
selected files. Additional workload, engine and fixture provenance belongs in
the supplied config and evidence.

Use repeatable `--binary PATH` arguments or config `binaries` paths to copy and
hash binaries; they are never executed by this option. Config paths are relative
to the config directory; CLI paths are relative to the invocation directory.
`manifest.sha256` identifies the final manifest bytes.

On execution, each trial retains its start record, phase command/cwd/PID,
UTC timestamps, actual child return codes, raw stdout/stderr and their hashes,
phase final records, workload outcome and final `result.json`. Missing commands
and unrun aborted trials also have final records. `run.result.json` lists every
trial status, result hashes and block completeness; `run.result.sha256` hashes
that summary. A `complete` block means all six commands/evidence recordings
completed, not that conditions or application correctness were independently
verified.

On timeout or Ctrl+C, only directly created children are terminated, then killed
if needed after a two-second grace period. Actual return codes and partial logs
are retained; subsequent trials after an interrupt are `not_run_aborted`.
Existing user processes are never discovered or stopped by this runner.
Descendants and persistent effects of explicitly supplied commands require
adapter-owned cleanup. A hard kill/power loss can leave only start records and
partial streams; preserve that directory as an incomplete attempt.

All runner-owned artifacts use exclusive creation. Logs remain writable while
their child runs; completed records are never revised by the CLI. This is an
application-level write-once archive with integrity hashes, not filesystem
tamper protection. Supplied programs can write arbitrary files, so they must
respect this archive and finish their children before returning.

Exit codes: `0` for a written plan or execution with every trial recorded as
completed, `2` for invalid configuration, existing output, missing commands or
evidence, and trial/observer failures, `130` for an interrupted execution.

## Safe tests

```powershell
python -B -m unittest discover -s scripts -p test_research_matrix.py -v
```

Only temporary files under `work/` and disposable Python test children are used.
Tests never run a benchmark, engine, installed adapter, real application or the
Windows observer. Observer lifecycle tests substitute a disposable test child.
Coverage includes determinism, complete pairing, position/carryover balance,
write-once behavior, missing commands/evidence, separate setup outcomes, stream
preservation, launch errors, timeout and interrupt handling. These checks prove
runner behavior, not engine efficacy or the validity of a real experiment.
