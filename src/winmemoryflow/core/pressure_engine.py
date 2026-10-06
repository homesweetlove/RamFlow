"""Memory Pressure 점수 엔진 (0~100, macOS Memory Pressure 유사)."""
from __future__ import annotations

from collections import deque

from .models import GB, LEVEL_COLOR, Level, MemorySnapshot, PressureState


def interp(x: float, pts: list[tuple[float, float]]) -> float:
    """구간 선형 보간 (양 끝은 고정)."""
    if x <= pts[0][0]:
        return pts[0][1]
    for (x0, y0), (x1, y1) in zip(pts, pts[1:]):
        if x <= x1:
            return y0 + (y1 - y0) * (x - x0) / (x1 - x0)
    return pts[-1][1]


WEIGHTS = {"available": 0.30, "commit": 0.15, "hard_fault": 0.15, "used": 0.15,
           "compression": 0.10, "pagefile": 0.08, "trend": 0.07}


class PressureEngine:
    def __init__(self, yellow=25.0, orange=50.0, red=75.0, critical=90.0,
                 confirm_up=3, confirm_down=5, hysteresis=5.0,
                 alpha_up=0.35, alpha_down=0.08):
        self.th = (yellow, orange, red, critical)
        self.confirm_up, self.confirm_down = confirm_up, confirm_down
        self.hyst = hysteresis
        self.a_up, self.a_down = alpha_up, alpha_down
        self.level = Level.NORMAL
        self.ema: float | None = None
        self._hist: deque[tuple[float, float, float]] = deque()  # (t, avail_ratio, score)
        self._prev: MemorySnapshot | None = None
        self._comp_growth = 0.0     # EMA MB/s
        self._hf_ema = 0.0
        self._up = self._down = self._crit = 0

    def set_thresholds(self, yellow, orange, red, critical) -> None:
        self.th = (yellow, orange, red, critical)

    # ---- 구성 요소
    def components(self, s: MemorySnapshot) -> dict[str, float]:
        avail_gb = s.avail_phys / GB
        a_ratio = interp(s.avail_ratio, [(0.03, 100), (0.08, 80), (0.15, 50), (0.25, 20), (0.40, 0)])
        a_abs = interp(avail_gb, [(0.3, 100), (0.5, 90), (1.0, 65), (2.0, 35), (4.0, 10), (6.0, 0)])
        commit = interp(s.commit_ratio, [(0.5, 0), (0.7, 20), (0.85, 50), (0.95, 90), (1.0, 100)])
        pf_pct = 100.0 * s.pagefile_used / s.pagefile_total if s.pagefile_total else 0.0
        pagefile = interp(pf_pct, [(10, 0), (30, 20), (60, 50), (85, 85), (100, 100)])
        hard = interp(self._hf_ema, [(50, 0), (200, 20), (500, 50), (1500, 85), (3000, 100)])
        used = interp(s.used_phys / s.total_phys if s.total_phys else 0,
                      [(0.6, 0), (0.8, 30), (0.9, 60), (0.97, 100)])
        comp_size = interp(s.compressed / s.total_phys if s.total_phys else 0,
                           [(0.05, 0), (0.15, 30), (0.30, 70)])
        comp_rate = interp(max(0.0, self._comp_growth), [(0, 0), (5, 25), (30, 60), (100, 100)])
        return {"available": max(a_ratio, a_abs), "commit": commit, "hard_fault": hard,
                "used": used, "compression": max(comp_size, comp_rate), "pagefile": pagefile,
                "trend": self._trend(s)}

    def _trend(self, s: MemorySnapshot) -> float:
        """최근 ~60초 동안 Available이 RAM 대비 분당 몇 % 줄었는가."""
        target = s.timestamp - 60
        old = None
        for t, ar, _ in self._hist:
            if t >= target:
                old = (t, ar)
                break
        if old is None or s.timestamp - old[0] < 20:
            return 0.0
        drop_pct_per_min = (old[1] - s.avail_ratio) * 100 / ((s.timestamp - old[0]) / 60)
        return interp(drop_pct_per_min, [(0, 0), (2, 20), (5, 50), (10, 90)])

    # ---- 메인
    def update(self, s: MemorySnapshot, bias: float = 0.0) -> PressureState:
        dt = (s.timestamp - self._prev.timestamp) if self._prev else 0.0
        if self._prev and dt > 0:
            g = (s.compressed - self._prev.compressed) / (1024 ** 2) / dt
            self._comp_growth = 0.7 * self._comp_growth + 0.3 * g
        self._hf_ema = s.hard_faults_per_sec if self._prev is None else \
            0.6 * self._hf_ema + 0.4 * s.hard_faults_per_sec
        self._prev = s

        comps = self.components(s)
        weighted = sum(comps[k] * w for k, w in WEIGHTS.items())
        raw = max(weighted, 0.75 * max(comps["available"], comps["commit"]))
        # 하한: Available 500MB 미만 또는 Commit 95% 이상은 최소 Red 구간(80점)
        if s.avail_phys < 0.5 * GB or s.commit_ratio >= 0.95:
            raw = max(raw, 80.0)
        # Hard Fault 급증은 메모리가 실제로 빠듯할 때(Available 20% 미만)만 의미가 크다
        if s.avail_ratio < 0.20:
            raw = max(raw, 0.6 * comps["hard_fault"])
        # Standby가 크면 파일 캐시가 건강하다는 뜻 → 최대 8점 완화
        raw -= 8.0 * min(1.0, (s.standby / s.total_phys) / 0.15) if s.total_phys else 0.0
        raw = max(0.0, min(100.0, raw + bias))

        if self.ema is None:
            self.ema = raw
        else:
            a = self.a_up if raw > self.ema else self.a_down
            self.ema += a * (raw - self.ema)

        hard_crit = s.avail_phys < 0.3 * GB or s.commit_ratio >= 0.97
        self._crit = self._crit + 1 if hard_crit else 0
        self._update_level(self.ema, self._crit >= 2)

        self._hist.append((s.timestamp, s.avail_ratio, self.ema))
        while self._hist and self._hist[0][0] < s.timestamp - 300:
            self._hist.popleft()
        w30 = [sc for t, _, sc in self._hist if t >= s.timestamp - 30]
        return PressureState(
            score=self.ema, raw_score=raw, level=self.level, color=LEVEL_COLOR[self.level],
            components=comps, avg_30s=sum(w30) / len(w30),
            avg_5m=sum(sc for _, _, sc in self._hist) / len(self._hist), hard_critical=hard_crit)

    def _level_from(self, score: float) -> Level:
        y, o, r, c = self.th
        return Level.CRITICAL if score >= c else Level.SEVERE if score >= r else \
            Level.HIGH if score >= o else Level.MODERATE if score >= y else Level.NORMAL

    def _update_level(self, score: float, hard_critical: bool) -> None:
        target = Level.CRITICAL if hard_critical else self._level_from(score)
        if target > self.level:
            self._up += 1
            self._down = 0
            if self._up >= self.confirm_up or hard_critical:
                self.level, self._up = target, 0
            return
        self._up = 0
        low = Level.CRITICAL if hard_critical else self._level_from(score + self.hyst)
        if low < self.level:
            self._down += 1
            if self._down >= self.confirm_down:
                self.level = Level(self.level - 1) if low < self.level - 1 else low
                self._down = 0
        else:
            self._down = 0
