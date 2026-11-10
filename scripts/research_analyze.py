#!/usr/bin/env python3
"""Read-only ResearchLab v2 outcome analysis, Python 3.10+ standard library.

No workload execution, engine imports, model training, dependency installation,
or p-value generation. All uncertainty resamples paired session blocks.
"""
from __future__ import annotations

import argparse
from collections import defaultdict
import csv
import hashlib
from html import escape
from itertools import combinations
import json
import math
from pathlib import Path
import random
import statistics
import sys
import textwrap


COHORT = ("split", "hardware_id", "workload_id", "regime")
POLICY = ("condition", "policy_variant", "model_id")
IDENTITY = COHORT + ("session_block_id",) + POLICY
METRICS = {"foreground_p50_ms": "ms", "foreground_p95_ms": "ms", "foreground_p99_ms": "ms",
           "foreground_wall_p50_ms": "ms", "foreground_wall_p95_ms": "ms",
           "foreground_wall_p99_ms": "ms", "background_throughput_units_per_s": "units/s",
           "background_completion_seconds": "s", "reaccess_ms": "ms"}
OVERHEAD_METRICS = ("cpu_percent", "private_bytes", "working_set_bytes", "decision_ms", "log_write_ms")
PHASES = ("idle", "observe", "evaluate", "intervene", "restore")
ROLES = ("engine-host", "foreground-fixture", "background-fixture", "observer", "tray", "service")
TABLES = ("ledger", "blocks", "per_hardware", "comparisons", "same_response", "ablations",
          "overhead_blocks", "overhead", "failures", "pairing", "p_values")
CAVEATS = [
    "Descriptive analysis, not an efficacy verdict or a confirmatory test. No p-values are generated.",
    "The independent analysis unit is the paired session block, never requests or overhead samples.",
    "Block independence, randomized order, carryover control and workload comparability require external verification.",
    "Means give equal weight to blocks; per-block quantiles use linear interpolation (type 7). p99 requires >=1000 values in that block and channel.",
    "95% percentile bootstrap intervals resample complete baseline/candidate block pairs with replacement; <2 pairs yields no interval.",
    "Intervals are marginal, unadjusted, exploratory and unreliable with few blocks; they are not simultaneous confidence or equivalence tests.",
    "Difference = mean(candidate - baseline); ratio = mean(candidate) / mean(baseline), not mean of block ratios.",
    "Paired standardized dz = mean(block differences) / sample SD(block differences), without small-sample correction. Zero SD or <2 blocks is undefined; dz is not comparable across dependence structures.",
    "Undefined ratio/dz bootstrap draws are counted; their entire interval is withheld rather than silently dropping draws.",
    "Complete-case contrasts may be biased by missingness. All excluded, failed, aborted, invalid, duplicate and known missing outcomes remain in the ledger.",
    "Regime is actually observed, not intended. Unattained regimes and calibration outcomes are excluded from efficacy; splits, hardware, workloads and regimes are never pooled.",
    "Model IDs and policy variants stay separate. No models are fitted or selected; fit/validation/pilot summaries remain exploratory.",
    "Pareto points use exactly the same complete blocks for both policies in each panel; frontiers are descriptive only, without a success claim.",
    "Same-response flags require >=2 complete pairs, explicit foreground p95 tolerance and positive background throughput difference; marginal CI checks do not establish joint coverage or practical equivalence.",
    "Background throughput is observed useful units / measurement seconds; missing completion times are never inferred from throughput.",
    "Overhead is summarized within outcome/role/phase first, then equally across blocks. Sample counts are diagnostic; CPU normalization is producer-defined, not inferred.",
    "Missing files can only be identified from an explicit expected-outcome manifest; without it, undiscovered trials cannot be enumerated.",
]


def _finite(value, minimum=0):
    if type(value) not in (int, float):
        return False
    try:
        return math.isfinite(value) and value >= minimum
    except OverflowError:
        return False


def _object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON key: " + key)
        result[key] = value
    return result


def _reject_constant(value):
    raise ValueError("Nonfinite JSON number: " + value)


def _parse_float(value):
    result = float(value)
    if not math.isfinite(result):
        _reject_constant(value)
    return result


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"), object_pairs_hook=_object,
                      parse_constant=_reject_constant, parse_float=_parse_float)


def _json(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, allow_nan=False, indent=2) + "\n"


def quantile(values, probability):
    """Type-7 empirical quantile, including singleton input; empty -> None."""
    if not values:
        return None
    ordered = sorted(values)
    index = (len(ordered) - 1) * probability
    lower = math.floor(index)
    upper = math.ceil(index)
    fraction = index - lower
    return ordered[lower] * (1 - fraction) + ordered[upper] * fraction


def _mean(values):
    values = list(values)
    return statistics.mean(values) if values else None


def _ratio(numerator, denominator):
    value = numerator / denominator if denominator else None
    return value if value is not None and math.isfinite(value) else None


