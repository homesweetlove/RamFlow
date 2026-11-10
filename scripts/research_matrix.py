#!/usr/bin/env python3
"""Write-once paired crossover plans; execute only explicitly supplied argv lists.

Python 3.10+ standard library. No engine imports, policy switches, built-in load
generators, or inferred latency. See scripts/research/README.md for the contract.
"""
from __future__ import annotations

import argparse
from collections import Counter
import csv
from datetime import datetime, timezone
import hashlib
import io
import json
import math
from pathlib import Path
import random
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parent.parent
CONDITIONS = {"A": "no engine", "B": "dry", "C": "fixed", "D": "V1", "E": "CARP", "F": "ablation"}
WORKLOADS = ("cpu", "ram", "mixed", "healthy")
SCHEMA_VERSION = 1


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def json_bytes(value):
    return (json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False, allow_nan=False) + "\n").encode("utf-8")


def write_new(path, data):
    """Exclusive creation, including manifests, logs, and final records; no resume."""
    with path.open("xb") as stream:
        stream.write(data)


def write_json(path, value):
    write_new(path, json_bytes(value))


def sha256_file(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def generate_schedule(seed, blocks=6, workloads=None):
    """Randomized six-treatment Williams design, replicated per workload.

    Each six-block cycle has every treatment once in every position and every
    directed within-block predecessor pair once. Partial cycles are explicitly
    incomplete. A block's six trials share its fixture seed and pairing ID.
    """
    selected = list(WORKLOADS if workloads is None else workloads)
    if type(seed) is not int or type(blocks) is not int or blocks <= 0:
        raise ValueError("seed must be an integer; blocks must be a positive integer")
    if not selected or len(set(selected)) != len(selected) or any(w not in WORKLOADS for w in selected):
        raise ValueError("workloads must be a nonempty unique selection of cpu/ram/mixed/healthy")
    rng = random.Random(seed)
    designs = {}
    # Differences modulo six are 1,4,3,2,5: all nonzero directed offsets.
    williams = (0, 1, 5, 2, 4, 3)
    for cycle in range((blocks + 5) // 6):
        for workload in selected:
            labels = list(CONDITIONS)
            rng.shuffle(labels)
            offsets = list(range(6))
            rng.shuffle(offsets)
            designs[cycle, workload] = [[labels[(x + offset) % 6] for x in williams]
                                        for offset in offsets]
    rows = []
    rotation = []
    for block_index in range(blocks):
        if block_index % len(selected) == 0:
            rotation = list(selected)
            rng.shuffle(rotation)
        shift = block_index % len(selected)
        for workload in rotation[shift:] + rotation[:shift]:
            block_id = "%s-b%04d" % (workload, block_index + 1)
            session_seed = rng.getrandbits(64)
            order = designs[block_index // 6, workload][block_index % 6]
            for position, condition in enumerate(order, 1):
                rows.append(dict(trial_id="t%05d" % (len(rows) + 1), workload=workload,
                                 block_id=block_id, block_index=block_index,
                                 cycle_index=block_index // 6, position=position,
                                 condition=condition, session_seed=session_seed))
    return rows


def default_config():
    return dict(schema_version=SCHEMA_VERSION, seed=20261110, blocks=6,
                workloads={w: dict(kind="synthetic", description="Proposed regime; no workload generator is implemented")
                           for w in WORKLOADS},
                conditions={c: dict(label=label, adapter="unimplemented", command=None,
                                    definition="Operator must supply the precise policy and workload adapter")
                            for c, label in CONDITIONS.items()},
                protocol=dict(reset=dict(mode="unimplemented", description="Define isolation/restoration between trials", command=None),
                              warmup=dict(mode="unimplemented", description="Define per-trial warmup and cache state", command=None),
                              timeout_seconds=300),
                observer=dict(enabled=False, duration_seconds=120, sample_seconds=2,
                              measurement="off", ramflow_pids=[], settings_pipe=None,
                              acknowledge_existing_intervention=False, by_condition={}),
                binaries=[])


def _object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("Duplicate JSON key: " + key)
        value[key] = item
    return value


def _reject_constant(value):
    raise ValueError("Nonfinite JSON number: " + value)


def _finite_float(value):
    number = float(value)
    if not math.isfinite(number):
        _reject_constant(value)
    return number


def read_json(value):
    return json.loads(value, object_pairs_hook=_object, parse_constant=_reject_constant,
                      parse_float=_finite_float)


def _merge(base, supplied):
    if not isinstance(supplied, dict):
        raise ValueError("Configuration sections must be JSON objects")
    for key, value in supplied.items():
        if key in base and isinstance(base[key], dict) and isinstance(value, dict):
            _merge(base[key], value)
        else:
            base[key] = value
    return base


def _argv(command, name):
    if command is not None and (not isinstance(command, list) or not command or
                               any(not isinstance(arg, str) or "\0" in arg for arg in command) or
                               not command[0].strip()):
        raise ValueError(name + " must be null or a nonempty JSON argv list of strings, never a shell string")


def _positive(value, name, minimum=0):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value <= minimum:
        raise ValueError(name + " must be finite and greater than " + str(minimum))


def validate_config(config):
    if type(config["schema_version"]) is not int or config["schema_version"] != SCHEMA_VERSION:
        raise ValueError("Unsupported schema_version")
    generate_schedule(config["seed"], config["blocks"], ["cpu"])
    for name in ("conditions", "workloads", "protocol", "observer"):
        if not isinstance(config[name], dict):
            raise ValueError(name + " must be a JSON object")
    if set(config["conditions"]) != set(CONDITIONS) or set(config["workloads"]) != set(WORKLOADS):
        raise ValueError("Only conditions A..F and workloads cpu/ram/mixed/healthy are supported")
    for name, workload in config["workloads"].items():
        if not isinstance(workload, dict) or workload.get("kind") not in ("synthetic", "real"):
            raise ValueError(name + ".kind must explicitly be synthetic or real")
    for name, condition in config["conditions"].items():
        if not isinstance(condition, dict) or condition.get("adapter") not in ("unimplemented", "explicit-command"):
            raise ValueError(name + ".adapter must be unimplemented or explicit-command")
        _argv(condition.get("command"), name + ".command")
        if condition.get("cwd") is not None and not isinstance(condition["cwd"], str):
            raise ValueError(name + ".cwd must be a path string")
    protocol = config["protocol"]
    _positive(protocol["timeout_seconds"], "timeout_seconds")
    for name in ("reset", "warmup"):
        stage = protocol[name]
        if not isinstance(stage, dict) or stage.get("mode") not in ("none", "command", "external", "unimplemented"):
            raise ValueError(name + ".mode must be none/command/external/unimplemented")
        if not isinstance(stage.get("description"), str) or not stage["description"].strip():
            raise ValueError(name + " needs a nonempty semantics description")
        _argv(stage.get("command"), name + ".command")
        if (stage["mode"] == "command") != (stage.get("command") is not None):
            raise ValueError(name + " must supply a command exactly when mode is command")
    observer = config["observer"]
    if type(observer.get("enabled")) is not bool or not isinstance(observer.get("by_condition", {}), dict):
        raise ValueError("observer.enabled must be Boolean; by_condition must be an object")
    if any(c not in CONDITIONS for c in observer.get("by_condition", {})):
        raise ValueError("Unknown observer condition override")
    for condition in CONDITIONS:
        settings = observer_settings(observer, condition)
        if type(settings["enabled"]) is not bool:
            raise ValueError("observer.enabled must be Boolean")
        if settings["enabled"]:
            observer_command(condition, settings, Path("observer"))
    if not isinstance(config["binaries"], list) or any(not isinstance(p, str) for p in config["binaries"]):
        raise ValueError("binaries must be a list of paths")
    json_bytes(config)  # Validate additional metadata before reserving an output directory.


def observer_settings(base, condition):
    settings = {key: value for key, value in base.items() if key != "by_condition"}
    override = base.get("by_condition", {}).get(condition, {})
    if not isinstance(override, dict):
        raise ValueError("observer condition override must be an object")
    settings.update(override)
    return settings


def observer_command(condition, settings, prefix):
    """Reuse the independent observer as a sibling, never as a workload launcher."""
    _positive(settings["duration_seconds"], "observer.duration_seconds")
    _positive(settings["sample_seconds"], "observer.sample_seconds")
    if settings["sample_seconds"] < 2 or settings["measurement"] not in ("on", "off"):
        raise ValueError("Observer sample_seconds must be >=2 and measurement must be off/on")
    pids = settings.get("ramflow_pids", [])
    if not isinstance(pids, list) or len(set(pids)) > 64 or any(type(p) is not int or not 0 < p <= 0xffffffff for p in pids):
        raise ValueError("Observer PIDs must be at most 64 explicit positive DWORDs")
    pipe = settings.get("settings_pipe")
    if pipe is not None and (not isinstance(pipe, str) or not pipe.startswith("RamFlow.Native.") or
                             len(pipe) > 240 or any(c in pipe for c in "\\/\0")):
        raise ValueError("Observer settings_pipe must be an explicit local RamFlow.Native.* name")
    coarse = "absent" if condition == "A" else "dry-run" if condition == "B" else "intervention"
    command = [sys.executable, str(ROOT / "scripts" / "benchmark-observer.py")]
    command += ["--condition", coarse, "--measurement", "off" if condition == "A" else settings["measurement"],
                "--output", str(prefix), "--duration", str(settings["duration_seconds"]),
                "--sample-seconds", str(settings["sample_seconds"])]
    if coarse == "intervention":
        if settings.get("acknowledge_existing_intervention") is not True:
            raise ValueError("Observer C..F requires explicit acknowledge_existing_intervention=true")
        command.append("--acknowledge-existing-intervention")
    if coarse != "absent":
        if pids:
            command += ["--ramflow-pid"] + [str(pid) for pid in pids]
        if pipe:
            command += ["--settings-pipe", pipe]
    return command


def git_source():
    value = dict(commit=None, branch=None, tracked_dirty=None, error=None,
                 scope="Git metadata is not a clean-tree guarantee; exact selected source snapshots are hashed separately")
    try:
        for key, args in (("commit", ["rev-parse", "HEAD"]), ("branch", ["branch", "--show-current"]),
                          ("tracked_dirty", ["status", "--porcelain", "--untracked-files=no"])):
            result = subprocess.run(["git", "-C", str(ROOT)] + args, shell=False, stdin=subprocess.DEVNULL,
                                    capture_output=True, timeout=5, check=False)
            if result.returncode:
                raise OSError(result.stderr.decode("utf-8", errors="replace").strip())
            text = result.stdout.decode("utf-8", errors="replace").strip()
            value[key] = bool(text) if key == "tracked_dirty" else text
    except (OSError, subprocess.TimeoutExpired) as exc:
        value["error"] = str(exc)
    return value


def snapshot(run, source, destination, role):
    record = dict(role=role, original_path=str(source), path=destination, status="missing", sha256=None)
    try:
        with source.open("rb") as incoming:
            target = run / destination
            target.parent.mkdir(parents=True, exist_ok=True)
            with target.open("xb") as outgoing:
                for chunk in iter(lambda: incoming.read(1024 * 1024), b""):
                    outgoing.write(chunk)
        record.update(status="recorded", sha256=sha256_file(target), bytes=target.stat().st_size)
    except OSError as exc:
        record["error"] = str(exc)
        if (run / destination).exists():
            record["status"] = "incomplete"
    return record


def create_run(run, config, supplied_bytes, schedule, binaries, execute):
    # Reserving the directory is the overwrite gate, before subprocess launch.
    run.mkdir(parents=True, exist_ok=False)
    artifacts = []

    def save(name, data, role):
        write_new(run / name, data)
        artifacts.append(dict(path=name, role=role, status="recorded", sha256=hashlib.sha256(data).hexdigest(), bytes=len(data)))

    save("config.json", json_bytes(config), "resolved_config")
    if supplied_bytes is not None:
        save("config.supplied.json", supplied_bytes, "supplied_config")
    save("schedule.json", json_bytes(schedule), "schedule")
    stream = io.StringIO(newline="")
    writer = csv.DictWriter(stream, fieldnames=list(schedule[0]))
    writer.writeheader()
    writer.writerows(schedule)
    save("schedule.csv", stream.getvalue().encode("utf-8"), "schedule")
    sources = ("scripts/research_matrix.py", "scripts/test_research_matrix.py", "scripts/benchmark-observer.py",
               "scripts/BenchmarkLab/Program.cs", "scripts/BenchmarkLab/BenchmarkLab.csproj",
               "src/winmemoryflow/core/benchmark.py", "scripts/research/README.md", "scripts/research/plan.example.json",
               "scripts/research_lab_adapter.py", "scripts/research_analyze.py", "native/RamFlow.Core/MeasuredCostModel.cs",
               "native/RamFlow.Core/StarvationGuard.cs", "scripts/ResearchLab/Trial.cs",
               "scripts/ResearchLab/EvidenceJournal.cs","scripts/ResearchLab/FixtureWorker.cs","scripts/ResearchLab/PreInterventionWindow.cs",
               "scripts/ResearchLab/IndependentObserver.cs","scripts/ResearchLab/Program.cs","scripts/ResearchLab/LabInfrastructure.cs","scripts/research_logcheck.py")
    for relative in sources:
        artifacts.append(snapshot(run, ROOT / relative, "source/" + relative, "source"))
    binary_records = [snapshot(run, path, "binaries/%03d-%s" % (i, path.name), "binary")
                      for i, path in enumerate(binaries)]
    artifacts.extend(binary_records)
    missing = [dict(condition=c, reason="unimplemented_adapter" if spec["adapter"] == "unimplemented" else "missing_command")
               for c, spec in config["conditions"].items()
               if spec["adapter"] == "unimplemented" or not spec.get("command")]
    manifest = dict(schema_version=SCHEMA_VERSION, created_utc=utc_now(), mode="execute" if execute else "plan",
                    analysis_unit="paired_session_block", requests_independent=False,
                    conditions=CONDITIONS, seed=config["seed"], blocks_per_workload=config["blocks"],
                    trial_count=len(schedule), workloads=sorted({r["workload"] for r in schedule}),
                    design=dict(name="randomized Williams six-treatment crossover",
                                full_cycles=config["blocks"] // 6, partial_cycle_blocks=config["blocks"] % 6,
                                balance_scope="Within workload, within complete six-block cycles; excludes between-block transitions",
                                workload_order="Randomized cyclic workload rotation per group of workload-count blocks"),
                    missing_commands=missing, source=git_source(), artifacts=artifacts, binaries=binary_records,
                    python=sys.version, platform=sys.platform,
                    evidence_scope="Condition labels are declarations; no native policy adapter or latency extractor is implemented")
    write_json(run / "manifest.json", manifest)
    write_new(run / "manifest.sha256", (sha256_file(run / "manifest.json") + "  manifest.json\n").encode("ascii"))
    return manifest


def expand_command(command, row, trial_dir, phase="workload"):
    evidence_name = "outcome.json" if phase == "workload" else phase + ".outcome.json"
    tokens = dict(row, trial_dir=str(trial_dir), evidence_path=str(trial_dir / evidence_name))
    result = []
    for argument in command:
        # Deliberately not str.format: Python/JSON code may contain literal braces.
        for key, value in tokens.items():
            argument = argument.replace("{" + key + "}", str(value))
        result.append(argument)
    return result


class Child:
    """One directly created process, with disk-backed streams and sealed records."""
    def __init__(self, command, phase, run, directory, cwd):
        self.run, self.directory, self.phase = run, directory, phase
        self.process = None
        self.started = time.monotonic()
        self.record = dict(phase=phase, command=command, cwd=str(cwd), started_utc=utc_now(),
                           finished_utc=None, return_code=None, pid=None, status="running", error=None,
                           stdout=str((directory / (phase + ".stdout.log")).relative_to(run)),
                           stderr=str((directory / (phase + ".stderr.log")).relative_to(run)))
        write_json(directory / (phase + ".started.json"), self.record)
        self.stdout = (run / self.record["stdout"]).open("xb")
        self.stderr = (run / self.record["stderr"]).open("xb")
        try:
            flags = getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0) | getattr(subprocess, "NORMAL_PRIORITY_CLASS", 0)
            self.process = subprocess.Popen(command, shell=False, cwd=cwd, stdin=subprocess.DEVNULL,
                                            stdout=self.stdout, stderr=self.stderr, creationflags=flags)
            self.record["pid"] = self.process.pid
        except OSError as exc:
            self.record.update(status="launch_error", error=str(exc))

    def _stop(self):
        # Never discover/stop existing apps or kill a tree. Only this direct child.
        if self.process is not None:
            try:
                if self.process.poll() is None:
                    self.process.terminate()
                try:
                    self.process.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait(timeout=2)
            except (OSError, subprocess.TimeoutExpired) as exc:
                self.record["cleanup_error"] = str(exc)

    def finish(self, timeout, abort=False):
        if self.record["status"] == "running":
            try:
                if abort:
                    self.record["status"] = "aborted"
                    self._stop()
                else:
                    code = self.process.wait(timeout=max(0, timeout))
                    self.record["status"] = "completed" if code == 0 else "failed"
            except subprocess.TimeoutExpired:
                self.record["status"] = "timed_out"
                self._stop()
            except KeyboardInterrupt:
                self.record["status"] = "aborted"
                self._stop()
            finally:
                self.record["return_code"] = self.process.poll()
                self.record["still_running"] = self.record["return_code"] is None
        self.stdout.close()
        self.stderr.close()
        self.record.update(finished_utc=utc_now(), process_wall_seconds=max(0, time.monotonic() - self.started))
        self.record["stdout_sha256"] = sha256_file(self.run / self.record["stdout"])
        self.record["stderr_sha256"] = sha256_file(self.run / self.record["stderr"])
        write_json(self.directory / (self.phase + ".finished.json"), self.record)
        return self.record


def workload_evidence(run, directory):
    path = directory / "outcome.json"
    record = dict(path=str(path.relative_to(run)), status="missing", sha256=None,
                  scope="Workload-owned raw evidence; values and correctness are not independently validated")
    if path.is_file():
        record["sha256"] = sha256_file(path)
        try:
            payload = read_json(path.read_text(encoding="utf-8"))
            if not isinstance(payload, dict) or not payload:
                raise ValueError("outcome.json must contain a nonempty JSON object")
            record["status"] = "recorded_unvalidated"
        except (OSError, ValueError) as exc:
            record.update(status="invalid", error=str(exc))
    return record


def run_trial(run, row, config, config_dir, aborted=False):
    directory = run / "trials" / row["trial_id"]
    directory.mkdir(parents=True, exist_ok=False)
    spec = config["conditions"][row["condition"]]
    result = dict(row, started_utc=utc_now(), finished_utc=None, return_code=None, phases=[],
                  status="not_run_aborted" if aborted else "planned", latency_ms=None,
                  latency_reason="No latency measurement adapter is implemented; process wall time is orchestration only",
                  condition_evidence=dict(status="user_declared_unverified", policy=CONDITIONS[row["condition"]],
                                          scope="Observer, when enabled, checks coarse mode only; cannot distinguish C/D/E/F"),
                  workload_kind=config["workloads"][row["workload"]]["kind"],
                  setup_semantics={name: config["protocol"][name] for name in ("reset", "warmup")},
                  observer=dict(status="disabled"))
    write_json(directory / "trial.started.json", result)
    if not aborted:
        if spec["adapter"] == "unimplemented" and spec.get("command"):
            result["status"] = "unimplemented_adapter"
        elif not spec.get("command"):
            result["status"] = "missing_command"
        elif any(config["protocol"][stage]["mode"] == "unimplemented" for stage in ("reset", "warmup")):
            result["status"] = "unimplemented_setup"
        else:
            cwd = (config_dir / spec["cwd"]).resolve() if spec.get("cwd") else directory
            timeout = config["protocol"]["timeout_seconds"]
            result["status"] = "completed"
            observer = None
            try:
                for phase in ("reset", "warmup", "workload"):
                    command = spec["command"] if phase == "workload" else config["protocol"][phase].get("command")
                    if not command:
                        continue
                    if phase == "workload":
                        if (directory / "outcome.json").exists():
                            result.update(status="preexisting_evidence", failed_phase="workload")
                            break
                        settings = observer_settings(config["observer"], row["condition"])
                        if settings["enabled"]:
                            observer = Child(observer_command(row["condition"], settings, directory / "observer"),
                                             "observer", run, directory, directory)
                    child = Child(expand_command(command, row, directory, phase), phase, run, directory, cwd)
                    record = child.finish(timeout)
                    result["phases"].append(record)
                    result["return_code"] = record["return_code"]
                    if record["status"] != "completed":
                        result.update(status=record["status"], failed_phase=phase)
                        break
            except KeyboardInterrupt:
                result["status"] = "aborted"
            finally:
                if observer is not None:
                    remaining = settings["duration_seconds"] + 10 - (time.monotonic() - observer.started)
                    record = observer.finish(remaining, abort=result["status"] == "aborted")
                    result["observer"] = dict(record, report=workload_evidence_for_observer(run, directory),
                                               coverage="Independent fixed-duration window; inspect timestamps for overlap; readiness is not synchronized")
                    if record["status"] == "aborted":
                        result["status"] = "aborted"
                    elif result["status"] == "completed" and (record["status"] != "completed" or
                            result["observer"]["report"]["status"] != "recorded"):
                        result["status"] = "observer_failed"
    result["workload_evidence"] = workload_evidence(run, directory)
    if result["status"] == "completed" and result["workload_evidence"]["status"] != "recorded_unvalidated":
        result["status"] = "missing_evidence" if result["workload_evidence"]["status"] == "missing" else "invalid_evidence"
    result["finished_utc"] = utc_now()
    write_json(directory / "result.json", result)
    return result


def workload_evidence_for_observer(run, directory):
    path = directory / "observer.json"
    value = dict(path=str(path.relative_to(run)), status="missing", sha256=None)
    if path.is_file():
        value["sha256"] = sha256_file(path)
        try:
            report = read_json(path.read_text(encoding="utf-8"))
            value.update(status="recorded" if report.get("status") == "completed" else "invalid",
                         condition_evidence=report.get("condition_evidence"),
                         measurement=report.get("measurement"))
        except (OSError, ValueError, AttributeError) as exc:
            value.update(status="invalid", error=str(exc))
    return value


def execute_run(run, schedule, config, config_dir):
    results = []
    aborted = False
    for row in schedule:
        result = run_trial(run, row, config, config_dir, aborted)
        results.append(result)
        aborted = aborted or result["status"] == "aborted"
    code = 130 if aborted else 0 if all(r["status"] == "completed" for r in results) else 2
    # An incomplete block must never be silently treated as six successful pairs.
    blocks = {}
    for result in results:
        blocks.setdefault(result["block_id"], []).append(result)
    summary = dict(schema_version=SCHEMA_VERSION, finished_utc=utc_now(), return_code=code,
                   status="aborted" if aborted else "completed" if code == 0 else "incomplete",
                   counts=dict(Counter(r["status"] for r in results)),
                   analysis_unit="paired_session_block", requests_independent=False,
                   blocks=[dict(block_id=key, complete=all(r["status"] == "completed" for r in group),
                                trial_ids=[r["trial_id"] for r in group]) for key, group in blocks.items()],
                   results=[dict(trial_id=r["trial_id"], status=r["status"],
                                 path="trials/" + r["trial_id"] + "/result.json",
                                 sha256=sha256_file(run / "trials" / r["trial_id"] / "result.json")) for r in results],
                   scope="Completion means commands returned zero and supplied JSON evidence, not verified correctness or performance")
    write_json(run / "run.result.json", summary)
    write_new(run / "run.result.sha256", (sha256_file(run / "run.result.json") + "  run.result.json\n").encode("ascii"))
    return code


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path, help="New run directory; existing paths are refused")
    parser.add_argument("--config", type=Path, help="JSON configuration; mandatory with --execute")
    parser.add_argument("--seed", type=int)
    parser.add_argument("--blocks", type=int, help="Paired six-condition session blocks per workload; default 6")
    parser.add_argument("--workloads", nargs="+", choices=WORKLOADS, default=list(WORKLOADS))
    parser.add_argument("--binary", type=Path, action="append", default=[], help="Optional binary to snapshot/hash, never execute")
    parser.add_argument("--execute", action="store_true", help="Run only explicitly configured command adapters")
    args = parser.parse_args(argv)
    try:
        if args.execute and args.config is None:
            raise ValueError("--execute requires --config with explicit condition argv lists; no workload is built in")
        config = default_config()
        supplied = args.config.read_bytes() if args.config else None
        if supplied is not None:
            _merge(config, read_json(supplied.decode("utf-8-sig")))
        if args.seed is not None:
            config["seed"] = args.seed
        if args.blocks is not None:
            config["blocks"] = args.blocks
        validate_config(config)
        schedule = generate_schedule(config["seed"], config["blocks"], args.workloads)
        config_dir = args.config.resolve().parent if args.config else Path.cwd()
        binaries = [(config_dir / path).resolve() for path in config["binaries"]] + [p.resolve() for p in args.binary]
        run = args.output.resolve()
        manifest = create_run(run, config, supplied, schedule, binaries, args.execute)
        code = execute_run(run, schedule, config, config_dir) if args.execute else 0
        print("%s: %s (%d trials; %d missing/unimplemented condition commands)" %
              ("Execution recorded" if args.execute else "Plan only", run, len(schedule), len(manifest["missing_commands"])))
        return code
    except (OSError, ValueError, TypeError, KeyError) as exc:
        print("Research matrix refused/failed: " + str(exc), file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        print("Interrupted; existing artifacts are preserved. The run directory cannot be reused.", file=sys.stderr)
        return 130


if __name__ == "__main__":
    sys.exit(main())
