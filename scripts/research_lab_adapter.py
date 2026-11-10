"""Bind the existing write-once matrix to explicitly owned Windows fixtures."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

import research_matrix


def make_config(lab, model, seconds=5, blocks=6, variant="WithoutBackgroundCost", background_threads=1, allow_high_cpu=False):
    lab, model = Path(lab).resolve(), Path(model).resolve()
    if not lab.is_file() or not model.is_file():
        raise ValueError("Supply an existing built ResearchLab and frozen model")
    if seconds < 2 or seconds > 300 or blocks < 1:
        raise ValueError("Use 2..300 seconds and positive blocks")
    if type(background_threads) is not int or not 1 <= background_threads <= 32 or background_threads > 8 and not allow_high_cpu:
        raise ValueError("Use 1..32 background threads; more than eight requires explicit --allow-high-cpu. Native per-machine limits still apply.")
    if variant not in ("WithoutBackgroundCost", "WithoutReaccessCost", "WithoutFeedback",
                       "WithoutMemoryAwareness", "WithoutStaging", "WithoutStability"):
        raise ValueError("Choose one explicit ablation; safety mechanisms are not ablations")
    config = research_matrix.default_config()
    config["blocks"] = blocks
    for condition, spec in config["conditions"].items():
        spec.update(adapter="explicit-command", command=[sys.executable, str(Path(__file__).resolve()), "run",
            "--lab", str(lab), "--native-dir", "{trial_dir}/native", "--outcome", "{evidence_path}",
            "--condition", condition, "--regime", "{workload}", "--block", "{block_id}-{session_seed}",
            "--seed", "{session_seed}", "--seconds", str(seconds), "--model", str(model),
            "--background-threads", str(background_threads),
            "--variant", variant if condition == "F" else "None"] + (["--allow-high-cpu"] if allow_high_cpu else []),
            definition="Disposable synthetic task; exact policy/model/action evidence is in native/outcome.json")
    for stage in ("reset", "warmup"):
        config["protocol"][stage] = dict(mode="external", command=None,
            description="ResearchLab creates fresh owned processes, runs five unreported warmups, verifies checksums and restores originals internally; inspect native evidence")
    config["protocol"]["timeout_seconds"] = max(90, seconds + 60)
    config["binaries"] = [str(lab), str(model)]
    for regime, spec in config["workloads"].items():
        spec.update(kind="synthetic", description="Intended " + regime + "; actual global pressure is measured and unattained regimes are excluded, never relabelled")
    config["native_lab"] = dict(scope="Synthetic foreground-role child; not actual OS focus or a real app study",
        model=str(model), ablation=variant, experiment_split="pilot", background_threads=background_threads,
        high_cpu_explicit=allow_high_cpu, core_safety="PID creation, explicit targets, no trim, readback restoration, starvation guard")
    return config


def run_adapter(args):
    destination = args.outcome.resolve()
    if destination.exists() or args.native_dir.exists():
        raise ValueError("Refusing existing evidence or native run directory")
    command = [str(args.lab.resolve()), "trial", "--output", str(args.native_dir.resolve()),
        "--condition", args.condition, "--regime", args.regime, "--block", args.block,
        "--seed", args.seed, "--seconds", str(args.seconds), "--model", str(args.model.resolve()),
        "--background-threads", str(args.background_threads),
        "--variant", args.variant, "--split", "pilot"]
    if args.allow_high_cpu:
        command.append("--allow-high-cpu")
    child = subprocess.Popen(command, shell=False, stdin=subprocess.DEVNULL)
    try:
        code = child.wait()
    except KeyboardInterrupt:
        child.terminate()
        child.wait(timeout=10)
        code = 130
    native = args.native_dir / "outcome.json"
    if native.is_file():
        # Copy actual bytes. Missing/failed measurements are never synthesized.
        with destination.open("xb") as stream:
            stream.write(native.read_bytes())
        with (destination.parent / "archive-alias.json").open("x", encoding="utf-8") as stream:
            json.dump(dict(canonical="outcome.json", archived_copy="native/outcome.json",
                sha256=hashlib.sha256(native.read_bytes()).hexdigest()), stream)
    return code if code else 0 if native.is_file() else 2


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="mode", required=True)
    config = sub.add_parser("config")
    config.add_argument("--lab", type=Path, required=True)
    config.add_argument("--model", type=Path, required=True)
    config.add_argument("--output", type=Path, required=True)
    config.add_argument("--seconds", type=int, default=5)
    config.add_argument("--blocks", type=int, default=6)
    config.add_argument("--variant", default="WithoutBackgroundCost")
    config.add_argument("--background-threads", type=int, default=1)
    config.add_argument("--allow-high-cpu", action="store_true")
    run = sub.add_parser("run")
    for name in ("lab", "model", "native-dir", "outcome"):
        run.add_argument("--" + name, type=Path, required=True)
    for name in ("condition", "regime", "block", "seed"):
        run.add_argument("--" + name, required=True)
    run.add_argument("--seconds", type=int, default=5)
    run.add_argument("--background-threads", type=int, default=1)
    run.add_argument("--variant", default="None")
    run.add_argument("--allow-high-cpu", action="store_true")
    args = parser.parse_args(argv)
    try:
        if args.mode == "run":
            return run_adapter(args)
        payload = make_config(args.lab, args.model, args.seconds, args.blocks, args.variant,
                              args.background_threads, args.allow_high_cpu)
        research_matrix.validate_config(payload)
        with args.output.open("x", encoding="utf-8") as stream:
            json.dump(payload, stream, ensure_ascii=False, indent=2, allow_nan=False)
        print("Wrote executable synthetic pilot configuration; no experiment started")
        return 0
    except (OSError, ValueError, subprocess.SubprocessError) as exc:
        parser.error(str(exc))


if __name__ == "__main__":
    raise SystemExit(main())
