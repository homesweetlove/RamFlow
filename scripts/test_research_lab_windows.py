"""Windows integration smoke using only newly created disposable fixtures."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import uuid

root = Path(__file__).resolve().parents[1]
app = root / "scripts/ResearchLab/bin/Release/net10.0-windows/ResearchLab.exe"
if os.name != "nt":
    print("SKIP ResearchLab Windows integration: Windows is required")
    raise SystemExit(0)
run = root / "work" / ("lab-windows-smoke-" + uuid.uuid4().hex)
run.mkdir(parents=True)


def trial(name, condition, extra=()):
    command = [str(app), "trial", "--condition", condition, "--output", str(run / name), "--regime", "healthy",
               "--seconds", "2", "--background-threads", "1", "--foreground-mib", "4", "--background-mib", "4", *extra]
    result = subprocess.run(command, capture_output=True, text=True, timeout=60)
    evidence = run / name / "outcome.json"
    if not evidence.exists():
        raise AssertionError(result.stderr)
    value = json.loads(evidence.read_text(encoding="utf-8"))
    return result, value


a, absent = trial("absent", "A")
if a.returncode and any("Insufficient safe headroom" in s or "existing RAM Flow instance" in s or "No interactive user session" in s for s in absent["failures"]):
    print("SKIP ResearchLab live smoke: environment headroom/existing instance; failure evidence retained at", run)
    raise SystemExit(0)
assert a.returncode == 0 and absent["actual_apply_count"] == 0 and absent["restored"]
c, fixed = trial("fixed", "C")
assert c.returncode == 0, fixed["failures"]
assert fixed["correctness_passed"] and fixed["restored"] and fixed["actual_apply_count"] >= 1
assert fixed["out_of_scope_requests"] == 0
assert fixed["schema_version"] == 3 and fixed["pre_intervention"]["basis"] == "pre-intervention-window-v1"
assert fixed["pre_intervention"]["stable"] and fixed["pre_intervention"]["regime_attained"]
assert fixed["pre_intervention"]["ended_utc"] <= fixed["treatment_started_utc"]
baseline = run / "fixed/pre-intervention.json"
assert fixed["pre_intervention"]["source_sha256"] == hashlib.sha256(baseline.read_bytes()).hexdigest()
cost = json.loads((run / "fixed/cost-observation.json").read_text(encoding="utf-8"))
assert cost["Features"] == fixed["pre_intervention"]["features"]
assert cost["Contention"] == json.loads(baseline.read_text(encoding="utf-8"))["Regime"]
audit = [json.loads(line) for line in (run / "fixed/audit/events.jsonl").read_text(encoding="utf-8").splitlines()]
assert all(row["Time"] >= fixed["treatment_started_utc"] for row in audit), "Audit action preceded the frozen baseline"
assert fixed["observer_control_writes"] and all(row["success"] and row["retries"] >= 0 and row["elapsed_ms"] >= 0 for row in fixed["observer_control_writes"])
assert any(s["role"] == "observer" for s in fixed["overhead_samples"])
assert any(s.get("status") == "timing-only" and s["log_write_ms"] is not None for s in fixed["overhead_samples"])
payload = run / "fixed/outcome.json"
before = hashlib.sha256(payload.read_bytes()).hexdigest()
duplicate = subprocess.run([str(app), "trial", "--output", str(run / "fixed")], capture_output=True)
assert duplicate.returncode != 0 and hashlib.sha256(payload.read_bytes()).hexdigest() == before
bad, failed = trial("bad-model", "E", ["--model", str(run / "nonexistent-model.json")])
assert bad.returncode != 0 and failed["status"] == "failed" and failed["failures"]
assert failed["actual_apply_count"] == 0
print("ResearchLab Windows integration passed: absence/fixed setters/readback/independent observer/timings/overwrite/failure preservation; no user process modified.")
