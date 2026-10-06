"""Mock 시스템: 실제 메모리를 소모하지 않고 시나리오로 압박 상황을 재현한다."""
from __future__ import annotations

from dataclasses import replace

from .models import GB, MB, MemorySnapshot, ProcessInfo
from .providers import SystemProvider


def _p(pid, name, ws_mb, private_mb=None, cmd=None, session=1):
    return ProcessInfo(pid=pid, name=name, ws=int(ws_mb * MB),
                       private=int((private_mb if private_mb is not None else ws_mb * 0.9) * MB),
                       cmdline=cmd or [], session=session)


def _system_procs():
    return [_p(4, "system", 20, 0, session=0), _p(800, "svchost.exe", 120, session=0),
            _p(900, "explorer.exe", 250), _p(901, "dwm.exe", 180)]


def scenario(name: str) -> tuple[dict, list[ProcessInfo]]:
    """(MemorySnapshot 필드 dict, 프로세스 목록)"""
    base = dict(compression_active=True, standby=int(0.5 * GB), cached=int(0.8 * GB))
    if name == "8gb_90pct":
        ram, used = 8, 0.90
        snap = dict(total_phys=8 * GB, avail_phys=int(8 * GB * (1 - used)), commit_total=int(9 * GB),
                    commit_limit=int(14 * GB), pagefile_total=6 * GB, pagefile_used=int(2.5 * GB),
                    compressed=int(1.2 * GB), hard_faults_per_sec=400, **base)
        procs = _system_procs() + [_p(100, "chrome.exe", 1400), _p(101, "code.exe", 900),
                                   _p(102, "slack.exe", 450), _p(103, "notepad.exe", 40)]
    elif name == "16gb_chrome100":
        snap = dict(total_phys=16 * GB, avail_phys=int(1.4 * GB), commit_total=int(19 * GB),
                    commit_limit=int(24 * GB), pagefile_total=8 * GB, pagefile_used=int(4 * GB),
                    compressed=int(2.5 * GB), hard_faults_per_sec=900, **base)
        procs = _system_procs() + [_p(200 + i, "chrome.exe", 220) for i in range(40)] + \
            [_p(300, "code.exe", 1800), _p(301, "slack.exe", 500)]
    elif name == "llm_loading":
        snap = dict(total_phys=8 * GB, avail_phys=int(0.9 * GB), commit_total=int(13 * GB),
                    commit_limit=int(18 * GB), pagefile_total=10 * GB, pagefile_used=int(5 * GB),
                    compressed=int(1.0 * GB), hard_faults_per_sec=1800, **base)
        procs = _system_procs() + [_p(400, "ollama_llama_server.exe", 5700, 5700),
                                   _p(401, "ollama.exe", 60), _p(402, "chrome.exe", 1100),
                                   _p(403, "slack.exe", 450)]
    elif name == "pagefile_shortage":
        snap = dict(total_phys=16 * GB, avail_phys=int(3 * GB), commit_total=int(17.2 * GB),
                    commit_limit=int(18 * GB), pagefile_total=2 * GB, pagefile_used=int(1.9 * GB),
                    compressed=int(2 * GB), hard_faults_per_sec=300, **base)
        procs = _system_procs() + [_p(500, "chrome.exe", 1500), _p(501, "code.exe", 1200)]
    elif name == "hard_fault_spike":
        snap = dict(total_phys=16 * GB, avail_phys=int(2.0 * GB), commit_total=int(14 * GB),
                    commit_limit=int(24 * GB), pagefile_total=8 * GB, pagefile_used=int(5 * GB),
                    compressed=int(2.5 * GB), hard_faults_per_sec=4000, pages_output_per_sec=2500, **base)
        procs = _system_procs() + [_p(600, "chrome.exe", 2200), _p(601, "slack.exe", 600)]
    elif name == "avail_under_500mb":
        snap = dict(total_phys=8 * GB, avail_phys=int(0.4 * GB), commit_total=int(11 * GB),
                    commit_limit=int(16 * GB), pagefile_total=8 * GB, pagefile_used=int(5 * GB),
                    compressed=int(1.8 * GB), hard_faults_per_sec=1200, **base)
        procs = _system_procs() + [_p(700, "chrome.exe", 2100), _p(701, "code.exe", 1500),
                                   _p(702, "slack.exe", 700)]
    elif name == "commit_95":
        snap = dict(total_phys=16 * GB, avail_phys=int(4 * GB), commit_total=int(22.8 * GB),
                    commit_limit=int(24 * GB), pagefile_total=8 * GB, pagefile_used=int(6 * GB),
                    compressed=int(2 * GB), hard_faults_per_sec=200, **base)
        procs = _system_procs() + [_p(800, "chrome.exe", 1800), _p(801, "code.exe", 1200)]
    else:  # "normal"
        snap = dict(total_phys=16 * GB, avail_phys=int(9 * GB), commit_total=int(7 * GB),
                    commit_limit=int(24 * GB), pagefile_total=8 * GB, pagefile_used=int(0.3 * GB),
                    compressed=int(0.2 * GB), hard_faults_per_sec=20, **base)
        procs = _system_procs() + [_p(900, "chrome.exe", 800), _p(901, "code.exe", 600)]
    return snap, procs


