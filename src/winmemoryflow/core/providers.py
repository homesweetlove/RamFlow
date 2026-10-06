"""시스템 접근 추상화. 엔진은 SystemProvider만 알고, 실제/Mock 구현을 교체할 수 있다."""
from __future__ import annotations

import os
import time
from abc import ABC, abstractmethod

import psutil

from . import win_api
from .models import MemorySnapshot, ProcessInfo


class SystemProvider(ABC):
    @abstractmethod
    def sample(self) -> MemorySnapshot: ...

    @abstractmethod
    def list_processes(self) -> list[ProcessInfo]: ...

    @abstractmethod
    def foreground(self) -> tuple[int, str]:
        """(pid, 소문자 프로세스 이름)"""

    @abstractmethod
    def foreground_fullscreen(self) -> bool: ...

    @abstractmethod
    def user_idle_seconds(self) -> float: ...

    @abstractmethod
    def trim_working_set(self, pid: int) -> tuple[int, int] | None:
        """(축소 전 WS, 축소 후 WS). 실패 시 None."""

    @abstractmethod
    def set_memory_priority(self, pid: int, priority: int) -> bool: ...

    def purge_low_priority_standby(self) -> bool:
        return False

    def is_admin(self) -> bool:
        return False

    def process_identity(self, pid: int) -> float | None:
        return next((p.create_time for p in self.list_processes() if p.pid == pid), None)

    def validate_target(self, pid: int, identity: float) -> bool:
        return self.process_identity(pid) == identity and self.foreground()[0] != pid

    def get_memory_priority(self, pid: int) -> int | None:
        return None

    def get_cpu_priority(self, pid: int) -> int | None:
        return None

    def set_cpu_priority(self, pid: int, value: int) -> bool:
        return False

    def get_power_throttling(self, pid: int) -> tuple[int, int] | None:
        return None

    def set_power_throttling(self, pid: int, value: tuple[int, int]) -> bool:
        return False

    def close(self) -> None:
        pass


# cmdline을 읽을 프로세스 (전체를 읽으면 비용이 큼)
_CMDLINE_NAMES = ("python", "pythonw", "node", "java", "ollama", "llama", "koboldcpp", "lmstudio", "lm studio")

_COUNTERS = {
    "standby_normal": r"\Memory\Standby Cache Normal Priority Bytes",
    "standby_reserve": r"\Memory\Standby Cache Reserve Bytes",
    "standby_core": r"\Memory\Standby Cache Core Bytes",
    "modified": r"\Memory\Modified Page List Bytes",
    "cache": r"\Memory\Cache Bytes",
    "page_faults": r"\Memory\Page Faults/sec",
    "pages_input": r"\Memory\Pages Input/sec",
    "pages_output": r"\Memory\Pages Output/sec",
    "pf_usage": r"\Paging File(_Total)\% Usage",
    "disk_busy": r"\PhysicalDisk(_Total)\% Disk Time",
}