def validate_outcome(value):
    """Validate the producer contract without filling in missing measurements."""
    if not isinstance(value, dict):
        return ["outcome must be an object"]
    errors = []

    def require(name, valid, description):
        if name not in value or not valid(value[name]):
            errors.append(name + " " + description)

    require("schema_version", lambda x: type(x) is int and x == 2, "must be integer 2")
    for name in ("session_block_id", "hardware_id", "workload_id", "intended_regime", "policy_variant"):
        require(name, lambda x: isinstance(x, str) and bool(x.strip()), "must be a nonempty string")
    for name, options in (("split", ("fit", "validation", "test", "pilot")),
                          ("condition", tuple("ABCDEF") + ("calibration",)),
                          ("regime", ("cpu", "ram", "mixed", "healthy")),
                          ("status", ("completed", "failed", "aborted", "invalid"))):
        require(name, lambda x, opts=options: isinstance(x, str) and x in opts, "has an unsupported value")
    for name in ("regime_attained", "correctness_passed", "restored"):
        require(name, lambda x: type(x) is bool, "must be Boolean")
    require("candidate_action", lambda x: type(x) is int and x in (0, 1, 3, 4, 5, 7) or x is None and value.get("condition") in ("D", "E", "F"),
            "must be 0/1/3/4/5/7, or null for a dynamic policy with no single action")
    require("model_id", lambda x: x is None or isinstance(x, str) and bool(x.strip()), "must be null or a nonempty string")
    for name in ("background_units", "measurement_seconds"):
        require(name, _finite, "must be finite and nonnegative")
    for name in ("background_completion_seconds", "reaccess_ms"):
        require(name, lambda x: x is None or _finite(x), "must be null or finite and nonnegative")
    for name in ("foreground_latency_ms", "foreground_wall_ms"):
        if name == "foreground_wall_ms" and name not in value:
            continue
        require(name, lambda x: isinstance(x, list) and all(_finite(n) for n in x), "must be a finite nonnegative number array")
    if (isinstance(value.get("foreground_latency_ms"), list) and isinstance(value.get("foreground_wall_ms"), list)
            and len(value["foreground_latency_ms"]) != len(value["foreground_wall_ms"])):
        errors.append("foreground_wall_ms must match foreground_latency_ms request count")
    require("failures", lambda x: isinstance(x, list), "must be an array")
    require("overhead_samples", lambda x: isinstance(x, list), "must be an array")
    if isinstance(value.get("overhead_samples"), list):
        for index, sample in enumerate(value["overhead_samples"]):
            prefix = "overhead_samples[%d]" % index
            if not isinstance(sample, dict):
                errors.append(prefix + " must be an object")
                continue
            if sample.get("phase") not in PHASES or sample.get("role") not in ROLES:
                errors.append(prefix + " has an unsupported phase/role")
            for name in OVERHEAD_METRICS:
                if name not in sample or not (_finite(sample[name]) or
                        (name in ("cpu_percent", "decision_ms", "log_write_ms") or sample.get("status") == "timing-only") and sample[name] is None):
                    errors.append(prefix + "." + name + " must be finite/nonnegative (timings may be null)")
    return errors


def _key(row, fields):
    return tuple(row.get(name) for name in fields)


def _sort_key(value):
    # JSON sorting gives a total ordering even when model_id is null.
    return json.dumps(value, ensure_ascii=True, sort_keys=True)


def _metadata(value):
    return {name: value.get(name) for name in IDENTITY + ("status", "intended_regime", "regime_attained", "candidate_action")}


def _expected(input_dir, path):
    if path is None:
        return {}
    manifest = read_json(path)
    if not isinstance(manifest, dict) or not isinstance(manifest.get("outcomes"), list):
        raise ValueError("Expected-outcome manifest must be an object with an outcomes array")
    result = {}
    for row in manifest["outcomes"]:
        if not isinstance(row, dict) or not isinstance(row.get("path"), str):
            raise ValueError("Expected outcomes need an explicit relative path")
        relative = Path(row["path"])
        resolved = (input_dir / relative).resolve()
        if relative.is_absolute() or ".." in relative.parts or not resolved.is_relative_to(input_dir) or relative.name != "outcome.json":
            raise ValueError("Expected path must stay inside input and end with outcome.json")
        name = resolved.relative_to(input_dir).as_posix()
        if name in result:
            raise ValueError("Duplicate expected outcome: " + name)
        result[name] = row
    return result


def load_ledger(input_dir, expected_outcomes=None):
    input_dir = Path(input_dir).resolve()
    if not input_dir.is_dir():
        raise ValueError("Input must be an existing directory")
    expected = _expected(input_dir, expected_outcomes)
    discovered = {p.relative_to(input_dir).as_posix(): p for p in input_dir.rglob("outcome.json")}
    ledger = []
    for name in sorted(set(discovered) | set(expected)):
        path = discovered.get(name, input_dir / name)
        row = dict(source_path=name, sha256=None, status="missing", schema_valid=False,
                   eligible=False, validation_errors=[], exclusion_reasons=[], outcome=None,
                   expected=expected.get(name))
        ledger.append(row)
        if not path.exists():
            row["exclusion_reasons"].append("missing_outcome")
            continue
        if path.is_symlink() or not path.resolve().is_relative_to(input_dir):
            row["status"] = "invalid"
            row["exclusion_reasons"].append("unsafe_input_path")
            continue
        try:
            data = path.read_bytes()
            row["sha256"] = hashlib.sha256(data).hexdigest()
        except OSError as exc:
            row["status"] = "invalid"
            row["exclusion_reasons"].append("unreadable_outcome")
            row["validation_errors"] = [str(exc)]
            continue
        try:
            value = json.loads(data.decode("utf-8-sig"), object_pairs_hook=_object,
                               parse_constant=_reject_constant, parse_float=_parse_float)
        except (ValueError, UnicodeError) as exc:
            row.update(status="invalid", raw_text=data.decode("utf-8", errors="replace"),
                       validation_errors=[str(exc)], exclusion_reasons=["malformed_json"])
            continue
        row["outcome"] = value
        errors = validate_outcome(value)
        if isinstance(value, dict):
            row.update(_metadata(value))
        if errors:
            row.update(status="invalid", validation_errors=errors, exclusion_reasons=["invalid_schema"])
            continue
        row["schema_valid"] = True
        reasons = row["exclusion_reasons"]
        # The matrix adapter preserves the native result plus an exact-byte
        # delivery copy. Explicit verified alias metadata identifies one record;
        # conflicting or undeclared duplicates still invalidate every duplicate.
        alias_path = path.parent.parent / "archive-alias.json"
        if path.parent.name == "native" and alias_path.is_file():
            alias = read_json(alias_path)
            canonical = path.parent.parent / "outcome.json"
            if alias.get("canonical") == "outcome.json" and alias.get("archived_copy") == "native/outcome.json" and canonical.is_file() and not canonical.is_symlink() and \
                    canonical.resolve().is_relative_to(input_dir) and hashlib.sha256(canonical.read_bytes()).hexdigest() == row["sha256"] == alias.get("sha256"):
                row["archive_copy_of"] = canonical.relative_to(input_dir).as_posix()
                reasons.append("identical_archived_copy")
        if value["status"] != "completed":
            reasons.append("status_" + value["status"])
        if value["condition"] == "calibration":
            reasons.append("calibration")
        for field in ("regime_attained", "correctness_passed", "restored"):
            if not value[field]:
                reasons.append(field + "_false")
        if value["failures"]:
            reasons.append("reported_failures")
        if not value["foreground_latency_ms"]:
            reasons.append("missing_foreground_requests")
        if value["measurement_seconds"] <= 0:
            reasons.append("nonpositive_measurement_window")
        if row["expected"]:
            for field in IDENTITY:
                if field in row["expected"] and row["expected"][field] != value[field]:
                    reasons.append("expected_identity_mismatch:" + field)
        row["eligible"] = not reasons
    by_identity = defaultdict(list)
    for row in ledger:
        # Even a malformed-schema duplicate must not permit an optimistic winner.
        if isinstance(row["outcome"], dict) and not row.get("archive_copy_of"):
            identity = _key(row["outcome"], IDENTITY)
            if all(x is None or isinstance(x, str) for x in identity) and all(row["outcome"].get(k) is not None for k in IDENTITY if k != "model_id"):
                by_identity[identity].append(row)
    for rows in by_identity.values():
        if len(rows) > 1:
            for row in rows:
                row["eligible"] = False
                row["exclusion_reasons"].append("duplicate_policy_block")
    return ledger