SCENARIOS = ["normal", "8gb_90pct", "16gb_chrome100", "llm_loading", "pagefile_shortage",
             "hard_fault_spike", "avail_under_500mb", "commit_95"]


class MockProvider(SystemProvider):
    """시간은 sample() 호출마다 interval 초씩 진행한다."""

    def __init__(self, scenario_name: str = "normal", interval: float = 2.0,
                 foreground: tuple[int, str] = (903, "notepad.exe"), start: float = 1_000_000.0):
        self.interval = interval
        self.now = start
        self.fg = foreground
        self.fullscreen = False
        self.idle = 0.0
        self.trim_calls: list[int] = []
        self.priority_calls: list[tuple[int, int]] = []
        self.purge_calls = 0
        self.admin = True
        self.memory_priorities = {}
        self.cpu_priorities = {}
        self.power_states = {}
        self.cpu_calls = []
        self.power_calls = []
        self.set_scenario(scenario_name)

    def set_scenario(self, name: str) -> None:
        self._snap, self._procs = scenario(name)
        if name != "normal" and not any(p.pid == self.fg[0] for p in self._procs):
            self._procs.append(_p(self.fg[0], self.fg[1], 40))

    def advance(self, seconds: float) -> None:
        self.now += seconds

    def sample(self) -> MemorySnapshot:
        self.now += self.interval
        return MemorySnapshot(timestamp=self.now, **self._snap)

    def patch(self, **kw) -> None:
        self._snap.update(kw)

    def list_processes(self) -> list[ProcessInfo]:
        return [replace(p) for p in self._procs]

    def foreground(self):
        return self.fg

    def foreground_fullscreen(self) -> bool:
        return self.fullscreen

    def user_idle_seconds(self) -> float:
        return self.idle

    def trim_working_set(self, pid):
        self.trim_calls.append(pid)
        for p in self._procs:
            if p.pid == pid:
                before = p.ws
                p.ws = int(max(p.private * 0.4, p.ws * 0.45))
                return before, p.ws
        return None

    def set_memory_priority(self, pid, priority) -> bool:
        self.priority_calls.append((pid, priority))
        self.memory_priorities[pid] = priority
        return True

    def get_memory_priority(self, pid):
        return self.memory_priorities.get(pid, 5)

    def get_cpu_priority(self, pid):
        return self.cpu_priorities.get(pid, 0x20)

    def set_cpu_priority(self, pid, value):
        self.cpu_calls.append((pid, value))
        self.cpu_priorities[pid] = value
        return True

    def get_power_throttling(self, pid):
        return self.power_states.get(pid, (0, 0))

    def set_power_throttling(self, pid, value):
        self.power_calls.append((pid, value))
        self.power_states[pid] = value
        return True

    def purge_low_priority_standby(self) -> bool:
        self.purge_calls += 1
        return True

    def is_admin(self) -> bool:
        return self.admin