class WindowsProvider(SystemProvider):
    def __init__(self) -> None:
        self._pdh = win_api.PdhSampler(_COUNTERS)
        self._mc_pid: int | None = None
        self._fg_name_cache: dict[int, str] = {}
        self._self_pid = os.getpid()
        self._activity: dict[int, tuple[float, float, float, int]] = {}
        psutil.cpu_percent()

    # ---- 시스템 메모리
    def sample(self) -> MemorySnapshot:
        total, avail = win_api.global_memory_status()
        pi = win_api.get_performance_info()
        c = self._pdh.collect()
        standby = sum(int(c.get(k) or 0) for k in ("standby_normal", "standby_reserve", "standby_core"))
        compressed, comp_active = self._compressed_bytes()
        pf_total = max(0, pi.commit_limit - pi.phys_total)  # 근사: CommitLimit ≈ RAM + Pagefile
        pf_used = int(pf_total * (c.get("pf_usage") or 0.0) / 100.0)
        battery = psutil.sensors_battery()
        return MemorySnapshot(
            timestamp=time.time(), total_phys=total, avail_phys=avail,
            cached=int(c.get("cache") or pi.system_cache), standby=standby,
            modified=int(c.get("modified") or 0), compressed=compressed,
            commit_total=pi.commit_total, commit_limit=pi.commit_limit,
            pagefile_total=pf_total, pagefile_used=pf_used,
            hard_faults_per_sec=float(c.get("pages_input") or 0.0),
            page_faults_per_sec=float(c.get("page_faults") or 0.0),
            pages_output_per_sec=float(c.get("pages_output") or 0.0),
            compression_active=comp_active, cpu_percent=psutil.cpu_percent(),
            disk_busy_percent=c.get("disk_busy"),
            on_battery=bool(battery and not battery.power_plugged),
            battery_percent=battery.percent if battery else None)

    def _compressed_bytes(self) -> tuple[int, bool]:
        """'Memory Compression' 시스템 프로세스의 Working Set = 압축 저장소 크기(근사)."""
        try:
            if self._mc_pid is None:
                for p in psutil.process_iter(["pid", "name"]):
                    if p.info["name"] == "Memory Compression":
                        self._mc_pid = p.info["pid"]
                        break
                if self._mc_pid is None:
                    self._mc_pid = -1
            if self._mc_pid > 0:
                return psutil.Process(self._mc_pid).memory_info().rss, True
        except (psutil.Error, OSError):
            self._mc_pid = -1
        return 0, self._mc_pid is not None and self._mc_pid > 0

    # ---- 프로세스
    def list_processes(self) -> list[ProcessInfo]:
        out: list[ProcessInfo] = []
        now = time.monotonic()
        alive = set()
        for p in psutil.process_iter(["pid", "name", "memory_info"]):
            try:
                mi = p.info["memory_info"]
                if mi is None:
                    continue
                name = (p.info["name"] or "").lower()
                pid = p.info["pid"]
                alive.add(pid)
                identity = p.create_time()
                cpu, io_rate, known = 0.0, 0.0, False
                try:
                    ct = p.cpu_times()
                    it = p.io_counters()
                    cpu_time, io_total = ct.user + ct.system, it.read_bytes + it.write_bytes
                    prev = self._activity.get(pid)
                    if prev and prev[0] == identity:
                        dt = max(.001, now - prev[1])
                        cpu = max(0, (cpu_time - prev[2]) / dt * 100)
                        io_rate = max(0, (io_total - prev[3]) / dt)
                        known = True
                    self._activity[pid] = (identity, now, cpu_time, io_total)
                except (psutil.Error, OSError):
                    pass
                cmd: list[str] = []
                if name.startswith(_CMDLINE_NAMES):
                    try:
                        cmd = p.cmdline()
                    except (psutil.Error, OSError):
                        pass
                out.append(ProcessInfo(pid=pid, name=name, ws=int(mi.rss),
                                       private=int(getattr(mi, "private", mi.vms)),
                                       cmdline=cmd, session=win_api.process_session_id(pid),
                                       create_time=identity, cpu_percent=cpu, io_bytes_per_sec=io_rate,
                                       activity_known=known, exe=p.exe()))
            except (psutil.Error, OSError):
                continue
        self._activity = {k: v for k, v in self._activity.items() if k in alive}
        return out

    def foreground(self) -> tuple[int, str]:
        pid = win_api.foreground_pid()
        if not pid:
            return 0, ""
        try:
            name = psutil.Process(pid).name().lower()
        except (psutil.Error, OSError):
            name = ""
        return pid, name

    def foreground_fullscreen(self) -> bool:
        return win_api.foreground_is_fullscreen()

    def user_idle_seconds(self) -> float:
        return win_api.user_idle_seconds()

    def trim_working_set(self, pid: int) -> tuple[int, int] | None:
        try:
            before = psutil.Process(pid).memory_info().rss
            if not win_api.empty_working_set(pid):
                return None
            after = psutil.Process(pid).memory_info().rss
            return before, after
        except (psutil.Error, OSError):
            return None

    def set_memory_priority(self, pid: int, priority: int) -> bool:
        return win_api.set_memory_priority(pid, priority)

    def purge_low_priority_standby(self) -> bool:
        return False

    def is_admin(self) -> bool:
        return win_api.is_admin()

    def process_identity(self, pid: int) -> float | None:
        try:
            return psutil.Process(pid).create_time()
        except psutil.Error:
            return None

    def validate_target(self, pid: int, identity: float) -> bool:
        from .process_analyzer import SYSTEM_NAMES
        try:
            p = psutil.Process(pid)
            fg_pid, fg_name = self.foreground()
            name = p.name().lower()
            session = win_api.process_session_id(pid)
            exe = os.path.normcase(p.exe())
            windows = os.path.normcase(os.environ.get("SystemRoot", r"C:\Windows")) + os.sep
            return (pid > 4 and pid != self._self_pid and pid != fg_pid and name != fg_name
                    and name not in SYSTEM_NAMES and not exe.startswith(windows)
                    and p.create_time() == identity and session > 0
                    and session == win_api.process_session_id(self._self_pid))
        except (psutil.Error, OSError):
            return False

    def get_memory_priority(self, pid: int) -> int | None:
        return win_api.get_memory_priority(pid)

    def get_cpu_priority(self, pid: int) -> int | None:
        return win_api.get_cpu_priority(pid)

    def set_cpu_priority(self, pid: int, value: int) -> bool:
        return win_api.set_cpu_priority(pid, value)

    def get_power_throttling(self, pid: int) -> tuple[int, int] | None:
        return win_api.get_power_throttling(pid)

    def set_power_throttling(self, pid: int, value: tuple[int, int]) -> bool:
        return win_api.set_power_throttling(pid, value)

    def close(self) -> None:
        self._pdh.close()