def summarize_blocks(ledger):
    blocks, overhead = [], []
    for entry in ledger:
        if not entry["schema_valid"] or entry.get("archive_copy_of"):
            continue
        value = entry["outcome"]
        row = dict(_metadata(value), source_path=entry["source_path"], eligible=entry["eligible"],
                   exclusion_reasons=entry["exclusion_reasons"], n_requests=len(value["foreground_latency_ms"]))
        for source, prefix in (("foreground_latency_ms", "foreground"), ("foreground_wall_ms", "foreground_wall")):
            samples = value.get(source, [])
            for percentile in (50, 95, 99):
                row[prefix + "_p%d_ms" % percentile] = quantile(samples, percentile / 100) if percentile != 99 or len(samples) >= 1000 else None
        row["background_throughput_units_per_s"] = (value["background_units"] / value["measurement_seconds"]
                                                      if value["measurement_seconds"] > 0 else None)
        for field in ("background_units", "measurement_seconds", "background_completion_seconds", "reaccess_ms"):
            row[field] = value[field]
        for metric in METRICS:
            if row[metric] is not None and not math.isfinite(row[metric]):
                row[metric] = None
                entry["exclusion_reasons"].append("nonfinite_derived_metric:" + metric)
                row["eligible"] = entry["eligible"] = False
        blocks.append(row)
        grouped = defaultdict(list)
        for sample in value["overhead_samples"]:
            grouped[sample["role"], sample["phase"]].append(sample)
        for (role, phase), samples in sorted(grouped.items()):
            item = dict(_metadata(value), source_path=entry["source_path"], eligible=entry["eligible"],
                        role=role, phase=phase, n_samples=len(samples))
            for metric in OVERHEAD_METRICS:
                numbers = [s[metric] for s in samples if s[metric] is not None]
                item[metric] = _mean(numbers)
                item[metric + "_n_samples"] = len(numbers)
            overhead.append(item)
    return blocks, overhead


def _interval(draws, iterations):
    # Withholding the whole CI avoids conditioning on a nonzero denominator/SD.
    return [quantile(draws, .025), quantile(draws, .975)] if len(draws) == iterations else None


def _estimates(pairs):
    baseline = _mean(p[0] for p in pairs)
    candidate = _mean(p[1] for p in pairs)
    differences = [y - x for x, y in pairs]
    difference = _mean(differences)
    try:
        sd = statistics.stdev(differences) if len(pairs) >= 2 else None
    except OverflowError:
        sd = None
    return dict(baseline_mean=baseline, candidate_mean=candidate, difference=difference,
                ratio=_ratio(candidate, baseline), standardized_dz=_ratio(difference, sd))


def paired_block_bootstrap(pairs, *, iterations=2000, seed=20261110):
    """Pairs are block summaries; each draw selects whole (baseline, candidate) pairs."""
    if type(iterations) is not int or iterations < 1 or type(seed) is not int:
        raise ValueError("Bootstrap iterations must be positive and seed must be an integer")
    pairs = list(pairs)
    if any(len(p) != 2 or any(not _finite(v) for v in p) for p in pairs):
        raise ValueError("Bootstrap requires finite nonnegative baseline/candidate block values")
    result = dict(n_blocks=len(pairs), bootstrap_seed=seed, bootstrap_iterations=iterations,
                  baseline_mean=None, candidate_mean=None, difference=None, ratio=None, standardized_dz=None)
    for name in ("difference", "ratio", "standardized_dz"):
        result[name + "_ci95"] = None
        result[name + "_defined_draws"] = 0
    if not pairs:
        return result
    result.update(_estimates(pairs))
    if len(pairs) < 2:
        return result
    rng = random.Random(seed)
    draws = {name: [] for name in ("difference", "ratio", "standardized_dz")}
    for _ in range(iterations):
        estimates = _estimates([pairs[rng.randrange(len(pairs))] for _ in pairs])
        for name in draws:
            number = estimates[name]
            if number is not None and math.isfinite(number):
                draws[name].append(number)
    for name, values in draws.items():
        result[name + "_ci95"] = _interval(values, iterations)
        result[name + "_defined_draws"] = len(values)
    return result


def _seed(seed, identity):
    data = json.dumps([seed, identity], ensure_ascii=True, sort_keys=True).encode("ascii")
    return int.from_bytes(hashlib.sha256(data).digest()[:8], "big")


def aggregate_hardware(blocks):
    groups = defaultdict(list)
    for row in blocks:
        if row["eligible"]:
            groups[_key(row, COHORT + POLICY)].append(row)
    result = []
    for key, rows in sorted(groups.items(), key=lambda item: _sort_key(item[0])):
        item = dict(zip(COHORT + POLICY, key))
        item.update(n_blocks=len(rows), n_requests_diagnostic=sum(r["n_requests"] for r in rows))
        for metric in METRICS:
            values = [r[metric] for r in rows if r[metric] is not None]
            item[metric] = _mean(values)
            item[metric + "_n_blocks"] = len(values)
        result.append(item)
    return result


