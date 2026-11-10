"""Safe research-matrix tests: temporary files and disposable Python children only.

Run: python -B -m unittest discover -s scripts -p test_research_matrix.py -v
No engine, observer, benchmark, real application, or installed adapter is run.
"""
from __future__ import annotations

from collections import Counter, defaultdict
import contextlib
import hashlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

try:
    import research_matrix as matrix
except ModuleNotFoundError as exc:
    if exc.name != "research_matrix":
        raise
    matrix = None


class MatrixTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(matrix, "research_matrix implementation does not exist yet")
        scratch_root = Path(__file__).resolve().parent.parent / "work"
        scratch_root.mkdir(exist_ok=True)
        self.scratch = tempfile.TemporaryDirectory(prefix="matrix-test-", dir=scratch_root)
        self.root = Path(self.scratch.name)
        self.assertTrue(self.root.resolve().is_relative_to(scratch_root.resolve()))
        self.addCleanup(self.scratch.cleanup)

    def config(self, commands=None, **overrides):
        value = {
            "schema_version": 1,
            "seed": 73,
            "blocks": 1,
            "workloads": {name: {"kind": "synthetic", "description": "test fixture only"}
                          for name in ("cpu", "ram", "mixed", "healthy")},
            "conditions": {key: {"adapter": "explicit-command", "command": command}
                           for key, command in (commands or {}).items()},
            "protocol": {
                "reset": {"mode": "none", "description": "disposable test; no reset", "command": None},
                "warmup": {"mode": "none", "description": "no warmup for test", "command": None},
                "timeout_seconds": 5,
            },
        }
        value.update(overrides)
        path = self.root / "config.json"
        path.write_text(json.dumps(value), encoding="utf-8")
        return path

    def run_cli(self, config=None, execute=False, extra=()):
        output = self.root / "run"
        args = ["--output", str(output), "--workloads", "cpu", "--blocks", "1"]
        if config:
            args += ["--config", str(config)]
        if execute:
            args += ["--execute"]
        args += list(extra)
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            code = matrix.main(args)
        return code, output

    @staticmethod
    def load(path):
        return json.loads(path.read_text(encoding="utf-8"))

    def results(self, output):
        return [self.load(path) for path in sorted((output / "trials").glob("*/result.json"))]

    def test_schedule_is_deterministic_and_seed_sensitive(self):
        first = matrix.generate_schedule(seed=41, blocks=6)
        self.assertEqual(first, matrix.generate_schedule(seed=41, blocks=6))
        self.assertNotEqual(first, matrix.generate_schedule(seed=42, blocks=6))

    def test_complete_paired_blocks_share_fixture_seed(self):
        rows = matrix.generate_schedule(seed=3, blocks=6)
        self.assertEqual(len(rows), 144)
        groups = defaultdict(list)
        for row in rows:
            groups[(row["workload"], row["block_id"])].append(row)
        self.assertEqual(Counter(workload for workload, _ in groups),
                         {"cpu": 6, "ram": 6, "mixed": 6, "healthy": 6})
        seeds = []
        for block in groups.values():
            self.assertEqual({r["condition"] for r in block}, set("ABCDEF"))
            self.assertEqual([r["position"] for r in block], [1, 2, 3, 4, 5, 6])
            self.assertEqual(len({r["session_seed"] for r in block}), 1)
            seeds.append(block[0]["session_seed"])
        self.assertEqual(len(set(seeds)), 24)
        self.assertEqual(len({r["trial_id"] for r in rows}), 144)

    def test_every_six_blocks_balance_position_and_directed_carryover(self):
        rows = matrix.generate_schedule(seed=917, blocks=12)
        for workload in ("cpu", "ram", "mixed", "healthy"):
            for cycle in (0, 1):
                selected = [r for r in rows if r["workload"] == workload and r["cycle_index"] == cycle]
                self.assertEqual(Counter((r["condition"], r["position"]) for r in selected),
                                 {(c, p): 1 for c in "ABCDEF" for p in range(1, 7)})
                blocks = defaultdict(list)
                for row in selected:
                    blocks[row["block_id"]].append(row["condition"])
                pairs = Counter(pair for block in blocks.values() for pair in zip(block, block[1:]))
                self.assertEqual(pairs, {(a, b): 1 for a in "ABCDEF" for b in "ABCDEF" if a != b})

    def test_partial_cycle_position_imbalance_is_at_most_one(self):
        rows = matrix.generate_schedule(seed=4, blocks=4, workloads=["cpu"])
        counts = Counter((r["condition"], r["position"]) for r in rows)
        for condition in "ABCDEF":
            values = [counts[condition, p] for p in range(1, 7)]
            self.assertLessEqual(max(values) - min(values), 1)

    def test_rejects_invalid_schedule_dimensions(self):
        for kwargs in ({"blocks": 0}, {"blocks": -1}, {"blocks": True},
                       {"workloads": []}, {"workloads": ["cpu", "cpu"]},
                       {"workloads": ["unknown"]}, {"seed": True}):
            with self.subTest(kwargs=kwargs), self.assertRaises(ValueError):
                matrix.generate_schedule(seed=kwargs.get("seed", 2),
                                         **{k: v for k, v in kwargs.items() if k != "seed"})

    def test_default_plan_never_launches_supplied_commands(self):
        marker = self.root / "should-not-exist"
        command = [sys.executable, "-c", "from pathlib import Path; Path(__import__('sys').argv[1]).touch()", str(marker)]
        code, output = self.run_cli(self.config({"A": command}))
        self.assertEqual(code, 0)
        self.assertFalse(marker.exists())
        self.assertFalse((output / "trials").exists())
        manifest = self.load(output / "manifest.json")
        self.assertEqual(manifest["mode"], "plan")
        self.assertEqual(manifest["analysis_unit"], "paired_session_block")
        self.assertEqual(len(manifest["missing_commands"]), 5)

    def test_manifest_hashes_exact_inputs_source_and_optional_binaries(self):
        config = self.config()
        binary = self.root / "fixture.bin"
        binary.write_bytes(b"binary fixture\x00\x01")
        code, output = self.run_cli(config, extra=["--binary", str(binary), "--binary", str(self.root / "missing.bin")])
        self.assertEqual(code, 0)
        manifest = self.load(output / "manifest.json")
        self.assertTrue(manifest["source"]["commit"] or manifest["source"]["error"])
        for record in manifest["artifacts"]:
            if record["status"] == "recorded":
                self.assertEqual(hashlib.sha256((output / record["path"]).read_bytes()).hexdigest(), record["sha256"])
        self.assertEqual((output / "config.supplied.json").read_bytes(), config.read_bytes())
        self.assertEqual(manifest["binaries"][1]["status"], "missing")
        self.assertEqual(hashlib.sha256((output / "manifest.json").read_bytes()).hexdigest(),
                         (output / "manifest.sha256").read_text().split()[0])
        source = [r for r in manifest["artifacts"] if r.get("role") == "source"]
        self.assertTrue(any(r["path"].endswith("benchmark-observer.py") for r in source))

    def test_existing_run_is_never_overwritten_or_executed(self):
        config = self.config({"A": [sys.executable, "-c", "raise SystemExit(9)"]})
        _, output = self.run_cli(config)
        before = {p.relative_to(output): p.read_bytes() for p in output.rglob("*") if p.is_file()}
        code, _ = self.run_cli(config, execute=True)
        self.assertEqual(code, 2)
        self.assertEqual(before, {p.relative_to(output): p.read_bytes() for p in output.rglob("*") if p.is_file()})

    def test_execute_without_config_is_rejected_without_output(self):
        code, output = self.run_cli(execute=True)
        self.assertEqual(code, 2)
        self.assertFalse(output.exists())

    def test_missing_commands_are_recorded_for_every_trial(self):
        code, output = self.run_cli(self.config(), execute=True)
        self.assertEqual(code, 2)
        results = self.results(output)
        self.assertEqual(len(results), 6)
        self.assertEqual({r["status"] for r in results}, {"missing_command"})
        self.assertTrue(all(r["return_code"] is None and r["phases"] == [] for r in results))

    def test_nonzero_exit_preserves_both_streams_code_and_times(self):
        command = [sys.executable, "-c", "import sys; print('partial output'); print('failure detail', file=sys.stderr); sys.exit(7)"]
        code, output = self.run_cli(self.config({"A": command}), execute=True)
        self.assertEqual(code, 2)
        trial = next(r for r in self.results(output) if r["condition"] == "A")
        self.assertEqual(trial["status"], "failed")
        self.assertEqual(trial["return_code"], 7)
        phase = trial["phases"][-1]
        self.assertIn("partial output", (output / phase["stdout"]).read_text())
        self.assertIn("failure detail", (output / phase["stderr"]).read_text())
        self.assertLessEqual(trial["started_utc"], trial["finished_utc"])
        self.assertGreaterEqual(phase["process_wall_seconds"], 0)
        self.assertIsNone(trial["latency_ms"])

    def test_successful_exit_with_no_workload_evidence_stays_missing(self):
        code, output = self.run_cli(self.config({"A": [sys.executable, "-c", "print('done')"]}), execute=True)
        self.assertEqual(code, 2)
        trial = next(r for r in self.results(output) if r["condition"] == "A")
        self.assertEqual(trial["status"], "missing_evidence")
        self.assertEqual(trial["return_code"], 0)
        self.assertEqual(trial["workload_evidence"]["status"], "missing")
        self.assertEqual(trial["condition_evidence"]["status"], "user_declared_unverified")
        self.assertIsNone(trial["latency_ms"])

    def test_explicit_commands_receive_block_seed_and_evidence_path_as_literal_argv(self):
        script = ("import json,sys; from pathlib import Path; "
                  "Path(sys.argv[1]).write_text(json.dumps({'seed':sys.argv[2], 'literal':sys.argv[3]}))")
        literal = "literal & $(do-not-run) ; | >"
        command = [sys.executable, "-c", script, "{evidence_path}", "{session_seed}", literal]
        code, output = self.run_cli(self.config({c: command for c in "ABCDEF"}), execute=True)
        self.assertEqual(code, 0)
        trials = self.results(output)
        self.assertEqual({r["status"] for r in trials}, {"completed"})
        self.assertEqual(len({r["session_seed"] for r in trials}), 1)
        for trial in trials:
            evidence = trial["workload_evidence"]
            self.assertEqual(evidence["status"], "recorded_unvalidated")
            payload = self.load(output / evidence["path"])
            self.assertEqual(payload["literal"], literal)
            self.assertEqual(payload["seed"], str(trial["session_seed"]))
            self.assertIsNone(trial["latency_ms"])

    def test_launch_error_is_preserved_and_later_trials_are_not_lost(self):
        code, output = self.run_cli(self.config({"A": [str(self.root / "nonexistent-executable")]}), execute=True)
        self.assertEqual(code, 2)
        results = self.results(output)
        self.assertEqual(len(results), 6)
        trial = next(r for r in results if r["condition"] == "A")
        self.assertEqual(trial["status"], "launch_error")
        self.assertIsNone(trial["return_code"])
        self.assertTrue(trial["phases"][-1]["error"])
        self.assertTrue((output / trial["phases"][-1]["stderr"]).is_file())

    def test_timeout_preserves_partial_output_and_actual_exit_code(self):
        config = self.config({"A": [sys.executable, "-u", "-c", "import time; print('before timeout'); time.sleep(30)"]})
        value = self.load(config)
        value["protocol"]["timeout_seconds"] = 0.5
        config.write_text(json.dumps(value))
        code, output = self.run_cli(config, execute=True)
        self.assertEqual(code, 2)
        trial = next(r for r in self.results(output) if r["condition"] == "A")
        self.assertEqual(trial["status"], "timed_out")
        self.assertIsNotNone(trial["return_code"])
        self.assertIn("before timeout", (output / trial["phases"][-1]["stdout"]).read_text())

    def test_interrupt_seals_current_and_marks_remaining_trials_aborted(self):
        config = self.config({c: [sys.executable, "-c", "print('safe child')"] for c in "ABCDEF"})
        # Only process waiting is interrupted; file I/O and process creation stay real.
        original_wait = subprocess.Popen.wait
        interrupted = [False]

        def interrupt_once(process, *args, **kwargs):
            if not interrupted[0] and process.args[:1] == [sys.executable]:
                interrupted[0] = True
                raise KeyboardInterrupt
            return original_wait(process, *args, **kwargs)

        with mock.patch.object(subprocess.Popen, "wait", interrupt_once):
            code, output = self.run_cli(config, execute=True)
        self.assertEqual(code, 130)
        results = self.results(output)
        self.assertEqual(results[0]["status"], "aborted")
        self.assertIsNotNone(results[0]["return_code"])
        self.assertEqual({r["status"] for r in results[1:]}, {"not_run_aborted"})
        self.assertEqual(len(results), 6)

    def test_reset_failure_blocks_warmup_and_workload(self):
        marker = self.root / "should-not-exist"
        command = [sys.executable, "-c", "from pathlib import Path; Path(__import__('sys').argv[1]).touch()", str(marker)]
        config = self.config({"A": command})
        value = self.load(config)
        value["protocol"]["reset"] = {"mode": "command", "description": "failing fixture reset",
                                        "command": [sys.executable, "-c", "raise SystemExit(4)"]}
        value["protocol"]["warmup"] = {"mode": "command", "description": "must not run", "command": command}
        config.write_text(json.dumps(value))
        _, output = self.run_cli(config, execute=True)
        trial = next(r for r in self.results(output) if r["condition"] == "A")
        self.assertEqual(trial["status"], "failed")
        self.assertEqual(trial["failed_phase"], "reset")
        self.assertEqual([p["phase"] for p in trial["phases"]], ["reset"])
        self.assertFalse(marker.exists())

    def test_unimplemented_adapter_and_setup_are_not_silently_executed(self):
        config = self.config({"A": [sys.executable, "-c", "raise SystemExit(9)"]})
        value = self.load(config)
        value["conditions"]["A"]["adapter"] = "unimplemented"
        config.write_text(json.dumps(value))
        _, output = self.run_cli(config, execute=True)
        trial = next(r for r in self.results(output) if r["condition"] == "A")
        self.assertEqual(trial["status"], "unimplemented_adapter")
        self.assertEqual(trial["phases"], [])

    def test_unimplemented_setup_blocks_an_explicit_workload_command(self):
        config = self.config({"A": [sys.executable, "-c", "raise SystemExit(9)"]})
        value = self.load(config)
        value["protocol"]["warmup"]["mode"] = "unimplemented"
        config.write_text(json.dumps(value))
        _, output = self.run_cli(config, execute=True)
        trial = next(r for r in self.results(output) if r["condition"] == "A")
        self.assertEqual(trial["status"], "unimplemented_setup")
        self.assertEqual(trial["phases"], [])

    def test_setup_commands_run_in_order_with_separate_evidence(self):
        config = self.config({"A": [sys.executable, "-c", "print('no workload outcome')"]})
        value = self.load(config)
        for phase in ("reset", "warmup"):
            script = ("import json,sys; from pathlib import Path; "
                      "p=Path('order.txt'); p.open('a').write(sys.argv[2]+'\\n'); "
                      "Path(sys.argv[1]).write_text(json.dumps({'setup':sys.argv[2]}))")
            value["protocol"][phase] = {"mode": "command", "description": "test " + phase,
                                         "command": [sys.executable, "-c", script, "{evidence_path}", phase]}
        config.write_text(json.dumps(value))
        _, output = self.run_cli(config, execute=True)
        trial = next(r for r in self.results(output) if r["condition"] == "A")
        directory = output / "trials" / trial["trial_id"]
        self.assertEqual((directory / "order.txt").read_text().splitlines(), ["reset", "warmup"])
        self.assertEqual([p["phase"] for p in trial["phases"]], ["reset", "warmup", "workload"])
        self.assertTrue((directory / "reset.outcome.json").is_file())
        self.assertTrue((directory / "warmup.outcome.json").is_file())
        self.assertEqual(trial["status"], "missing_evidence")

    def test_preexisting_workload_evidence_cannot_be_credited_to_measurement(self):
        marker = self.root / "must-not-run"
        command = [sys.executable, "-c", "from pathlib import Path; Path(__import__('sys').argv[1]).touch()", str(marker)]
        config = self.config({"A": command})
        value = self.load(config)
        value["protocol"]["warmup"] = {"mode": "command", "description": "writes stale fixture outcome",
            "command": [sys.executable, "-c", "from pathlib import Path; Path('outcome.json').write_text('{\"old\":true}')"]}
        config.write_text(json.dumps(value))
        _, output = self.run_cli(config, execute=True)
        trial = next(r for r in self.results(output) if r["condition"] == "A")
        self.assertEqual(trial["status"], "preexisting_evidence")
        self.assertFalse(marker.exists())

    def test_malformed_workload_evidence_is_retained_with_hash(self):
        command = [sys.executable, "-c", "from pathlib import Path; import sys; Path(sys.argv[1]).write_text('not json')", "{evidence_path}"]
        _, output = self.run_cli(self.config({"A": command}), execute=True)
        trial = next(r for r in self.results(output) if r["condition"] == "A")
        self.assertEqual(trial["status"], "invalid_evidence")
        self.assertEqual((output / trial["workload_evidence"]["path"]).read_text(), "not json")
        self.assertEqual(trial["workload_evidence"]["sha256"], hashlib.sha256(b"not json").hexdigest())
        summary = self.load(output / "run.result.json")
        self.assertFalse(summary["blocks"][0]["complete"])

    def test_nonfinite_and_duplicate_key_evidence_is_never_accepted(self):
        for payload in ('{"latency":NaN}', '{"latency":1e999}', '{"done":true,"done":false}'):
            with self.subTest(payload=payload):
                directory = self.root / ("evidence-" + str(len(list(self.root.iterdir()))))
                directory.mkdir()
                (directory / "outcome.json").write_text(payload)
                evidence = matrix.workload_evidence(self.root, directory)
                self.assertEqual(evidence["status"], "invalid")
                self.assertEqual(evidence["sha256"], hashlib.sha256(payload.encode()).hexdigest())

    def test_invalid_config_sections_and_numbers_fail_before_reserving_run(self):
        for section, value in (("observer", []), ("protocol", None),
                               ("workloads", []), ("extra_metadata", float("inf"))):
            with self.subTest(section=section):
                config = self.config(**{section: value})
                code, output = self.run_cli(config)
                self.assertEqual(code, 2)
                self.assertFalse(output.exists())

    def test_optional_observer_failure_preserves_its_streams_and_raw_report(self):
        config = self.config({"A": [sys.executable, "-c", "from pathlib import Path; import sys; Path(sys.argv[1]).write_text('{\"done\":true}')", "{evidence_path}"]})
        value = self.load(config)
        value["observer"] = {"by_condition": {"A": {"enabled": True, "duration_seconds": 2}}}
        config.write_text(json.dumps(value))
        real_builder = matrix.observer_command

        def stand_in(condition, settings, prefix):
            real_builder(condition, settings, prefix)  # Validate the real observer argv contract.
            script = ("from pathlib import Path; import sys; print('observer partial'); "
                      "print('counter unavailable',file=sys.stderr); "
                      "Path(sys.argv[1]+'.json').write_text('{\"status\":\"error\"}'); sys.exit(3)")
            return [sys.executable, "-c", script, str(prefix)]

        # A disposable child stands in for Windows observation; never start the observer.
        with mock.patch.object(matrix, "observer_command", side_effect=stand_in):
            code, output = self.run_cli(config, execute=True)
        self.assertEqual(code, 2)
        trial = next(r for r in self.results(output) if r["condition"] == "A")
        self.assertEqual(trial["status"], "observer_failed")
        self.assertEqual(trial["observer"]["return_code"], 3)
        self.assertEqual(trial["observer"]["report"]["status"], "invalid")
        self.assertIn("observer partial", (output / trial["observer"]["stdout"]).read_text())
        self.assertIn("counter unavailable", (output / trial["observer"]["stderr"]).read_text())

    def test_example_configuration_is_plan_only_with_no_implemented_adapters(self):
        example = Path(__file__).with_name("research") / "plan.example.json"
        self.assertTrue(example.is_file(), "The runnable example configuration is missing")
        code, output = self.run_cli(example)
        self.assertEqual(code, 0)
        config = self.load(output / "config.json")
        self.assertEqual({c["adapter"] for c in config["conditions"].values()}, {"unimplemented"})
        self.assertTrue(all(c["command"] is None for c in config["conditions"].values()))
        self.assertFalse(config["observer"]["enabled"])
        self.assertEqual({w["kind"] for w in config["workloads"].values()}, {"synthetic"})

    def test_rejects_shell_strings_and_nonfinite_timeout_before_creating_run(self):
        for command, timeout in (("echo not-an-argv-list", 5), (["echo"], float("nan"))):
            with self.subTest(command=command, timeout=timeout):
                config = self.config({"A": command})
                value = self.load(config)
                value["protocol"]["timeout_seconds"] = timeout
                config.write_text(json.dumps(value))
                code, output = self.run_cli(config, execute=True)
                self.assertEqual(code, 2)
                self.assertFalse(output.exists())

    def test_observer_command_reuses_existing_script_without_owning_workload(self):
        settings = {"enabled": True, "duration_seconds": 2, "sample_seconds": 2,
                    "measurement": "off", "ramflow_pids": [1234],
                    "settings_pipe": "RamFlow.Native.Test.Fixture",
                    "acknowledge_existing_intervention": True}
        args = matrix.observer_command("E", settings, self.root / "observer")
        self.assertTrue(args[1].endswith("benchmark-observer.py"))
        self.assertEqual(args[args.index("--condition") + 1], "intervention")
        self.assertIn("--acknowledge-existing-intervention", args)
        self.assertNotIn("--workload", args)
        self.assertIn("--duration", args)
        absent = matrix.observer_command("A", settings, self.root / "absent")
        self.assertEqual(absent[absent.index("--condition") + 1], "absent")
        self.assertNotIn("--ramflow-pid", absent)
        self.assertNotIn("--settings-pipe", absent)
        self.assertEqual(absent[absent.index("--measurement") + 1], "off")


if __name__ == "__main__":
    unittest.main()
