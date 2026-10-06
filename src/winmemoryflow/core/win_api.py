"""Windows API ctypes 래퍼.

문서화된 사용자 모드 API만 사용한다. 64비트 핸들을 보존하도록 함수 시그니처를 지정한다.
"""
from __future__ import annotations

import ctypes
import sys
from ctypes import wintypes as wt
from dataclasses import dataclass

IS_WINDOWS = sys.platform == "win32"

if IS_WINDOWS:
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    pdh = ctypes.WinDLL("pdh")
    advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
    shell32 = ctypes.WinDLL("shell32")

    kernel32.OpenProcess.restype = wt.HANDLE
    kernel32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
    kernel32.CloseHandle.argtypes = [wt.HANDLE]
    kernel32.K32EmptyWorkingSet.argtypes = [wt.HANDLE]
    kernel32.GetCurrentProcess.restype = wt.HANDLE
    kernel32.SetProcessInformation.argtypes = [wt.HANDLE, ctypes.c_int, wt.LPVOID, wt.DWORD]
    kernel32.GetProcessInformation.argtypes = [wt.HANDLE, ctypes.c_int, wt.LPVOID, wt.DWORD]
    kernel32.SetPriorityClass.argtypes = [wt.HANDLE, wt.DWORD]
    kernel32.GetPriorityClass.argtypes = [wt.HANDLE]
    kernel32.GetPriorityClass.restype = wt.DWORD
    kernel32.GlobalMemoryStatusEx.argtypes = [wt.LPVOID]
    psapi.GetPerformanceInfo.argtypes = [wt.LPVOID, wt.DWORD]
    pdh.PdhOpenQueryW.argtypes = [wt.LPCWSTR, ctypes.c_size_t, ctypes.POINTER(wt.HANDLE)]
    pdh.PdhAddEnglishCounterW.argtypes = [wt.HANDLE, wt.LPCWSTR, ctypes.c_size_t, ctypes.POINTER(wt.HANDLE)]
    pdh.PdhCollectQueryData.argtypes = [wt.HANDLE]
    pdh.PdhGetFormattedCounterValue.argtypes = [wt.HANDLE, wt.DWORD, wt.LPVOID, wt.LPVOID]
    pdh.PdhCloseQuery.argtypes = [wt.HANDLE]
    user32.GetForegroundWindow.restype = wt.HWND
    user32.GetShellWindow.restype = wt.HWND
    user32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
    user32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
    user32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
PROCESS_SET_QUOTA = 0x0100
PROCESS_SET_INFORMATION = 0x0200
PROCESS_QUERY_INFORMATION = 0x0400


class MEMORYSTATUSEX(ctypes.Structure):
    _fields_ = [("dwLength", wt.DWORD), ("dwMemoryLoad", wt.DWORD),
                ("ullTotalPhys", ctypes.c_uint64), ("ullAvailPhys", ctypes.c_uint64),
                ("ullTotalPageFile", ctypes.c_uint64), ("ullAvailPageFile", ctypes.c_uint64),
                ("ullTotalVirtual", ctypes.c_uint64), ("ullAvailVirtual", ctypes.c_uint64),
                ("ullAvailExtendedVirtual", ctypes.c_uint64)]


class PERFORMANCE_INFORMATION(ctypes.Structure):
    _fields_ = [("cb", wt.DWORD), ("CommitTotal", ctypes.c_size_t), ("CommitLimit", ctypes.c_size_t),
                ("CommitPeak", ctypes.c_size_t), ("PhysicalTotal", ctypes.c_size_t),
                ("PhysicalAvailable", ctypes.c_size_t), ("SystemCache", ctypes.c_size_t),
                ("KernelTotal", ctypes.c_size_t), ("KernelPaged", ctypes.c_size_t),
                ("KernelNonpaged", ctypes.c_size_t), ("PageSize", ctypes.c_size_t),
                ("HandleCount", wt.DWORD), ("ProcessCount", wt.DWORD), ("ThreadCount", wt.DWORD)]