def aggregate_overhead(rows):
    groups = defaultdict(list)
    fields = COHORT + POLICY + ("role", "phase", "status", "eligible")
    for row in rows:
        groups[_key(row, fields)].append(row)
    result = []
    for key, items in sorted(groups.items(), key=lambda item: _sort_key(item[0])):
        row = dict(zip(fields, key))
        row.update(n_blocks=len({_key(r, COHORT + ("session_block_id",)) for r in items}),
                   n_outcomes=len(items), n_samples=sum(r["n_samples"] for r in items))
        # Duplicated policy/block outcomes remain in overhead_blocks, not its means.
        counts = defaultdict(int)
        for item in items:
            counts[item["session_block_id"]] += 1
        unique = [r for r in items if counts[r["session_block_id"]] == 1]
        row["n_usable_unique_blocks"] = len(unique)
        for metric in OVERHEAD_METRICS:
            values = [r[metric] for r in unique if r[metric] is not None]
            row[metric] = _mean(values)
            row[metric + "_n_blocks"] = len(values)
        result.append(row)
    return result


def compare_policies(blocks, tolerance, iterations, seed):
    groups = defaultdict(lambda: defaultdict(lambda: defaultdict(list)))
    for row in blocks:
        if row["condition"] != "calibration":
            groups[_key(row, COHORT)][_key(row, POLICY)][row["session_block_id"]].append(row)
    comparisons, same_response, pairing, pareto = [], [], [], []
    for cohort, policies in sorted(groups.items(), key=lambda item: _sort_key(item[0])):
        for baseline, candidate in combinations(sorted(policies, key=_sort_key), 2):
            context = dict(zip(COHORT, cohort))
            for prefix, policy in (("baseline_", baseline), ("candidate_", candidate)):
                context.update({prefix + k: v for k, v in zip(POLICY, policy)})
            by_base, by_candidate = policies[baseline], policies[candidate]
            block_ids = sorted(set(by_base) | set(by_candidate))
            paired = []
            for block_id in block_ids:
                left, right = by_base.get(block_id, []), by_candidate.get(block_id, [])
                usable = len(left) == len(right) == 1 and left[0]["eligible"] and right[0]["eligible"]
                pair_row = dict(context, session_block_id=block_id, paired_eligible=usable,
                                baseline_paths=[r["source_path"] for r in left],
                                candidate_paths=[r["source_path"] for r in right],
                                baseline_reasons=[r["exclusion_reasons"] for r in left] or [["missing_policy_block"]],
                                candidate_reasons=[r["exclusion_reasons"] for r in right] or [["missing_policy_block"]])
                pairing.append(pair_row)
                if usable:
                    paired.append((left[0], right[0]))
            for metric, unit in METRICS.items():
                pairs = [(a[metric], b[metric]) for a, b in paired if a[metric] is not None and b[metric] is not None]
                item = dict(context, metric=metric, unit=unit, n_pairable_blocks=len(paired),
                            n_unpaired_or_excluded=len(block_ids) - len(paired),
                            n_missing_metric_pairs=len(paired) - len(pairs),
                            paired_block_ids=[a["session_block_id"] for a, b in paired if a[metric] is not None and b[metric] is not None])
                item.update(paired_block_bootstrap(pairs, iterations=iterations, seed=_seed(seed, [cohort, baseline, candidate, metric])))
                comparisons.append(item)
            fg, bg = "foreground_p95_ms", "background_throughput_units_per_s"
            joint = [(a, b) for a, b in paired if all(r[m] is not None for r in (a, b) for m in (fg, bg))]
            joint_seed = _seed(seed, [cohort, baseline, candidate, "same_response"])
            # Identical seed/order/n select the same whole block across both metrics.
            fg_stats = paired_block_bootstrap([(a[fg], b[fg]) for a, b in joint], iterations=iterations, seed=joint_seed)
            bg_stats = paired_block_bootstrap([(a[bg], b[bg]) for a, b in joint], iterations=iterations, seed=joint_seed)
            enough = len(joint) >= 2
            within = enough and fg_stats["difference"] <= tolerance
            gain = enough and bg_stats["difference"] > 0
            item = dict(context, n_blocks=len(joint), n_unpaired_or_excluded=len(block_ids) - len(joint),
                        paired_block_ids=[a["session_block_id"] for a, b in joint],
                        foreground_tolerance_ms=tolerance, foreground_difference_ms=fg_stats["difference"],
                        foreground_difference_ci95=fg_stats["difference_ci95"],
                        background_difference_units_per_s=bg_stats["difference"],
                        background_difference_ci95=bg_stats["difference_ci95"],
                        background_ratio=bg_stats["ratio"], background_ratio_ci95=bg_stats["ratio_ci95"],
                        bootstrap_seed=joint_seed, within_tolerance_point_estimate=within,
                        background_gain_point_estimate=gain, same_response_descriptive=within and gain,
                        foreground_ci_upper_within_tolerance=bool(enough and fg_stats["difference_ci95"] and fg_stats["difference_ci95"][1] <= tolerance),
                        background_ci_lower_above_zero=bool(enough and bg_stats["difference_ci95"] and bg_stats["difference_ci95"][0] > 0))
            same_response.append(item)
            if joint:
                pareto.append(dict(context, n_blocks=len(joint),
                    baseline_fg_p95_ms=fg_stats["baseline_mean"], candidate_fg_p95_ms=fg_stats["candidate_mean"],
                    baseline_bg_units_per_s=bg_stats["baseline_mean"], candidate_bg_units_per_s=bg_stats["candidate_mean"],
                    block_points=[dict(session_block_id=a["session_block_id"], baseline_fg_p95_ms=a[fg],
                        candidate_fg_p95_ms=b[fg], baseline_bg_units_per_s=a[bg], candidate_bg_units_per_s=b[bg]) for a, b in joint]))
    return comparisons, same_response, pairing, pareto


