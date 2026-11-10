"""Labelled synthetic data only; no ResearchLab, engine, or workload is run."""
from __future__ import annotations

import copy
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET


SCRIPT = Path(__file__).with_name("research_analyze.py")
SPEC = importlib.util.spec_from_file_location("research_analyze", SCRIPT)
analyze = importlib.util.module_from_spec(SPEC) if SCRIPT.exists() else None
if analyze is not None:
    SPEC.loader.exec_module(analyze)


def synthetic(block="synthetic-b1", condition="A", latency=10, **overrides):
    """Full v2 fixture, explicitly labelled; numbers are not performance evidence."""
    value = dict(schema_version=2, fixture_label="SYNTHETIC TEST DATA",
                 session_block_id=block, hardware_id="synthetic-hw", workload_id="synthetic-work",
                 split="test", condition=condition, regime="cpu", intended_regime="cpu",
                 regime_attained=True, candidate_action=0, status="completed",
                 foreground_latency_ms=[latency] * 20, background_units=100,
                 measurement_seconds=10, background_completion_seconds=None, reaccess_ms=None,
                 correctness_passed=True, restored=True, policy_variant="synthetic-" + condition,
                 model_id=None, overhead_samples=[dict(phase="observe", role="observer",
                     cpu_percent=2, private_bytes=1000, working_set_bytes=2000,
                     decision_ms=None, log_write_ms=0.1)], failures=[])
    value.update(overrides)
    return value