@dataclass
class PerfInfo:
    commit_total: int
    commit_limit: int
    commit_peak: int
    phys_total: int
    phys_avail: int
    system_cache: int


def global_memory_status() -> tuple[int, int]:
    """(총 물리 RAM, 가용 RAM)"""
    ms = MEMORYSTATUSEX()
    ms.dwLength = ctypes.sizeof(ms)
    if not kernel32.GlobalMemoryStatusEx(ctypes.byref(ms)):
        raise ctypes.WinError(ctypes.get_last_error())
    return ms.ullTotalPhys, ms.ullAvailPhys


def get_performance_info() -> PerfInfo:
    pi = PERFORMANCE_INFORMATION()
    pi.cb = ctypes.sizeof(pi)
    if not psapi.GetPerformanceInfo(ctypes.byref(pi), pi.cb):
        raise ctypes.WinError(ctypes.get_last_error())
    ps = pi.PageSize
    return PerfInfo(pi.CommitTotal * ps, pi.CommitLimit * ps, pi.CommitPeak * ps,
                    pi.PhysicalTotal * ps, pi.PhysicalAvailable * ps, pi.SystemCache * ps)


# ---------------------------------------------------------------- PDH
class _PDH_FMT_COUNTERVALUE(ctypes.Structure):
    _fields_ = [("CStatus", wt.DWORD), ("doubleValue", ctypes.c_double)]


class PdhSampler:
    """영어 카운터 경로 기반 PDH 수집기 (로케일 무관)."""

    def __init__(self, counters: dict[str, str]):
        self._query = wt.HANDLE()
        self._handles: dict[str, wt.HANDLE] = {}
        self.ok = False
        if not IS_WINDOWS:
            return
        if pdh.PdhOpenQueryW(None, 0, ctypes.byref(self._query)) != 0:
            return
        for name, path in counters.items():
            h = wt.HANDLE()
            if pdh.PdhAddEnglishCounterW(self._query, path, 0, ctypes.byref(h)) == 0:
                self._handles[name] = h
        self.ok = bool(self._handles)
        if self.ok:
            pdh.PdhCollectQueryData(self._query)  # 속도 카운터는 두 번째 수집부터 유효

    def collect(self) -> dict[str, float | None]:
        out: dict[str, float | None] = {k: None for k in self._handles}
        if not self.ok or pdh.PdhCollectQueryData(self._query) != 0:
            return out
        for name, h in self._handles.items():
            val = _PDH_FMT_COUNTERVALUE()
            if pdh.PdhGetFormattedCounterValue(h, 0x200, None, ctypes.byref(val)) == 0 and val.CStatus == 0:
                out[name] = val.doubleValue
        return out

    def close(self) -> None:
        if self.ok:
            pdh.PdhCloseQuery(self._query)
            self.ok = False


# ---------------------------------------------------------------- 프로세스 조작
def _open(pid: int, access: int):
    h = kernel32.OpenProcess(access, False, pid)
    return h or None


def empty_working_set(pid: int) -> bool:
    """대상 프로세스 Working Set 축소 (페이지는 Standby로 이동, 데이터 손실 없음)."""
    h = _open(pid, PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION)
    if not h:
        return False
    try:
        return bool(kernel32.K32EmptyWorkingSet(h))
    finally:
        kernel32.CloseHandle(h)


class _MEMORY_PRIORITY_INFORMATION(ctypes.Structure):
    _fields_ = [("MemoryPriority", wt.ULONG)]


def set_memory_priority(pid: int, priority: int) -> bool:
    """Memory Priority 1(매우 낮음)~5(보통). Windows 8+."""
    h = _open(pid, PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION)
    if not h:
        return False
    try:
        info = _MEMORY_PRIORITY_INFORMATION(priority)
        return bool(kernel32.SetProcessInformation(h, 0, ctypes.byref(info), ctypes.sizeof(info)))
    finally:
        kernel32.CloseHandle(h)