def external_holm(path):
    if path is None:
        return []
    spec = read_json(path)
    if not isinstance(spec, dict) or not isinstance(spec.get("families"), list) or not spec["families"]:
        raise ValueError("p-values require a nonempty families array")
    output, family_ids = [], set()
    for family in spec["families"]:
        if not isinstance(family, dict) or family.get("predeclared") is not True or family.get("complete") is not True:
            raise ValueError("Holm requires an explicitly predeclared, complete family")
        family_id = family.get("family_id")
        if not isinstance(family_id, str) or not family_id.strip() or family_id in family_ids:
            raise ValueError("Holm family_id must be nonempty and unique")
        family_ids.add(family_id)
        hypotheses = family.get("hypotheses")
        if not isinstance(hypotheses, list) or not hypotheses:
            raise ValueError("Holm family needs every predeclared hypothesis and its p-value")
        ids, rows = set(), []
        for hypothesis in hypotheses:
            if not isinstance(hypothesis, dict):
                raise ValueError("Hypothesis must be an object")
            for name in ("hypothesis_id", "test", "contrast"):
                if not isinstance(hypothesis.get(name), str) or not hypothesis[name].strip():
                    raise ValueError("External p-values require hypothesis_id, test and contrast")
            if hypothesis["hypothesis_id"] in ids or not _finite(hypothesis.get("p_value")) or hypothesis["p_value"] > 1:
                raise ValueError("Hypothesis IDs must be unique and p-values finite in [0,1]")
            ids.add(hypothesis["hypothesis_id"])
            rows.append(dict(hypothesis, family_id=family_id, source="supplied_external_predeclared",
                             predeclared=True, family_complete=True, family_size=len(hypotheses)))
        previous = 0
        for rank, row in enumerate(sorted(rows, key=lambda r: (r["p_value"], r["hypothesis_id"]))):
            previous = max(previous, min(1, row["p_value"] * (len(rows) - rank)))
            row["holm_p_value"] = previous
        output.extend(sorted(rows, key=lambda r: r["hypothesis_id"]))
    return output


def analyze_directory(input_dir, *, foreground_tolerance_ms, bootstrap_iterations=2000,
                      seed=20261110, expected_outcomes=None, p_values=None):
    if not _finite(foreground_tolerance_ms):
        raise ValueError("Foreground tolerance must be explicitly provided, finite and nonnegative (ms)")
    if type(bootstrap_iterations) is not int or bootstrap_iterations < 100 or type(seed) is not int:
        raise ValueError("Use >=100 bootstrap iterations and an integer seed")
    ledger = load_ledger(input_dir, expected_outcomes)
    blocks, overhead_blocks = summarize_blocks(ledger)
    comparisons, same_response, pairing, pareto = compare_policies(blocks, foreground_tolerance_ms, bootstrap_iterations, seed)
    failures = [row for row in ledger if not row["eligible"]]
    warnings = []
    if expected_outcomes is None:
        warnings.append("No expected-outcome manifest: trials with no outcome file cannot be discovered.")
    if not ledger:
        warnings.append("No outcomes found; no performance or efficacy evidence exists.")
    if not any(row["eligible"] for row in ledger):
        warnings.append("No eligible completed outcomes.")
    if not any(row["n_blocks"] >= 2 for row in same_response):
        warnings.append("No same-response comparison has >=2 complete paired blocks.")
    return dict(schema_version=2, analysis_version=1, analysis_unit="paired_session_block",
                requests_independent=False, foreground_tolerance_ms=foreground_tolerance_ms,
                seed=seed, bootstrap_iterations=bootstrap_iterations,
                quantile_method="linear_type_7", interval_method="paired_block_percentile_95",
                ledger=ledger, blocks=blocks, per_hardware=aggregate_hardware(blocks),
                comparisons=comparisons, same_response=same_response,
                ablations=[r for r in comparisons if r["baseline_condition"] == "E" and r["candidate_condition"] == "F"],
                overhead_blocks=overhead_blocks, overhead=aggregate_overhead(overhead_blocks),
                failures=failures, pairing=pairing, pareto=pareto, p_values=external_holm(p_values),
                warnings=warnings, caveats=CAVEATS)


def _fmt(value):
    if value is None:
        return "NA"
    if isinstance(value, float):
        return format(value, ".5g")
    return str(value)


def _policy_label(row, prefix=""):
    result = str(row.get(prefix + "condition", "?")) + ":" + str(row.get(prefix + "policy_variant", "?"))
    if row.get(prefix + "model_id") is not None:
        result += " [model=" + str(row[prefix + "model_id"]) + "]"
    return result


def _context_label(row):
    return " / ".join(str(row[k]) for k in COHORT)


