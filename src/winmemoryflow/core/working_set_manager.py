"""실행 직전 보호 확인과 원래 설정 복원. 시뮬레이션은 실제 복원 기록을 만들지 않는다."""
from __future__ import annotations

from dataclasses import dataclass

from . import eventlog as ev
from .eventlog import EventLog
from .models import fmt_bytes
from .policy import PlannedAction, PolicyEngine
from .providers import SystemProvider


@dataclass
class ActionResult:
    action: PlannedAction
    dry_run: bool
    success: bool
    message: str
    before: int = 0
    after: int = 0


class WorkingSetManager:
    def __init__(self, provider: SystemProvider, policy: PolicyEngine, log: EventLog):
        self.p, self.policy, self.log = provider, policy, log
        self.originals: dict[tuple[str, int], tuple[float, object]] = {}
        self._retry_after: dict[tuple[str, int], float] = {}

    def execute(self, actions: list[PlannedAction], ts: float, dry_run: bool) -> list[ActionResult]:
        results = []
        for a in actions:
            if a.kind not in {"trim_ws", "set_priority", "set_cpu_priority", "set_ecoqos", "restore_resource"}:
                continue
            result = self._execute(a, ts, dry_run)
            results.append(result)
            self.log.add(ev.PRIORITY if a.kind != "trim_ws" else ev.WORKING_SET,
                         f"{a.name} (PID {a.pid})", result.message, ts=ts,
                         level="info" if result.success else "warn", dry_run=result.dry_run)
        return results

    def _execute(self, a: PlannedAction, ts: float, dry: bool) -> ActionResult:
        kind = a.extra.get("resource", a.kind)
        key = (kind, a.pid)
        restoring = a.kind == "restore_resource" or (kind == "set_priority" and a.value == 5)
        if restoring and key in self.originals:
            return self._restore(key, a)
        if dry:
            if a.kind == "trim_ws":
                ws = a.extra.get("ws", 0)
                self.policy.record_trim(a.pid, ts, ws, ws - a.est_bytes)
                msg = f"[DRY RUN] {a.name} Working Set을 약 {fmt_bytes(a.est_bytes)} 감소시켰을 것입니다. 추정치이며 보장하지 않습니다. ({a.reason})"
            else:
                if kind == "set_priority":
                    self.policy.record_priority(a.pid, a.value)
                msg = f"[DRY RUN] {a.name} {kind} → {a.value} ({a.reason})"
            return ActionResult(a, True, True, msg)
        if restoring:
            return ActionResult(a, False, True, "실제 변경 이력 없음 — 복원 생략")
        if ts < self._retry_after.get(key, 0):
            return ActionResult(a, False, False, "이전 실패 이후 재시도 대기")
        identity = a.extra.get("identity")
        if identity is None or not self.p.validate_target(a.pid, identity):
            self._retry_after[key] = ts + 60
            return ActionResult(a, False, False, "실행 직전 보호/포그라운드/PID 재사용 확인 — 변경 생략")
        if a.kind == "trim_ws":
            r = self.p.trim_working_set(a.pid)
            if r is None:
                self.policy.record_trim(a.pid, ts, 1, 1)
                return ActionResult(a, False, False, "Working Set 축소 실패")
            before, after = r
            self.policy.record_trim(a.pid, ts, before, after)
            return ActionResult(a, False, True, f"Working Set {fmt_bytes(before)} → {fmt_bytes(after)}", before, after)
        getter, setter = self._accessors(kind)
        old = getter(a.pid)
        if old is None or (kind == "set_cpu_priority" and old not in (0x40, 0x4000, 0x20)):
            self._retry_after[key] = ts + 60
            return ActionResult(a, False, False, "원래 설정 조회 불가 또는 높은 CPU 우선순위 — 변경 생략")
        value = (old[0] | 1, old[1] | 1) if kind == "set_ecoqos" else min(old, a.value) if kind == "set_priority" else a.value
        if kind == "set_cpu_priority" and old == 0x40:
            value = old
        ok = setter(a.pid, value)
        if ok:
            self.originals.setdefault(key, (identity, old))
            if kind == "set_priority":
                self.policy.record_priority(a.pid, a.value)
        else:
            self._retry_after[key] = ts + 60
        return ActionResult(a, False, ok, f"{kind} → {value}: {'적용' if ok else '실패'} ({a.reason})")

    def _accessors(self, kind):
        return {"set_priority": (self.p.get_memory_priority, self.p.set_memory_priority),
                "set_cpu_priority": (self.p.get_cpu_priority, self.p.set_cpu_priority),
                "set_ecoqos": (self.p.get_power_throttling, self.p.set_power_throttling)}[kind]

    def _restore(self, key, action):
        identity, value = self.originals[key]
        current = self.p.process_identity(key[1])
        if current is None or current != identity:
            self.originals.pop(key, None)
            return ActionResult(action, False, True, "원래 프로세스 종료/PID 재사용 — 복원 대상 제거")
        _, setter = self._accessors(key[0])
        ok = setter(key[1], value)
        if ok:
            self.originals.pop(key, None)
            if key[0] == "set_priority":
                self.policy.record_priority(key[1], 5)
        return ActionResult(action, False, ok, f"{key[0]} 원래 값 {value} 복원 {'완료' if ok else '실패(재시도 예정)'}")

    def restore_all(self) -> list[ActionResult]:
        results = []
        for key in list(self.originals):
            a = PlannedAction("restore_resource", key[1], reason="중지/설정 전환/정상 종료", extra={"resource": key[0]})
            result = self._restore(key, a)
            results.append(result)
            self.log.add(ev.PRIORITY, f"PID {key[1]} 원상복구", result.message,
                         level="info" if result.success else "warn")
        return results
