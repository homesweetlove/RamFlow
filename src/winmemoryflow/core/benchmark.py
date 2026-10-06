"""Before / After 벤치마크. 결과를 JSON, CSV로 저장한다."""
from __future__ import annotations

import csv
import json
import statistics
import subprocess
import sys
import time
from pathlib import Path

from .engine import Engine
from .models import MemorySnapshot


def _avg(snaps: list[MemorySnapshot], attr: str) -> float:
    return statistics.fmean(getattr(s, attr) for s in snaps) if snaps else 0.0


def measure_launch_ms(cmd: list[str] | None = None, runs: int = 3) -> float | None:
    """대표 앱 실행 시간(ms). 기본값은 파이썬 인터프리터 + 표준 라이브러리 import."""
    if cmd is None and getattr(sys, "frozen", False):
        return None  # 패키징된 GUI를 다시 실행하면 벤치마크 재귀가 발생한다.
    cmd = cmd or [sys.executable, "-c", "import json, sqlite3, decimal"]
    times = []
    for _ in range(runs):
        t0 = time.perf_counter()
        subprocess.run(cmd, capture_output=True, timeout=30, check=True,
                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        times.append((time.perf_counter() - t0) * 1000)
    return statistics.median(times)


def measure_responsiveness_ms(samples: int = 150) -> dict:
    """스케줄링 지터: 1ms 슬립이 실제로 얼마나 늦게 깨는가 (시스템 반응성 간접 지표)."""
    over = []
    for _ in range(samples):
        t0 = time.perf_counter()
        time.sleep(0.001)
        over.append((time.perf_counter() - t0) * 1000 - 1.0)
    over.sort()
    return {"mean_ms": round(statistics.fmean(over), 3), "p95_ms": round(over[int(len(over) * 0.95) - 1], 3)}


def _collect(engine: Engine, seconds: float, launch_cmd) -> dict:
    snaps = []
    end = time.time() + seconds
    while True:
        snaps.append(engine.provider.sample())
        if time.time() >= end:
            break
        time.sleep(min(1.0, max(0.05, end - time.time())))
    return {
        "available_ram": _avg(snaps, "avail_phys"), "compressed": _avg(snaps, "compressed"),
        "page_faults_per_sec": _avg(snaps, "page_faults_per_sec"),
        "hard_faults_per_sec": _avg(snaps, "hard_faults_per_sec"),
        "pagefile_io_pages_out_per_sec": _avg(snaps, "pages_output_per_sec"),
        "commit_total": _avg(snaps, "commit_total"),
        "cpu_percent": _avg(snaps, "cpu_percent"),
        "disk_busy_percent": (statistics.fmean(s.disk_busy_percent for s in snaps if s.disk_busy_percent is not None)
                               if any(s.disk_busy_percent is not None for s in snaps) else None),
        "launch_ms": measure_launch_ms(launch_cmd),
        "responsiveness": measure_responsiveness_ms(),
    }


def run_benchmark(engine: Engine, measure_seconds: float = 5.0, settle_seconds: float = 3.0,
                  launch_cmd: list[str] | None = None, out_dir: Path | None = None) -> dict:
    """측정 → 정책 1회 적용(Dry Run 설정 존중) → 안정화 대기 → 재측정."""
    before = _collect(engine, measure_seconds, launch_cmd)
    results = engine.optimize_now()
    time.sleep(settle_seconds)
    after = _collect(engine, measure_seconds, launch_cmd)
    report = {
        "timestamp": time.strftime("%Y-%m-%d %H:%M:%S"),
        "dry_run": engine.settings.dry_run,
        "actions": [{"kind": r.action.kind, "name": r.action.name, "ok": r.success, "dry": r.dry_run,
                     "message": r.message} for r in results],
        "before": before, "after": after,
        "delta": {k: after[k] - before[k] for k in before if isinstance(before[k], (int, float))
                  and isinstance(after[k], (int, float))},
        "note": "이 결과만으로 성능 개선을 증명할 수 없습니다. 동일 작업으로 반복 측정하세요. Dry Run 차이는 측정 노이즈입니다. 응답 시간은 1ms sleep 스케줄링 지터이며 앱 입력 지연이 아닙니다.",
    }
    if out_dir:
        out_dir.mkdir(parents=True, exist_ok=True)
        stem = time.strftime("bench_%Y%m%d_%H%M%S")
        (out_dir / f"{stem}.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        with (out_dir / f"{stem}.csv").open("w", newline="", encoding="utf-8-sig") as f:
            w = csv.writer(f)
            w.writerow(["metric", "before", "after", "delta"])
            for k in before:
                if isinstance(before[k], (int, float)) and isinstance(after[k], (int, float)):
                    w.writerow([k, round(before[k], 2), round(after[k], 2), round(after[k] - before[k], 2)])
        report["saved_to"] = str(out_dir / f"{stem}.json")
    return report