class Svg:
    """Small vector-only writer: no external fonts, scripts, images or CSS URLs."""
    def __init__(self, title, description, height, width=1280):
        self.parts = [('<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" '
                       'viewBox="0 0 %d %d" role="img" aria-labelledby="title desc">') % (width, height, width, height),
                      '<title id="title">' + escape(title) + '</title>',
                      '<desc id="desc">' + escape(description) + '</desc>',
                      '<rect width="100%" height="100%" fill="white"/>',
                      '<g font-family="Arial, sans-serif" font-size="13" fill="#17212b">']
        self.text(24, 30, title, size=20)
        for i, line in enumerate(textwrap.wrap(description, width=max(50, width // 8))):
            self.text(24, 54 + 17 * i, line, size=12)

    def text(self, x, y, value, size=13, anchor="start", full=None):
        title = '<title>' + escape(str(full)) + '</title>' if full else ''
        self.parts.append('<text x="%.2f" y="%.2f" font-size="%d" text-anchor="%s">%s%s</text>' %
                          (x, y, size, anchor, title, escape(str(value))))

    def line(self, x1, y1, x2, y2, color="#727b84", width=1, dashed=False):
        self.parts.append('<line x1="%.2f" y1="%.2f" x2="%.2f" y2="%.2f" stroke="%s" stroke-width="%s"%s/>' %
                          (x1, y1, x2, y2, color, width, ' stroke-dasharray="5 4"' if dashed else ''))

    def marker(self, x, y, candidate=False, radius=5, full=None):
        color = "#9a4a00" if candidate else "#145a80"
        title = '<title>' + escape(full) + '</title>' if full else ''
        if candidate:
            self.parts.append('<path d="M %.2f %.2f l %.2f %.2f l %.2f %.2f l %.2f %.2f Z" fill="white" stroke="%s" stroke-width="1.8">%s</path>' %
                              (x, y - radius, radius, radius, -radius, radius, -radius, -radius, color, title))
        else:
            self.parts.append('<circle cx="%.2f" cy="%.2f" r="%s" fill="white" stroke="%s" stroke-width="1.8">%s</circle>' %
                              (x, y, radius, color, title))

    def finish(self):
        return "\n".join(self.parts + ["</g></svg>\n"])


def _short(value, width):
    return textwrap.shorten(value, width=width, placeholder="...")


def pareto_svg(rows):
    description = ("Descriptive only. Lower foreground p95 (ms) and higher background throughput (units/s) are preferred. "
                   "Each panel uses common complete paired blocks. Small symbols: individual blocks; large: equal-block means. "
                   "Circle=baseline; diamond=candidate. Dashed segment joins nondominated means only. See pareto.csv and ledger.json.")
    height = 120 + max(1, math.ceil(len(rows) / 2)) * 430
    svg = Svg("Foreground / background Pareto plots", description, height)
    if not rows:
        svg.text(30, 180, "No complete paired blocks: no Pareto points or efficacy claim.")
    for index, row in enumerate(rows):
        ox, oy = 30 + (index % 2) * 630, 125 + (index // 2) * 430
        svg.text(ox, oy, _short(_context_label(row), 77), size=12, full=_context_label(row))
        label = _policy_label(row, "baseline_") + " vs " + _policy_label(row, "candidate_")
        svg.text(ox, oy + 20, _short(label, 77), size=12, full=label)
        svg.text(ox, oy + 39, "n=%d paired blocks; descriptive, no test" % row["n_blocks"], size=12)
        left, top, width, ph = ox + 75, oy + 65, 440, 255
        points = row["block_points"] + [row]
        xmax = min(max(p[k] for p in points for k in ("baseline_fg_p95_ms", "candidate_fg_p95_ms")) * 1.15, sys.float_info.max) or 1
        ymax = min(max(p[k] for p in points for k in ("baseline_bg_units_per_s", "candidate_bg_units_per_s")) * 1.15, sys.float_info.max) or 1
        x = lambda value: left + (value / xmax) * width
        y = lambda value: top + ph - (value / ymax) * ph
        for tick in range(6):
            px, py = x(xmax * (tick / 5)), y(ymax * (tick / 5))
            svg.line(px, top, px, top + ph, color="#e2e6e9")
            svg.line(left, py, left + width, py, color="#e2e6e9")
            svg.text(px, top + ph + 20, _fmt(xmax * (tick / 5)), size=11, anchor="middle")
            svg.text(left - 8, py + 4, _fmt(ymax * (tick / 5)), size=11, anchor="end")
        svg.line(left, top + ph, left + width, top + ph, color="#17212b")
        svg.line(left, top, left, top + ph, color="#17212b")
        svg.text(left + width / 2, top + ph + 44, "Foreground p95 (ms), lower preferred", size=12, anchor="middle")
        svg.text(left, top - 12, "Background throughput (units/s), higher preferred", size=12)
        bx, by = row["baseline_fg_p95_ms"], row["baseline_bg_units_per_s"]
        cx, cy = row["candidate_fg_p95_ms"], row["candidate_bg_units_per_s"]
        b_dominates = bx <= cx and by >= cy and (bx < cx or by > cy)
        c_dominates = cx <= bx and cy >= by and (cx < bx or cy > by)
        if not b_dominates and not c_dominates and (bx != cx or by != cy):
            svg.line(x(bx), y(by), x(cx), y(cy), dashed=True)
        for point in row["block_points"]:
            for candidate, prefix in ((False, "baseline_"), (True, "candidate_")):
                svg.marker(x(point[prefix + "fg_p95_ms"]), y(point[prefix + "bg_units_per_s"]), candidate,
                           radius=2.5, full=point["session_block_id"] + " " + prefix.rstrip("_"))
        svg.marker(x(bx), y(by), radius=7, full="Baseline equal-block mean")
        svg.marker(x(cx), y(cy), candidate=True, radius=7, full="Candidate equal-block mean")
        svg.text(ox, oy + 405, "Mean coordinates and raw block points: pareto.csv / pareto.json", size=11)
    return svg.finish()


def effect_svg(rows, metric, unit):
    description = ("Candidate minus baseline in raw units. Points: equal-block mean differences; whiskers: seeded paired-block "
                   "95% percentile bootstrap CI, unadjusted. NA CI is explicitly labelled; n is paired blocks. "
                   "Each row retains its split, hardware, workload and observed regime. See comparisons.csv. Descriptive only.")
    svg = Svg(metric + " paired differences (" + unit + ")", description, 185 + len(rows) * 59, width=1520)
    if not rows:
        svg.text(30, 150, "No estimable paired differences.")
        return svg.finish()
    left, width, top = 750, 500, 125
    values = [0]
    for row in rows:
        values.append(row["difference"])
        if row["difference_ci95"]:
            values.extend(row["difference_ci95"])
    low, high = min(values), max(values)
    scale = max(abs(low), abs(high), 1)
    low, high = low / scale, high / scale
    span = high - low or 1
    low, high = low - .1 * span, high + .1 * span
    x = lambda v: left + (v / scale - low) / (high - low) * width
    for tick in range(6):
        position = left + tick * width / 5
        value = max(-sys.float_info.max, min(sys.float_info.max, (low + (tick / 5) * (high - low)) * scale))
        svg.line(position, top - 10, position, top + len(rows) * 59 - 18, color="#e2e6e9")
        svg.text(position, top + len(rows) * 59 + 15, _fmt(value), size=12, anchor="middle")
    svg.line(x(0), top - 10, x(0), top + len(rows) * 59 - 18, dashed=True, color="#5d6873")
    for index, row in enumerate(rows):
        y = top + index * 59 + 10
        context = _context_label(row)
        contrast = _policy_label(row, "candidate_") + " minus " + _policy_label(row, "baseline_")
        svg.text(24, y, _short(context, 95), size=12, full=context)
        svg.text(24, y + 18, _short(contrast, 95), size=12, full=contrast)
        bounds = row["difference_ci95"]
        if bounds:
            svg.line(x(bounds[0]), y, x(bounds[1]), y, color="#145a80", width=2)
            for bound in bounds:
                svg.line(x(bound), y - 5, x(bound), y + 5, color="#145a80")
        svg.marker(x(row["difference"]), y)
        svg.text(left + width + 18, y, "delta=" + _fmt(row["difference"]) + "; n=" + str(row["n_blocks"]), size=12)
        svg.text(left + width + 18, y + 18, "CI " + ("[" + ", ".join(_fmt(v) for v in bounds) + "]" if bounds else "NA"), size=12)
    svg.text(left + width / 2, top + len(rows) * 59 + 43, "Candidate - baseline (" + unit + "); zero = no mean difference", size=12, anchor="middle")
    return svg.finish()


def bars_svg(title, description, rows, unit):
    svg = Svg(title, description, 190 + max(len(rows), 1) * 55, width=1520)
    if not rows:
        svg.text(30, 145, "No observations in this table; missing values are not zero.")
        return svg.finish()
    maximum = min(max(value for _, value in rows) * 1.08, sys.float_info.max) or 1
    left, width, top = 780, 500, 115
    for tick in range(6):
        x = left + width * tick / 5
        svg.line(x, top - 10, x, top + len(rows) * 55 - 15, color="#e2e6e9")
        svg.text(x, top + len(rows) * 55 + 12, _fmt(maximum * (tick / 5)), size=11, anchor="middle")
    for index, (label, value) in enumerate(rows):
        y = top + index * 55
        for offset, line in enumerate(textwrap.wrap(label, width=99)[:2]):
            svg.text(24, y + offset * 16, line, size=12, full=label)
        svg.line(left, y, left + width * (value / maximum), y, color="#145a80", width=8)
        svg.text(left + width + 18, y + 4, _fmt(value), size=12)
    svg.text(left + width / 2, top + len(rows) * 55 + 42, unit + "; zero baseline", anchor="middle")
    return svg.finish()


def _write_new(path, text):
    with path.open("x", encoding="utf-8", newline="") as stream:
        stream.write(text)


def _write_csv(path, rows):
    columns = list(dict.fromkeys(key for row in rows for key in row))
    # Even empty tables have a parseable, meaningful header.
    columns = columns or ["no_rows"]
    with path.open("x", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=columns)
        writer.writeheader()
        for row in rows:
            writer.writerow({k: json.dumps(v, ensure_ascii=False, sort_keys=True, allow_nan=False)
                             if isinstance(v, (dict, list)) else v for k, v in row.items()})


def markdown_summary(report):
    labels = sorted({str(r["outcome"]["fixture_label"]) for r in report["ledger"]
                     if isinstance(r["outcome"], dict) and "fixture_label" in r["outcome"]})
    lines = ["# ResearchLab outcome analysis", "", "Descriptive analysis; no efficacy verdict.", "",
             "- Analysis unit: paired session block; requests are not independent replicates.",
             "- Recorded outcomes: %d; eligible: %d; excluded or missing: %d." %
             (len(report["ledger"]), sum(r["eligible"] for r in report["ledger"]), len(report["failures"])),
             "- Foreground p95 tolerance: %s ms (explicitly supplied)." % _fmt(report["foreground_tolerance_ms"]),
             "- Bootstrap: %d paired-block draws; master seed %d; 95%% percentile intervals." %
             (report["bootstrap_iterations"], report["seed"]),
             "- Supplied external p-values: %d. No p-values inferred from bootstrap intervals." % len(report["p_values"]), ""]
    if labels:
        lines += ["Producer fixture labels (unverified): " + "; ".join(escape(v) for v in labels), ""]
    lines += ["## Warnings", ""] + ["- " + item for item in report["warnings"]] + [""]
    lines += ["## Per-hardware summaries", "", "Equal-weight means of eligible block summaries; n = blocks.", "",
              "| Split / hardware / workload / observed regime | Policy | Blocks | FG p95 ms | BG units/s |", "| --- | --- | ---: | ---: | ---: |"]
    md = lambda s: str(s).replace("|", "\\|").replace("\n", " ").replace("\r", " ")
    for row in report["per_hardware"]:
        lines.append("| %s | %s | %d | %s | %s |" % (md(_context_label(row)), md(_policy_label(row)), row["n_blocks"],
                     _fmt(row["foreground_p95_ms"]), _fmt(row["background_throughput_units_per_s"])))
    lines += ["", "## Same-response comparisons", "",
              "Point flags below require at least two complete pairs. They are descriptive and do not prove equivalence or joint confidence.", "",
              "| Cohort | Candidate minus baseline | Paired blocks | FG difference ms [95% CI] | BG difference units/s [95% CI] | Within tolerance + positive BG (point only) |",
              "| --- | --- | ---: | --- | --- | --- |"]
    for row in report["same_response"]:
        label = _policy_label(row, "candidate_") + " minus " + _policy_label(row, "baseline_")
        lines.append("| %s | %s | %d | %s %s | %s %s | %s |" %
                     (md(_context_label(row)), md(label), row["n_blocks"], _fmt(row["foreground_difference_ms"]),
                      _fmt(row["foreground_difference_ci95"]), _fmt(row["background_difference_units_per_s"]),
                      _fmt(row["background_difference_ci95"]), row["same_response_descriptive"]))
    lines += ["", "## Artifacts", "",
              "Each CSV table has a JSON counterpart. Full nested raw outcomes, hashes and exclusion reasons are in ledger.json; malformed raw text is preserved there.", "",
              "- blocks: per-outcome latency, throughput, completion and reaccess summaries.",
              "- per_hardware: separate split/hardware/workload/observed-regime/policy/model means.",
              "- comparisons: all policy-pair raw differences, ratios, paired dz and block-bootstrap intervals.",
              "- pairing: union of known blocks for each policy pair, including excluded and unpaired blocks.",
              "- same_response: common complete-block foreground/background contrasts and explicit tolerance checks.",
              "- ablations: F variants compared with E within the same cohort and block.",
              "- overhead_blocks / overhead: role and phase separated, raw samples retained in ledger.",
              "- failures: every efficacy exclusion, including calibration and known missing files (not necessarily runtime failure).",
              "- p_values: externally supplied predeclared complete families only, Holm step-down adjustment.",
              "- pareto: common-pair means and raw block coordinates; pareto.svg is descriptive.",
              "- effect_*.svg: raw-unit difference intervals with units and block n; unavailable intervals labelled NA.",
              "- overhead_*.svg / failures.svg: zero-baseline descriptive block summaries and outcome counts.",
              "- manifest.json: provenance and SHA-256 hashes of every generated artifact except itself.", "",
              "## Methods and limitations", ""] + ["- " + item for item in report["caveats"]] + [""]
    return "\n".join(lines)


def write_analysis(input_dir, output_dir, **options):
    input_dir, output_dir = Path(input_dir).resolve(), Path(output_dir).resolve()
    if output_dir.is_relative_to(input_dir):
        raise ValueError("Output directory must be outside the input tree")
    if output_dir.exists():
        raise ValueError("Output directory already exists; overwrite is refused")
    report = analyze_directory(input_dir, **options)
    # Validate/read optional evidence before reserving output; preserve provenance.
    sources = {}
    for key in ("expected_outcomes", "p_values"):
        if options.get(key) is not None:
            path = Path(options[key]).resolve()
            sources[key] = dict(path=str(path), sha256=hashlib.sha256(path.read_bytes()).hexdigest())
    report["provenance"] = dict(input_directory=str(input_dir), python=sys.version,
        analyzer_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), auxiliary_inputs=sources)
    output_dir.mkdir(parents=True, exist_ok=False)
    for table in TABLES + ("pareto",):
        _write_new(output_dir / (table + ".json"), _json(report[table]))
        _write_csv(output_dir / (table + ".csv"), report[table])
    _write_new(output_dir / "analysis.json", _json(report))
    _write_new(output_dir / "SUMMARY.md", markdown_summary(report))
    _write_new(output_dir / "pareto.svg", pareto_svg(report["pareto"]))
    for metric, unit in METRICS.items():
        rows = [r for r in report["comparisons"] if r["metric"] == metric and r["difference"] is not None]
        if rows:
            _write_new(output_dir / ("effect_" + metric + ".svg"), effect_svg(rows, metric, unit))
    if not any(r["difference"] is not None for r in report["comparisons"]):
        _write_new(output_dir / "effect_unavailable.svg", effect_svg([], "Paired differences", "raw units"))
    for metric, unit in (("cpu_percent", "CPU percent (producer-defined normalization)"),
                         ("private_bytes", "Private bytes"), ("working_set_bytes", "Working set bytes"),
                         ("decision_ms", "Decision time (ms)"), ("log_write_ms", "Log write time (ms)")):
        rows = [("%s / %s / %s / %s / %s / eligible=%s / n=%d" %
                 (_context_label(r), _policy_label(r), r["role"], r["phase"], r["status"], r["eligible"], r["n_usable_unique_blocks"]), r[metric])
                for r in report["overhead"] if r[metric] is not None]
        _write_new(output_dir / ("overhead_" + metric + ".svg"), bars_svg("Overhead: " + metric,
            "Descriptive only. Equal-weight means of unique block summaries, separately by role, phase, status and eligibility. "
            "No sample-level replication or sample weighting. Missing timings are not zero. See overhead.csv and overhead_blocks.csv.", rows, unit))
    counts = defaultdict(int)
    for row in report["ledger"]:
        counts[str(row["status"]) + (" / eligible" if row["eligible"] else " / excluded")] += 1
    _write_new(output_dir / "failures.svg", bars_svg("Outcome status and exclusions",
        "Counts of outcome records, not independent blocks or request counts. Completed exclusions include calibration, regime, correctness and restoration exclusions. "
        "Known missing files require the expected-outcome manifest. See failures.json and ledger.json.", sorted(counts.items()), "Outcome records"))
    artifacts = [dict(path=p.name, bytes=p.stat().st_size, sha256=hashlib.sha256(p.read_bytes()).hexdigest())
                 for p in sorted(output_dir.iterdir()) if p.is_file()]
    _write_new(output_dir / "manifest.json", _json(dict(schema_version=2, provenance=report["provenance"],
        seed=report["seed"], foreground_tolerance_ms=report["foreground_tolerance_ms"],
        bootstrap_iterations=report["bootstrap_iterations"], artifacts=artifacts)))
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, type=Path, help="Recursive ResearchLab schema-v2 outcome.json directory (read only)")
    parser.add_argument("--output", required=True, type=Path, help="New directory, outside input; overwrite is refused")
    parser.add_argument("--foreground-tolerance-ms", required=True, type=float, help="Predeclared allowed increase in paired mean block FG p95 (ms)")
    parser.add_argument("--bootstrap-iterations", type=int, default=2000)
    parser.add_argument("--seed", type=int, default=20261110)
    parser.add_argument("--expected-outcomes", type=Path, help="Optional explicit manifest of expected relative outcome paths")
    parser.add_argument("--p-values", type=Path, help="Optional externally computed predeclared complete families; never inferred")
    args = parser.parse_args(argv)
    try:
        report = write_analysis(args.input, args.output, foreground_tolerance_ms=args.foreground_tolerance_ms,
            bootstrap_iterations=args.bootstrap_iterations, seed=args.seed,
            expected_outcomes=args.expected_outcomes, p_values=args.p_values)
    except (OSError, ValueError, OverflowError) as exc:
        parser.exit(2, "Analysis error: " + str(exc) + "\n")
    print("Wrote descriptive analysis: %d outcomes, %d eligible; %s" %
          (len(report["ledger"]), sum(r["eligible"] for r in report["ledger"]), args.output))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
