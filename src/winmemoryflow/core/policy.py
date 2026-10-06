"""단계별 정책: 압박 레벨 → 실행할 행동 계획. 쿨다운/백오프로 과잉 개입을 막는다."""
from __future__ import annotations

from dataclasses import dataclass, field

from ..config.settings import Settings
from .models import GB, MB, Category, Level, MemorySnapshot, PressureState, ProcessAssessment

# Windows Memory Priority 값 (1~5)
PRIO_NORMAL = 5
PRIO_BACKGROUND = 3
PRIO_IDLE = 1

TRIM_ESTIMATE_RATIO = 0.55   # Dry Run에서 쓰는 감소 추정 비율


@dataclass
class PlannedAction:
    kind: str             # trim_ws | set_priority | purge_standby | pagefile_advice | critical_advice
    pid: int = 0
    name: str = ""
    value: int = 0
    reason: str = ""
    est_bytes: int = 0
    extra: dict = field(default_factory=dict)


class PolicyEngine:
    MAX_TRIMS_L1 = 2
    MAX_TRIMS_L2 = 5

    def __init__(self, settings: Settings):
        self.s = settings
        self._trim_last: dict[int, tuple[float, float]] = {}   # pid → (시각, 백오프 배수)
        self._global_trim_ts = -1e18
        self._lowered: dict[int, int] = {}                     # pid → 현재 낮춘 우선순위
        self._pf_advice_ts = -1e18
        self._crit_ts = -1e18
        self._purge_ts = -1e18

    # ---- 실행 결과 피드백
    def record_trim(self, pid: int, ts: float, before: int, after: int) -> None:
        mult = 1.0
        if before > 0 and (before - after) / before < 0.10:
            prev = self._trim_last.get(pid, (0, 1.0))[1]
            mult = min(8.0, prev * 2)     # 효과가 없으면 쿨다운을 두 배로
        self._trim_last[pid] = (ts, mult)
        self._global_trim_ts = ts

    def record_priority(self, pid: int, value: int) -> None:
        if value >= PRIO_NORMAL:
            self._lowered.pop(pid, None)
        else:
            self._lowered[pid] = value

    def record_purge(self, ts: float) -> None:
        self._purge_ts = ts

    # ---- 계획
    def plan(self, level: Level, snap: MemorySnapshot, assess: list[ProcessAssessment],
             pressure: PressureState, ai_active: bool, fullscreen: bool) -> list[PlannedAction]:
        now = snap.timestamp
        actions: list[PlannedAction] = []
        by_pid = {a.info.pid: a for a in assess}

        # 0) 낮춰둔 Memory Priority 복원 (항상, 설정과 무관) — 다시 사용 중이면 즉시 되돌린다.
        for pid, cur in list(self._lowered.items()):
            a = by_pid.get(pid)
            if a is None:
                self._lowered.pop(pid, None)
            elif (a.protected_reason or a.category in (Category.FOREGROUND, Category.NORMAL)
                  or level == Level.NORMAL or not self.s.auto_optimization or fullscreen):
                actions.append(PlannedAction("set_priority", pid, a.info.name, PRIO_NORMAL,
                                             "다시 사용 중이거나 압박 해소 → 우선순위 복원"))

        # 어드바이저리(권장/경고)는 auto_optimization과 무관하게 제공
        if level >= Level.SEVERE:
            actions += self._pagefile_advice(snap, now)
        if level >= Level.CRITICAL:
            actions += self._critical_advice(assess, now)

        if not self.s.auto_optimization or fullscreen:
            return actions   # 전체화면 앱(게임/영상) 중에는 어떤 조작도 하지 않는다

        eff = level
        if eff < Level.MODERATE:
            return actions

        bl = {n.lower().removesuffix(".exe") for n in self.s.blacklist}
        cand = [a for a in assess if a.protected_reason is None]

        # 1) Memory Priority 낮추기 (가역적, 부작용 적음)
        for a in cand:
            want = PRIO_IDLE if a.category == Category.DEEP_IDLE else \
                PRIO_BACKGROUND if a.category == Category.IDLE else None
            if want is not None and self._lowered.get(a.info.pid) != want:
                actions.append(PlannedAction("set_priority", a.info.pid, a.info.name, want,
                                             f"{a.category.value} 프로세스 Memory Priority 낮춤"))

        # 2) Working Set 축소
        min_ws, max_n, gap = self.s.min_trim_ws_mb_l2 * MB, 2, 120
        def ok(a):
            return a.category == Category.DEEP_IDLE and a.inactive_sec >= self.s.inactive_minutes_l2 * 60
        if eff >= Level.HIGH and now - self._global_trim_ts >= gap:
            picked = 0
            for a in sorted(cand, key=lambda x: x.info.ws, reverse=True):
                if picked >= max_n:
                    break
                if a.info.ws < min_ws or not ok(a):
                    continue
                last, mult = self._trim_last.get(a.info.pid, (-1e18, 1.0))
                if now - last < self.s.trim_cooldown_min * 60 * mult:
                    continue
                actions.append(PlannedAction(
                    "trim_ws", a.info.pid, a.info.name, 0,
                    f"{a.category.value} {int(a.inactive_sec // 60)}분 비활성",
                    est_bytes=int(a.info.ws * TRIM_ESTIMATE_RATIO), extra={"ws": a.info.ws, "identity": a.info.create_time}))
                picked += 1

        for action in actions:
            if action.pid in by_pid:
                action.extra["identity"] = by_pid[action.pid].info.create_time
        return actions

    def _pagefile_advice(self, snap: MemorySnapshot, now: float) -> list[PlannedAction]:
        if not self.s.pagefile_recommendation or now - self._pf_advice_ts < 1800:
            return []
        free = snap.commit_limit - snap.commit_total
        if snap.commit_ratio >= 0.85 or free < 2 * GB:
            self._pf_advice_ts = now
            return [PlannedAction("pagefile_advice", reason=(
                f"Commit {snap.commit_ratio * 100:.0f}% 사용, 여유 {free / GB:.1f}GB"))]
        return []

    def _critical_advice(self, assess: list[ProcessAssessment], now: float) -> list[PlannedAction]:
        if now - self._crit_ts < 120:
            return []
        self._crit_ts = now
        top = sorted([a for a in assess if a.protected_reason is None],
                     key=lambda a: a.info.private, reverse=True)[:5]
        return [PlannedAction("critical_advice", reason="메모리 부족 임박 — 종료 후보 추천(자동 종료 안 함)",
                              extra={"candidates": [a.to_dict() for a in top]})]