class SyntheticAnalysisTests(unittest.TestCase):
    def pre_value(self, **overrides):
        baseline=dict(basis="pre-intervention-window-v1",regime="cpu",stable=True,regime_attained=True,
            started_utc="2026-11-10T00:00:00+00:00",ended_utc="2026-11-10T00:00:02+00:00",
            samples=[dict(Time="2026-11-10T00:00:0"+str(i//2)+( ".500" if i%2 else "")+"+00:00",CpuPercent=90) for i in range(5)])
        return synthetic(schema_version=3,eligibility_regime="cpu",pre_intervention=baseline,
            treatment_started_utc="2026-11-10T00:00:03+00:00",**overrides)

    def test_pre_cpu_candidate_that_becomes_healthy_stays_in_cpu_contrast(self):
        self.save("A",self.pre_value(condition="A"))
        self.save("C",self.pre_value(condition="C",regime="healthy",regime_attained=False))
        report=self.report()
        self.assertEqual(sum(r["eligible"] for r in report["ledger"]),2)
        pairs=[r for r in report["comparisons"] if r["metric"]=="foreground_p95_ms"]
        self.assertEqual(pairs[0]["n_blocks"],1)
        self.assertEqual(pairs[0]["regime"],"cpu")
        self.assertEqual({r["observed_regime"] for r in report["blocks"]},{"cpu","healthy"})

    def test_pre_window_timestamp_overlap_and_unattained_baseline_are_rejected(self):
        value=self.pre_value()
        value["treatment_started_utc"]="2026-11-10T00:00:01+00:00"
        self.save("overlap",value)
        wrong=self.pre_value(session_block_id="different")
        wrong["pre_intervention"]["regime_attained"]=False
        self.save("unattained",wrong)
        self.assertFalse(any(r["eligible"] for r in self.report()["ledger"]))

    def test_legacy_post_window_and_new_pre_window_are_not_pooled(self):
        self.save("old",synthetic())
        self.save("new",self.pre_value(condition="C"))
        report=self.report()
        self.assertEqual(sum(r["eligible"] for r in report["ledger"]),2)
        self.assertFalse(any(r["n_blocks"] for r in report["comparisons"]))

    def setUp(self):
        self.assertIsNotNone(analyze, "The standard-library analyzer must exist")
        self.temp = tempfile.TemporaryDirectory(prefix="ramflow-synthetic-", dir=SCRIPT.parent.parent.parent)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.inputs = self.root / "synthetic-input"
        self.inputs.mkdir()

    def save(self, name, value):
        path = self.inputs / name / "outcome.json"
        path.parent.mkdir(parents=True)
        path.write_text(json.dumps(value), encoding="utf-8")
        return path

    def report(self, **kwargs):
        return analyze.analyze_directory(self.inputs, foreground_tolerance_ms=1,
                                         bootstrap_iterations=200, seed=42, **kwargs)

    def test_request_counts_do_not_weight_independent_block_means(self):
        self.save("small", synthetic("b1", latency=10, foreground_latency_ms=[10]))
        self.save("large", synthetic("b2", latency=100, foreground_latency_ms=[100] * 999))
        report = self.report()
        row = report["per_hardware"][0]
        self.assertEqual(row["n_blocks"], 2)
        self.assertEqual(row["foreground_p95_ms"], 55)
        self.assertIsNone(row["foreground_p99_ms"])
        self.assertEqual([r["n_requests"] for r in report["blocks"]], [999, 1])

    def test_p99_threshold_is_per_block_not_pooled_requests(self):
        self.save("999", synthetic("b1", foreground_latency_ms=list(range(999))))
        self.save("1000", synthetic("b2", foreground_latency_ms=list(range(1000))))
        rows = {r["session_block_id"]: r for r in self.report()["blocks"]}
        self.assertIsNone(rows["b1"]["foreground_p99_ms"])
        self.assertAlmostEqual(rows["b2"]["foreground_p99_ms"], 989.01)
        self.assertEqual(rows["b2"]["foreground_p50_ms"], 499.5)
        self.assertAlmostEqual(rows["b2"]["foreground_p95_ms"], 949.05)

    def test_paired_bootstrap_is_seeded_and_resamples_whole_pairs(self):
        # Both paired differences are exactly 5. Unpaired resampling would vary.
        pairs = [(1, 6), (1001, 1006)]
        result = analyze.paired_block_bootstrap(pairs, iterations=300, seed=17)
        self.assertEqual(result, analyze.paired_block_bootstrap(pairs, iterations=300, seed=17))
        self.assertEqual(result["difference"], 5)
        self.assertEqual(result["difference_ci95"], [5, 5])
        self.assertAlmostEqual(result["ratio"], 506 / 501)
        self.assertIsNone(result["standardized_dz"])
        self.assertEqual(result["n_blocks"], 2)

    def test_bootstrap_interval_matches_hand_checked_discrete_support(self):
        # n=2 gives differences 1, 2, 3 with probabilities .25, .5, .25.
        result = analyze.paired_block_bootstrap([(10, 11), (20, 23)], iterations=2000, seed=9)
        self.assertEqual(result["difference_ci95"], [1, 3])
        self.assertEqual(result["difference"], 2)
        self.assertAlmostEqual(result["standardized_dz"], 2 / (2 ** 0.5))
        self.assertIsNone(result["standardized_dz_ci95"])
        self.assertIsNone(analyze.paired_block_bootstrap([(1, 2)], iterations=20, seed=9)["difference_ci95"])
        self.assertIsNone(analyze.paired_block_bootstrap([(0, 1), (0, 2)], iterations=20, seed=9)["ratio"])

    def test_failure_invalid_missing_aborted_and_regime_mismatch_are_preserved(self):
        self.save("failed", synthetic(status="failed", failures=[{"message": "synthetic failure"}]))
        self.save("aborted", synthetic("b2", status="aborted"))
        self.save("invalid", synthetic("b3", status="invalid"))
        self.save("wrong-regime", synthetic("b4", intended_regime="ram", regime_attained=False))
        bad = self.save("malformed", {})
        bad.write_text('{"bad":', encoding="utf-8")
        expected = self.root / "expected.json"
        expected.write_text(json.dumps({"outcomes": [{"path": "missing/outcome.json",
            "session_block_id": "b5", "condition": "E"}]}), encoding="utf-8")
        report = self.report(expected_outcomes=expected)
        self.assertEqual(len(report["ledger"]), 6)
        self.assertEqual(len(report["failures"]), 6)
        self.assertFalse(any(r["eligible"] for r in report["ledger"]))
        missing = next(r for r in report["ledger"] if r["status"] == "missing")
        self.assertEqual(missing["expected"]["session_block_id"], "b5")
        failure = next(r for r in report["ledger"] if r["status"] == "failed")
        self.assertEqual(failure["outcome"]["failures"], [{"message": "synthetic failure"}])
        self.assertEqual(next(r for r in report["ledger"] if r["source_path"] == "malformed/outcome.json")["raw_text"], '{"bad":')
        self.assertFalse(report["comparisons"])
        output = self.root / "preserved-failures"
        analyze.write_analysis(self.inputs, output, foreground_tolerance_ms=1,
                               bootstrap_iterations=100, seed=42, expected_outcomes=expected)
        saved = json.loads((output / "failures.json").read_text(encoding="utf-8"))
        self.assertEqual({r["source_path"] for r in saved}, {r["source_path"] for r in report["failures"]})
        self.assertEqual(len(saved), 6)

    def test_schema_rejects_nonfinite_bools_unknown_actions_and_missing_fields(self):
        changes = [{"candidate_action": 2}, {"candidate_action": True}, {"background_units": -1},
                   {"measurement_seconds": float("inf")}, {"regime": "intended-cpu"},
                   {"schema_version": 1}, {"correctness_passed": 1},
                   {"overhead_samples": [{"phase": "other", "role": "observer"}]},
                   {"foreground_wall_ms": [True]}, {"foreground_latency_ms": [float("nan")]}]
        for i, change in enumerate(changes):
            self.save(str(i), synthetic(str(i), **change))
        value = synthetic("missing-field")
        del value["model_id"]
        self.save("missing-field", value)
        report = self.report()
        self.assertEqual(len(report["failures"]), 11)
        self.assertFalse(report["per_hardware"])

    def test_pairing_never_crosses_hardware_workload_regime_split_or_block(self):
        self.save("A", synthetic("shared", "A"))
        for i, change in enumerate([{"hardware_id": "other"}, {"workload_id": "other"},
                                    {"regime": "ram"}, {"split": "fit"},
                                    {"session_block_id": "other"}]):
            self.save("E" + str(i), synthetic("shared", "E", **change))
        report = self.report()
        self.assertTrue(report["comparisons"])
        self.assertTrue(all(r["n_blocks"] == 0 for r in report["comparisons"]))
        self.assertFalse(any(r["same_response_descriptive"] for r in report["same_response"]))

    def test_same_response_requires_pairs_and_foreground_tolerance(self):
        self.save("onlyA", synthetic("alone", "A"))
        self.assertEqual(self.report()["same_response"], [])
        for b in ("b1", "b2"):
            self.save(b + "A", synthetic(b, "A"))
            self.save(b + "E", synthetic(b, "E", latency=12, background_units=200))
        self.assertFalse(self.report()["same_response"][0]["same_response_descriptive"])
        report = analyze.analyze_directory(self.inputs, foreground_tolerance_ms=2,
                                           bootstrap_iterations=200, seed=42)
        row = report["same_response"][0]
        self.assertTrue(row["same_response_descriptive"])
        self.assertEqual(row["n_blocks"], 2)
        self.assertEqual(row["n_unpaired_or_excluded"], 1)
        self.assertNotIn("success", row)

    def test_duplicates_are_all_excluded_without_first_or_last_wins(self):
        self.save("first", synthetic())
        self.save("duplicate", synthetic(latency=1))
        report = self.report()
        self.assertEqual(len(report["failures"]), 2)
        self.assertTrue(all("duplicate_policy_block" in r["exclusion_reasons"] for r in report["ledger"]))
        self.assertFalse(report["per_hardware"])

    def test_explicit_identical_archive_copy_is_retained_without_pseudoreplication(self):
        value = synthetic()
        self.save("trial", value)
        original = self.inputs / "trial" / "outcome.json"
        nested = original.parent / "native"
        nested.mkdir()
        (nested / "outcome.json").write_bytes(original.read_bytes())
        (original.parent / "archive-alias.json").write_text(json.dumps(dict(canonical="outcome.json", archived_copy="native/outcome.json",
            sha256=hashlib.sha256(original.read_bytes()).hexdigest())), encoding="utf-8")
        report = self.report()
        self.assertEqual(sum(row["eligible"] for row in report["ledger"]), 1)
        self.assertEqual(sum(bool(row.get("archive_copy_of")) for row in report["ledger"]), 1)
        self.assertEqual(len(report["blocks"]), 1)

    def test_policy_variant_and_model_id_remain_separate_and_ablation_is_reported(self):
        for b in ("b1", "b2"):
            self.save(b + "E", synthetic(b, "E", model_id="model-1"))
            self.save(b + "F1", synthetic(b, "F", policy_variant="no-restoration", model_id="model-1"))
            self.save(b + "F2", synthetic(b, "F", policy_variant="no-observation", model_id="model-2"))
        report = self.report()
        self.assertEqual(len(report["per_hardware"]), 3)
        variants = {r["candidate_policy_variant"] for r in report["ablations"]}
        self.assertEqual(variants, {"no-restoration", "no-observation"})
        self.assertTrue(all(r["baseline_condition"] == "E" for r in report["ablations"]))

    def test_overhead_is_reduced_within_block_role_and_phase_before_averaging(self):
        value = synthetic("b1")
        sample = value["overhead_samples"][0]
        sample["cpu_percent"] = 10
        value["overhead_samples"] = [copy.deepcopy(sample) for _ in range(100)]
        value["overhead_samples"].append(dict(sample, phase="restore", role="engine-host", cpu_percent=70))
        self.save("one", value)
        value = synthetic("b2")
        value["overhead_samples"][0]["cpu_percent"] = 30
        self.save("two", value)
        rows = self.report()["overhead"]
        row = next(r for r in rows if r["role"] == "observer" and r["phase"] == "observe")
        self.assertEqual(row["cpu_percent"], 20)
        self.assertEqual(row["n_blocks"], 2)
        self.assertEqual(row["n_samples"], 101)
        self.assertIsNone(row["decision_ms"])
        self.assertEqual(next(r for r in rows if r["phase"] == "restore")["cpu_percent"], 70)

    def test_calibration_and_failed_correctness_never_enter_efficacy(self):
        self.save("cal", synthetic(condition="calibration"))
        self.save("incorrect", synthetic("b2", correctness_passed=False))
        self.save("unrestored", synthetic("b3", restored=False))
        self.save("zero-window", synthetic("b4", measurement_seconds=0))
        self.save("no-requests", synthetic("b5", foreground_latency_ms=[]))
        report = self.report()
        self.assertFalse(report["per_hardware"])
        self.assertEqual(len(report["ledger"]), 5)

    def test_optional_wall_completion_and_reaccess_keep_their_own_units_and_missingness(self):
        self.save("one", synthetic("b1", foreground_wall_ms=[20] * 20,
            background_completion_seconds=12, reaccess_ms=7))
        self.save("two", synthetic("b2"))
        rows = self.report()["per_hardware"][0]
        self.assertEqual(rows["foreground_wall_p95_ms"], 20)
        self.assertEqual(rows["foreground_wall_p95_ms_n_blocks"], 1)
        self.assertIsNone(rows["foreground_wall_p99_ms"])
        self.assertEqual(rows["background_completion_seconds"], 12)
        self.assertEqual(rows["background_completion_seconds_n_blocks"], 1)
        self.assertEqual(rows["reaccess_ms"], 7)
        self.assertEqual(rows["background_throughput_units_per_s"], 10)

    def test_derived_overflow_preserves_raw_evidence_and_is_not_an_infinite_gain(self):
        self.save("overflow", synthetic(background_units=1e308, measurement_seconds=1e-308))
        output = self.root / "overflow-output"
        analyze.write_analysis(self.inputs, output, foreground_tolerance_ms=1,
                               bootstrap_iterations=100, seed=42)
        ledger = json.loads((output / "ledger.json").read_text())
        self.assertEqual(len(ledger), 1)
        self.assertFalse(ledger[0]["eligible"])
        self.assertEqual(ledger[0]["outcome"]["background_units"], 1e308)
        self.assertIn("nonfinite_derived_metric:background_throughput_units_per_s", ledger[0]["exclusion_reasons"])
        self.assertIsNone(json.loads((output / "blocks.json").read_text())[0]["background_throughput_units_per_s"])

    def test_expected_identity_mismatch_and_escaping_paths_are_rejected(self):
        self.save("exists", synthetic())
        path = self.root / "expected.json"
        path.write_text(json.dumps({"outcomes": [{"path": "exists/outcome.json", "hardware_id": "wrong"}]}))
        report = self.report(expected_outcomes=path)
        self.assertIn("expected_identity_mismatch:hardware_id", report["ledger"][0]["exclusion_reasons"])
        for relative in ("../outside/outcome.json", "exists/other.json"):
            path.write_text(json.dumps({"outcomes": [{"path": relative}]}))
            with self.assertRaises(ValueError):
                self.report(expected_outcomes=path)

    def test_holm_rejects_missing_nonfinite_boolean_and_duplicate_p_values(self):
        original = {"families": [{"family_id": "synthetic", "predeclared": True,
            "complete": True, "hypotheses": [{"hypothesis_id": "h", "test": "external",
                                              "contrast": "synthetic contrast", "p_value": .5}]}]}
        path = self.root / "p-values.json"
        for value in (None, True, -1, 1.01, float("nan"), float("inf")):
            spec = copy.deepcopy(original)
            spec["families"][0]["hypotheses"][0]["p_value"] = value
            path.write_text(json.dumps(spec))
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.report(p_values=path)
        spec = copy.deepcopy(original)
        spec["families"][0]["hypotheses"] *= 2
        path.write_text(json.dumps(spec))
        with self.assertRaises(ValueError):
            self.report(p_values=path)

    def test_holm_only_uses_complete_explicit_predeclared_external_p_values(self):
        self.assertEqual(self.report()["p_values"], [])
        spec = {"families": [{"family_id": "synthetic-primary", "predeclared": True,
            "complete": True, "hypotheses": [
                {"hypothesis_id": "h1", "test": "external-test", "contrast": "E-A FG", "p_value": .01},
                {"hypothesis_id": "h2", "test": "external-test", "contrast": "E-A BG", "p_value": .04},
                {"hypothesis_id": "h3", "test": "external-test", "contrast": "F-E FG", "p_value": .03}]}]}
        path = self.root / "pvalues.json"
        path.write_text(json.dumps(spec), encoding="utf-8")
        rows = {r["hypothesis_id"]: r for r in self.report(p_values=path)["p_values"]}
        self.assertAlmostEqual(rows["h1"]["holm_p_value"], .03)
        self.assertAlmostEqual(rows["h2"]["holm_p_value"], .06)
        self.assertAlmostEqual(rows["h3"]["holm_p_value"], .06)
        for change in ({"predeclared": False}, {"complete": False}):
            bad = copy.deepcopy(spec)
            bad["families"][0].update(change)
            path.write_text(json.dumps(bad), encoding="utf-8")
            with self.assertRaises(ValueError):
                self.report(p_values=path)

    def test_cli_emits_standalone_vector_and_tables_refuses_overwrite(self):
        for b in ("b1", "b2"):
            self.save(b + "A", synthetic(b, "A", policy_variant='synthetic<&"baseline'))
            self.save(b + "E", synthetic(b, "E", latency=9, background_units=110))
        output = self.root / "synthetic-output"
        args = [sys.executable, "-B", "-S", str(SCRIPT), "--input", str(self.inputs),
                "--output", str(output), "--foreground-tolerance-ms", "1",
                "--bootstrap-iterations", "100", "--seed", "123"]
        first = subprocess.run(args, capture_output=True, text=True)
        self.assertEqual(first.returncode, 0, first.stderr)
        report_bytes = (output / "analysis.json").read_bytes()
        for stem in ("ledger", "blocks", "per_hardware", "comparisons", "same_response",
                     "ablations", "overhead_blocks", "overhead", "failures", "pairing", "p_values"):
            self.assertTrue((output / (stem + ".csv")).exists(), stem)
            self.assertTrue((output / (stem + ".json")).exists(), stem)
        self.assertTrue((output / "SUMMARY.md").is_file())
        for path in output.glob("*.svg"):
            svg = ET.parse(path).getroot()
            self.assertTrue(svg.tag.endswith("svg"))
            self.assertTrue(any(e.tag.endswith("title") for e in svg.iter()))
            self.assertFalse(any(e.tag.endswith(("image", "script")) for e in svg.iter()))
            self.assertFalse(any("href" in key for e in svg.iter() for key in e.attrib))
        self.assertTrue(list(output.glob("pareto*.svg")))
        self.assertTrue(list(output.glob("effect*.svg")))
        manifest = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
        for artifact in manifest["artifacts"]:
            payload = (output / artifact["path"]).read_bytes()
            self.assertEqual(hashlib.sha256(payload).hexdigest(), artifact["sha256"])
            self.assertEqual(len(payload), artifact["bytes"])
        second_output = self.root / "synthetic-output-repeat"
        repeated_args = [str(second_output) if value == str(output) else value for value in args]
        repeated = subprocess.run(repeated_args, capture_output=True, text=True)
        self.assertEqual(repeated.returncode, 0, repeated.stderr)
        for artifact in manifest["artifacts"]:
            self.assertEqual((output / artifact["path"]).read_bytes(), (second_output / artifact["path"]).read_bytes())
        again = subprocess.run(args, capture_output=True, text=True)
        self.assertNotEqual(again.returncode, 0)
        self.assertEqual((output / "analysis.json").read_bytes(), report_bytes)
        without_tolerance = args[:args.index("--foreground-tolerance-ms")]
        without_tolerance[without_tolerance.index(str(output))] = str(self.root / "no-tolerance")
        self.assertNotEqual(subprocess.run(without_tolerance, capture_output=True).returncode, 0)
        self.assertFalse((self.root / "no-tolerance").exists())

    def test_output_cannot_be_inside_input_and_empty_inputs_are_honest(self):
        with self.assertRaises(ValueError):
            analyze.write_analysis(self.inputs, self.inputs / "analysis", foreground_tolerance_ms=1,
                                   bootstrap_iterations=100, seed=1)
        report = self.report()
        self.assertEqual(report["ledger"], [])
        self.assertEqual(report["comparisons"], [])
        self.assertTrue(report["warnings"])

    def test_finite_extreme_values_do_not_produce_nan_or_infinite_svg_coordinates(self):
        for hardware, baseline, candidate in (("synthetic-negative", 1.7e308, 0),
                                               ("synthetic-positive", 0, 1.7e308)):
            for block in ("b1", "b2"):
                for condition, latency in (("A", baseline), ("E", candidate)):
                    self.save(hardware + block + condition, synthetic(block, condition, latency=latency,
                        hardware_id=hardware, background_units=1.7e308, measurement_seconds=1,
                        overhead_samples=[dict(phase="idle", role="observer", cpu_percent=1.7e308,
                            private_bytes=1.7e308, working_set_bytes=1.7e308, decision_ms=None, log_write_ms=None)]))
        output = self.root / "extreme-output"
        analyze.write_analysis(self.inputs, output, foreground_tolerance_ms=1,
                               bootstrap_iterations=100, seed=42)
        for path in output.glob("*.svg"):
            for element in ET.parse(path).iter():
                for key, value in element.attrib.items():
                    if key in ("x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r"):
                        self.assertTrue(math.isfinite(float(value)), (path.name, key, value))
                    if key == "d":
                        self.assertNotIn("nan", value.lower())
                        self.assertNotIn("inf", value.lower())


if __name__ == "__main__":
    unittest.main()
