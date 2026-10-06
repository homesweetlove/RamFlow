"""Windows 스케줄러를 보조하는 CPU Priority / EcoQoS 정책."""
from .models import Category, Level
from .policy import PlannedAction


class SchedulerAssistant:
    def __init__(self, settings):
        self.s = settings
        self.lowered = set()

    def plan(self, level, snap, assessments, fullscreen=False):
        by_pid = {a.info.pid: a for a in assessments}
        pressure = level >= Level.MODERATE or snap.cpu_percent >= 80
        actions = []
        for kind, pid in list(self.lowered):
            a = by_pid.get(pid)
            if a is None:
                self.lowered.discard((kind, pid))
                continue
            enabled = self.s.cpu_management if kind == "set_cpu_priority" else self.s.ecoqos
            if (a.protected_reason or a.category not in (Category.IDLE, Category.DEEP_IDLE)
                    or not pressure or not enabled or not self.s.auto_optimization or fullscreen):
                actions.append(PlannedAction("restore_resource", pid, a.info.name,
                                             reason="작업 재개/압박 해소/정책 중지", extra={"resource": kind}))
        if not pressure or not self.s.auto_optimization or fullscreen:
            return actions
        for a in assessments:
            if a.protected_reason or a.category not in (Category.IDLE, Category.DEEP_IDLE):
                continue
            for kind, enabled, value in (("set_cpu_priority", self.s.cpu_management, 0x4000),
                                          ("set_ecoqos", self.s.ecoqos, 1)):
                if enabled and (kind, a.info.pid) not in self.lowered:
                    actions.append(PlannedAction(kind, a.info.pid, a.info.name, value,
                                                 "30분 이상 비활성 앱의 백그라운드 부하 완화",
                                                 extra={"identity": a.info.create_time}))
        return actions

    def feedback(self, results):
        for r in results:
            if not r.success:
                continue
            a = r.action
            if a.kind == "restore_resource":
                self.lowered.discard((a.extra["resource"], a.pid))
            elif a.kind in ("set_cpu_priority", "set_ecoqos"):
                self.lowered.add((a.kind, a.pid))
