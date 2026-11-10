import json
import pathlib
import subprocess
import uuid

repo = pathlib.Path(__file__).resolve().parents[1]
root = repo / "work" / ("replay-check-" + uuid.uuid4().hex)
root.mkdir(parents=True)
app = repo / "scripts/ResearchReplay/bin/Release/net10.0-windows/ResearchReplay.exe"
sample = {"System": {"Time": "2026-11-10T00:00:00Z", "RamTotal": 16 << 30, "Available": 4 << 30,
                     "Commit": 4 << 30, "CommitLimit": 32 << 30, "CpuPercent": 95},
          "Process": {"Pid": 987650, "Identity": 12, "Name": "fixture.exe", "Path": "D:\\Fixtures\\fixture.exe",
                      "Session": 1, "WorkingSet": 32 << 20, "PrivateBytes": 16 << 20,
                      "CpuPercent": 30, "IoBytesPerSecond": 0, "ActivityKnown": True},
          "Settings": {"PolicyName": "carp", "Carp": {"StableSamples": 1}},
          "ForegroundPid": 987651, "ForegroundName": "foreground-fixture.exe", "Fullscreen": False,
          "Mode": "background", "MemoryPressure": "Normal", "CpuPressure": True, "IdleSeconds": 0,
          "Designated": True, "OwnedJob": False, "CurrentAction": "None",
          "Evidence": {"Pid": 987650, "Identity": 12, "ObservedAt": "2026-11-10T00:00:00Z",
                       "ModelId": "synthetic-IO-fixture-not-performance", "Contention": "Cpu", "IndependentBlocks": 3,
                       "Candidates": [{"Action": "LowerCpu", "ForegroundGain": .4, "BackgroundLoss": .1,
                                       "ReaccessRisk": 0, "ControlCost": .01, "Confidence": 1}]}}
second = json.loads(json.dumps(sample))
second["Evidence"] = None
inp = root / "input.jsonl"
inp.write_text(json.dumps(sample) + "\n" + json.dumps(second) + "\n", encoding="utf-8")
run = subprocess.run([str(app), "--input", str(inp), "--output", str(root / "ok")], capture_output=True, text=True)
assert run.returncode == 0, run.stderr
rows = [json.loads(line) for line in (root / "ok/replay.jsonl").read_text(encoding="utf-8").splitlines()]
assert len(rows) == 6 and all(row["Applied"] is False for row in rows)
assert rows[2]["Decision"]["Action"] == "LowerCpu" and rows[5]["Decision"]["Action"] == "None"
before = (root / "ok/replay.jsonl").read_bytes()
again = subprocess.run([str(app), "--input", str(inp), "--output", str(root / "ok")], capture_output=True)
assert again.returncode == 2 and (root / "ok/replay.jsonl").read_bytes() == before
bad = root / "bad.jsonl"
bad.write_text(json.dumps(sample) + "\n{broken\n", encoding="utf-8")
fail = subprocess.run([str(app), "--input", str(bad), "--output", str(root / "failure")], capture_output=True)
assert fail.returncode == 1 and (root / "failure/failure.json").exists()
assert len((root / "failure/replay.jsonl").read_text(encoding="utf-8").splitlines()) == 3
print("Replay end-to-end: same-input decisions, missing evidence, no actions, overwrite refusal and partial failure preservation passed.")