def get_memory_priority(pid: int) -> int | None:
    h = _open(pid, PROCESS_QUERY_INFORMATION) or _open(pid, PROCESS_QUERY_LIMITED_INFORMATION)
    if not h:
        return None
    try:
        info = _MEMORY_PRIORITY_INFORMATION()
        if kernel32.GetProcessInformation(h, 0, ctypes.byref(info), ctypes.sizeof(info)):
            return int(info.MemoryPriority)
        return None
    finally:
        kernel32.CloseHandle(h)


def process_session_id(pid: int) -> int:
    sid = wt.DWORD()
    if kernel32.ProcessIdToSessionId(pid, ctypes.byref(sid)):
        return int(sid.value)
    return -1


# ---------------------------------------------------------------- 사용자 활동
def foreground_pid() -> int:
    hwnd = user32.GetForegroundWindow()
    if not hwnd:
        return 0
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return int(pid.value)


def foreground_is_fullscreen() -> bool:
    """전체화면 앱(게임/영상) 판단. 바탕화면 셸은 제외."""
    hwnd = user32.GetForegroundWindow()
    if not hwnd or hwnd == user32.GetShellWindow():
        return False
    buf = ctypes.create_unicode_buffer(64)
    user32.GetClassNameW(hwnd, buf, 64)
    if buf.value in ("Progman", "WorkerW", "Shell_TrayWnd"):
        return False
    r = wt.RECT()
    if not user32.GetWindowRect(hwnd, ctypes.byref(r)):
        return False
    return (r.left <= 0 and r.top <= 0 and
            r.right >= user32.GetSystemMetrics(0) and r.bottom >= user32.GetSystemMetrics(1))


class _LASTINPUTINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.UINT), ("dwTime", wt.DWORD)]


def user_idle_seconds() -> float:
    """마지막 키보드/마우스 입력 이후 경과 시간."""
    li = _LASTINPUTINFO()
    li.cbSize = ctypes.sizeof(li)
    if not user32.GetLastInputInfo(ctypes.byref(li)):
        return 0.0
    return ((kernel32.GetTickCount() - li.dwTime) & 0xFFFFFFFF) / 1000.0


def is_admin() -> bool:
    try:
        return bool(shell32.IsUserAnAdmin())
    except Exception:
        return False


BELOW_NORMAL_PRIORITY_CLASS = 0x4000
NORMAL_PRIORITY_CLASS = 0x20


class POWER_THROTTLING_STATE(ctypes.Structure):
    _fields_ = [("Version", wt.ULONG), ("ControlMask", wt.ULONG), ("StateMask", wt.ULONG)]


def get_cpu_priority(pid: int) -> int | None:
    h = _open(pid, PROCESS_QUERY_LIMITED_INFORMATION)
    if not h:
        return None
    try:
        return int(kernel32.GetPriorityClass(h)) or None
    finally:
        kernel32.CloseHandle(h)


def set_cpu_priority(pid: int, priority: int) -> bool:
    # 이전 값을 복원할 수 있지만 Realtime 값은 어떤 경로에서도 허용하지 않는다.
    if priority not in (0x40, 0x4000, 0x20, 0x8000, 0x80):
        return False
    h = _open(pid, PROCESS_SET_INFORMATION)
    if not h:
        return False
    try:
        return bool(kernel32.SetPriorityClass(h, priority))
    finally:
        kernel32.CloseHandle(h)


def get_power_throttling(pid: int) -> tuple[int, int] | None:
    h = _open(pid, PROCESS_QUERY_LIMITED_INFORMATION)
    if not h:
        return None
    try:
        state = POWER_THROTTLING_STATE(1, 0, 0)
        if kernel32.GetProcessInformation(h, 4, ctypes.byref(state), ctypes.sizeof(state)):
            return int(state.ControlMask), int(state.StateMask)
        return None
    finally:
        kernel32.CloseHandle(h)


def set_power_throttling(pid: int, value: tuple[int, int]) -> bool:
    h = _open(pid, PROCESS_SET_INFORMATION)
    if not h:
        return False
    try:
        state = POWER_THROTTLING_STATE(1, *value)
        return bool(kernel32.SetProcessInformation(h, 4, ctypes.byref(state), ctypes.sizeof(state)))
    finally:
        kernel32.CloseHandle(h)
