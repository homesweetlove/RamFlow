"""가벼운 사용 패턴 학습: 시간대별 EMA와 상주 프로세스 빈도. (AI 모델 없음)"""
from __future__ import annotations

import json
import time
from pathlib import Path

ALPHA = 0.1


class UsageLearner:
    def __init__(self, path: Path | None = None, enabled: bool = False):
        self.enabled = enabled
        self.path = path
        self.hours: dict[int, dict] = {h: {"used": 0.0, "n": 0, "procs": {}} for h in range(24)}
        self.commit_hist: list[int] = []     # 분 단위 Commit 샘플 (최근 3일)
        self.commit_peak = 0
        self._acc: list[tuple[float, int, list[str]]] = []
        self._last_min = 0.0
        self._last_save = 0.0
        self._load()

    # ---- 관찰
    def observe(self, ts: float, used_ratio: float, commit: int, top_names: list[str]) -> None:
        self.commit_peak = max(self.commit_peak, commit)
        if ts - self._last_min < 60:
            return
        self._last_min = ts
        self.commit_hist.append(commit)
        del self.commit_hist[:-4320]
        if not self.enabled:
            return
        h = self.hours[time.localtime(ts).tm_hour]
        h["used"] = used_ratio if h["n"] == 0 else h["used"] + ALPHA * (used_ratio - h["used"])
        h["n"] += 1
        procs = h["procs"]
        for k in procs:
            procs[k] *= (1 - ALPHA)
        for n in top_names[:3]:
            procs[n] = procs.get(n, 0.0) + ALPHA
        h["procs"] = dict(sorted(procs.items(), key=lambda kv: kv[1], reverse=True)[:5])
        if ts - self._last_save > 600:
            self.save(ts)

    # ---- 활용
    def commit_p95(self) -> int:
        if len(self.commit_hist) < 10:
            return 0
        s = sorted(self.commit_hist)
        return s[int(len(s) * 0.95) - 1]

    def pressure_bias(self, ts: float) -> float:
        """곧 고부하 시간대가 예상되면 압박 점수를 소폭(최대 +5) 올려 조금 일찍 대응한다."""
        if not self.enabled:
            return 0.0
        h = self.hours[(time.localtime(ts).tm_hour) % 24]
        if h["n"] < 5:
            return 0.0
        return 5.0 if h["used"] > 0.85 else 3.0 if h["used"] > 0.75 else 0.0

    def summary(self) -> list[dict]:
        return [{"hour": h, "used_pct": round(v["used"] * 100), "samples": v["n"],
                 "top": list(v["procs"].keys())[:3]}
                for h, v in self.hours.items() if v["n"] > 0]

    # ---- 저장
    def save(self, ts: float = 0.0) -> None:
        self._last_save = ts
        if not self.path:
            return
        try:
            self.path.write_text(json.dumps({"hours": self.hours, "commit": self.commit_hist,
                                             "peak": self.commit_peak}), encoding="utf-8")
        except OSError:
            pass

    def _load(self) -> None:
        if not self.path or not self.path.exists():
            return
        try:
            raw = json.loads(self.path.read_text(encoding="utf-8"))
            for k, v in raw.get("hours", {}).items():
                self.hours[int(k)] = v
            self.commit_hist = raw.get("commit", [])
            self.commit_peak = raw.get("peak", 0)
        except (OSError, ValueError):
            pass
