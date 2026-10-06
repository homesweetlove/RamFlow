"""오케스트레이터: 모니터 → 압박 엔진 → 분석 → 정책 → 실행 → 로그를 한 틱으로 묶는다."""
from __future__ import annotations

import threading
import time

import psutil

from ..config.settings import Settings, data_dir
from . import eventlog as ev
from . import llm_detector
from .eventlog import EventLog
from .learning import UsageLearner
from .memory_monitor import MemoryMonitor
from .models import GB, Level, MemorySnapshot, PressureState, ProcessAssessment, fmt_bytes
from .pagefile_manager import PagefileManager
from .policy import PolicyEngine
from .pressure_engine import PressureEngine
from .process_analyzer import ProcessAnalyzer, top_groups
from .providers import SystemProvider
from .working_set_manager import ActionResult, WorkingSetManager
from .scheduler import SchedulerAssistant
from .system_pressure import system_pressure
from .workload import detect_mode, resource_groups


class Engine:
    def __init__(self, provider: SystemProvider, settings: Settings | None = None,
                 log: EventLog | None = None, pagefile: PagefileManager | None = None,
                 persist: bool = False):
        self.provider = provider
        self.settings = settings or Settings()
        self.settings.update({})
        self._persist = persist
        d = data_dir() if persist else None
        self.log = log or EventLog(d, enabled=self.settings.logging)
        self.monitor = MemoryMonitor(provider)
        self.pressure = self._make_pressure()
        self.analyzer = ProcessAnalyzer(self.settings)
        self.policy = PolicyEngine(self.settings)
        self.executor = WorkingSetManager(provider, self.policy, self.log)
        self.scheduler = SchedulerAssistant(self.settings)
        self.pagefile = pagefile or PagefileManager(backup_dir=d)
        self.learner = UsageLearner(d / "learning.json" if d else None, self.settings.learning)
        self._lock = threading.RLock()
        self._last_scan = -1e18
        self._assess: list[ProcessAssessment] = []
        self._ai: list[llm_detector.AIProcess] = []
        self._ai_names: set[str] = set()
        self._last_level = Level.NORMAL
        self._last_snap: MemorySnapshot | None = None
        self._last_pressure: PressureState | None = None
        self._last_results: list[ActionResult] = []
        self._hf_base = 0.0
        self._hf_ts = -1e18
        self._advice: dict = {}
        self._pf_auto_ts = -1e18
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._procs = []
        self._foreground = (0, "")
        self._workload = "background"

    def _make_pressure(self) -> PressureEngine:
        s = self.settings
        return PressureEngine(s.threshold_yellow, s.threshold_orange, s.threshold_red, s.threshold_critical)

    # ------------------------------------------------------------ 틱
    def tick(self) -> dict:
        with self._lock:
            s = self.settings
            if (s.dry_run or not s.auto_optimization) and self.executor.originals:
                self.executor.restore_all()
            snap = self.provider.sample()
            ts = snap.timestamp
            fg_pid, fg_name = self.provider.foreground()
            self.analyzer.note_foreground(fg_name, ts)
            changed = (fg_pid, fg_name) != self._foreground
            if changed:
                self.log.add(ev.PRIORITY, "Foreground Changed", f"{self._foreground[1]} → {fg_name}", ts=ts)
                self._foreground = (fg_pid, fg_name)

            state = self.pressure.update(snap, self.learner.pressure_bias(ts))
            self.monitor.record(snap, state)
            self._log_level_change(state, snap)
            self._log_hard_fault(snap)

            interval = s.process_scan_interval_sec / (2 if state.level >= Level.HIGH else 1)
            if changed or ts - self._last_scan >= interval:
                self._last_scan = ts
                procs = self.provider.list_processes()
                self._procs = procs
                prev_ai = {a.pid for a in self._ai}
                self._ai = llm_detector.detect(procs, s.ai_custom_processes)
                self._ai_names = {a.name for a in self._ai}
                for a in self._ai:
                    if a.pid not in prev_ai:
                        size = f" (모델 {fmt_bytes(a.model_bytes)})" if a.model_bytes else ""
                        self.log.add(ev.LLM, f"LLM/AI 프로세스 감지\n{a.name} PID {a.pid}",
                                     f"WS {fmt_bytes(a.ws)}{size}", ts=ts)
                self.learner.observe(ts, snap.used_phys / snap.total_phys, snap.commit_total,
                                     [g["name"] for g in top_groups(self._assess, 3)])

            fullscreen = self.provider.foreground_fullscreen()
            mode = detect_mode(fg_name, self._procs, {a.pid for a in self._ai},
                               "ai" if s.ai_mode and s.workload_mode == "auto" else s.workload_mode,
                               fullscreen, snap.on_battery)
            if mode != self._workload:
                self.log.add(ev.PRIORITY, "작업 모드 변경", f"{self._workload} → {mode}", ts=ts)
                self._workload = mode
            self._assess = self.analyzer.analyze(self._procs, fg_name, ts, self._ai_names, mode)
            self._last_snap, self._last_pressure = snap, state

            ai_active = bool(self._ai) and s.ai_mode
            actions = self.policy.plan(state.level, snap, self._assess, state, ai_active,
                                       fullscreen)
            actions += self.scheduler.plan(state.level, snap, self._assess, fullscreen)
            self._last_results = self.executor.execute(actions, ts, s.dry_run)
            self.scheduler.feedback(self._last_results)
            self._handle_advice(actions, snap, ts)

            self._last_snap, self._last_pressure = snap, state
            return self.state()

    def _log_level_change(self, state: PressureState, snap: MemorySnapshot) -> None:
        if state.level != self._last_level:
            old, new = self._last_level, state.level
            names = ["GREEN", "YELLOW", "ORANGE", "RED", "RED(CRITICAL)"]
            lvl = "warn" if new >= Level.HIGH else "info"
            self.log.add(ev.PRESSURE, f"Memory Pressure\n{names[old]} → {names[new]}",
                         f"점수 {state.score:.0f}, Available {fmt_bytes(snap.avail_phys)}",
                         level=lvl, ts=snap.timestamp)
            if new == Level.CRITICAL:
                self.log.add(ev.WARNING, "메모리 부족 경고",
                             "시스템 응답성이 크게 저하될 수 있습니다. 메모리 사용이 큰 앱 정리를 권장합니다.",
                             level="error", ts=snap.timestamp)
            self._last_level = new

    def _log_hard_fault(self, snap: MemorySnapshot) -> None:
        hf = snap.hard_faults_per_sec
        base = self._hf_base or hf
        if hf > 1500 and hf > 3 * max(base, 100) and snap.timestamp - self._hf_ts > 60:
            self._hf_ts = snap.timestamp
            self.log.add(ev.HARD_FAULT, "Hard Fault 급증", f"{hf:.0f}/sec (평소 약 {base:.0f}/sec)",
                         level="warn", ts=snap.timestamp)
        self._hf_base = 0.9 * base + 0.1 * hf

    def _handle_advice(self, actions, snap: MemorySnapshot, ts: float) -> None:
        for a in actions:
            if a.kind == "pagefile_advice":
                rec = self.recommend_pagefile()
                self._advice["pagefile"] = rec
                self.log.add(ev.PAGEFILE, "Pagefile 점검", f"{a.reason}\n" + " / ".join(rec["rationale"][:2]),
                             level="warn", ts=ts)
            elif a.kind == "critical_advice":
                self._advice["critical"] = {"ts": ts, "candidates": a.extra["candidates"]}
                names = ", ".join(f"{c['name']}({fmt_bytes(c['private'])})" for c in a.extra["candidates"][:3])
                self.log.add(ev.WARNING, "메모리 부족 임박 — 종료 후보 추천", names, level="error", ts=ts)

    def _maybe_auto_pagefile(self, rec: dict, ts: float) -> None:
        return  # Pagefile은 명시적인 사용자 적용 명령으로만 변경한다.

    # ------------------------------------------------------------ 상태
    def state(self) -> dict:
        with self._lock:
            return self._state_unlocked()

    def _state_unlocked(self) -> dict:
        snap, pr = self._last_snap, self._last_pressure
        if snap is None or pr is None:
            return {"ready": False}
        return {
            "ready": True, "snapshot": snap.to_dict(), "pressure": pr.to_dict(),
            "top_processes": top_groups(self._assess, 8),
            "ai_processes": [a.__dict__ for a in self._ai],
            "dry_run": self.settings.dry_run, "ai_mode": self.settings.ai_mode,
            "admin": self.provider.is_admin(),
            "foreground": {"pid": self._foreground[0], "name": self._foreground[1]},
            "workload": self._workload, "workload_mode": self.settings.workload_mode,
            "system_pressure": system_pressure(snap, pr.score),
            "resource_groups": resource_groups(self._assess, self._workload),
            "processes": [a.to_dict() for a in self._assess],
            "optimization_paused": not self.settings.auto_optimization,
            "pending_restores": len(self.executor.originals) if (self.settings.dry_run or not self.settings.auto_optimization) else 0,
            "last_actions": [{"msg": r.message, "dry": r.dry_run, "ok": r.success, "kind": r.action.kind}
                             for r in self._last_results],
            "advice": self._advice,
        }

    # ------------------------------------------------------------ 기능
    def recommend_pagefile(self, mode: str | None = None, model_gb: float | None = None) -> dict:
        s = self.settings
        snap = self._last_snap or self.provider.sample()
        mode = mode or s.pagefile_mode
        size = (model_gb if model_gb is not None else s.ai_model_size_gb) * GB
        runtime = int(size * s.ai_overhead_factor)
        return self.pagefile.recommend(
            mode, snap.total_phys, snap.commit_total, self.learner.commit_p95(),
            self.learner.commit_peak, runtime, s.pagefile_custom_gb).to_dict()

    def check_llm(self, model_gb: float, extra_gb: float = 0.0) -> dict:
        snap = self._last_snap or self.provider.sample()
        reclaim = int(sum(a.info.ws for a in self._assess
                          if a.protected_reason is None and a.category.value in ("Background", "Idle")) * 0.5)
        f = llm_detector.assess_model(int(model_gb * GB), snap, self.settings.ai_overhead_factor,
                                      int(extra_gb * GB), reclaim)
        d = f.to_dict()
        d["text"] = llm_detector.describe(f, snap)
        if self.settings.pagefile_mode != "llm" and f.recommended_pagefile:
            d["notes"].append("Pagefile 모드를 'Local AI / LLM'으로 바꾸면 권장값이 자동 계산됩니다.")
        self.log.add(ev.LLM, "모델 실행 가능성 평가", f.message, ts=snap.timestamp,
                     level="warn" if f.verdict.value in ("pagefile_heavy", "fail") else "info")
        return d

    def optimize_now(self) -> list[ActionResult]:
        """현재 실제 압박에 맞는 정책만 적용. 벤치마크도 쿨다운을 무시하지 않는다."""
        with self._lock:
            snap = self._last_snap or self.provider.sample()
            procs = self.provider.list_processes()
            _, fg_name = self.provider.foreground()
            self._assess = self.analyzer.analyze(procs, fg_name, snap.timestamp, self._ai_names, self._workload)
            level = self._last_pressure.level if self._last_pressure else Level.NORMAL
            actions = self.policy.plan(level, snap, self._assess, self._last_pressure,
                                       False, self.provider.foreground_fullscreen())
            self._last_results = self.executor.execute(actions, snap.timestamp, self.settings.dry_run)
            return self._last_results

    def terminate_process(self, pid: int) -> tuple[bool, str]:
        """사용자가 UI에서 명시적으로 요청한 경우에만 호출. 보호 대상은 거부한다."""
        old = next((x for x in self._assess if x.info.pid == pid), None)
        _, fg = self.provider.foreground()
        fresh = self.analyzer.analyze(self.provider.list_processes(), fg, time.time(), self._ai_names, self._workload)
        a = next((x for x in fresh if x.info.pid == pid), None)
        if a is None:
            return False, "알 수 없는 프로세스입니다."
        if old is None or old.info.create_time != a.info.create_time:
            return False, "프로세스가 변경되었습니다. 목록을 새로 고쳐주세요."
        if a.protected_reason:
            return False, f"보호 대상이라 종료할 수 없습니다: {a.protected_reason}"
        if self.settings.dry_run:
            return True, f"[DRY RUN] {a.info.name} 종료 요청을 시뮬레이션했습니다."
        if not self.provider.validate_target(pid, a.info.create_time):
            return False, "실행 직전 보호 검사에서 종료를 거부했습니다."
        try:
            proc = psutil.Process(pid)
            if proc.create_time() != a.info.create_time:
                return False, "PID가 재사용되어 종료를 거부했습니다."
            proc.terminate()  # Windows에서는 강제 종료이므로 UI에서 데이터 손실을 알린다.
        except psutil.Error as e:
            return False, f"종료 실패: {e}"
        self.log.add(ev.WARNING, f"사용자 요청으로 종료\n{a.info.name} (PID {pid})", ts=time.time())
        return True, "종료 요청을 보냈습니다."

    def apply_settings(self, raw: dict) -> dict:
        with self._lock:
            before = self.settings.to_dict()
            self.settings.update(raw)
            s = self.settings
            if before != s.to_dict():
                self.executor.restore_all()
                self.policy._lowered.clear()
                if before["dry_run"] != s.dry_run:
                    self.policy._trim_last.clear()
                    self.policy._global_trim_ts = -1e18
                self.scheduler.lowered.clear()
                self._last_scan = -1e18
            self.pressure.set_thresholds(s.threshold_yellow, s.threshold_orange,
                                         s.threshold_red, s.threshold_critical)
            self.learner.enabled = s.learning
            self.log.enabled = s.logging
            if self._persist:
                s.save()
            return s.to_dict()

    # ------------------------------------------------------------ 루프
    def run_forever(self) -> None:
        next_sample = 0.0
        while not self._stop.is_set():
            try:
                now = time.monotonic()
                if now >= next_sample:
                    self.tick()
                    next_sample = now + self.settings.sample_interval_sec
                else:
                    self._restore_foreground()
            except Exception as e:   # 모니터링 루프는 어떤 경우에도 죽지 않는다
                self.log.add(ev.WARNING, "엔진 오류", repr(e), level="error")
            self._stop.wait(.25)

    def _restore_foreground(self) -> None:
        """샘플/전체 스캔 사이에도 250ms마다 포그라운드 앱의 기존 변경을 복구한다."""
        with self._lock:
            pid, name = self.provider.foreground()
            if not name:
                return
            for key in list(self.executor.originals):
                a = next((x for x in self._assess if x.info.pid == key[1]), None)
                if key[1] == pid or (a and a.info.name.lower() == name.lower()):
                    from .policy import PlannedAction
                    action = PlannedAction("restore_resource", key[1], name, extra={"resource": key[0]})
                    result = self.executor.execute([action], time.time(), False)
                    self.scheduler.feedback(result)

    def start_thread(self) -> threading.Thread:
        if self._thread and self._thread.is_alive():
            return self._thread
        t = threading.Thread(target=self.run_forever, name="ramflow-engine", daemon=True)
        self._thread = t
        t.start()
        return t

    def stop(self) -> None:
        self._stop.set()
        if self._thread and self._thread is not threading.current_thread():
            self._thread.join(timeout=5)
        with self._lock:
            self.executor.restore_all()
            self.learner.save()
            self.provider.close()
