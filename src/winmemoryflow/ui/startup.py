"""시작 프로그램 등록 (HKCU Run, 관리자 권한 불필요)."""
from __future__ import annotations

import sys

_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
_NAME = "RamFlow"


def _command() -> str:
    if getattr(sys, "frozen", False):
        return f'"{sys.executable}" --tray'
    exe = sys.executable
    if exe.lower().endswith("python.exe"):
        exe = exe[:-10] + "pythonw.exe"      # 콘솔 창 없이 실행
    return f'"{exe}" -m winmemoryflow ui --tray'


def set_startup(enabled: bool) -> bool:
    if sys.platform != "win32":
        return False
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, _KEY, 0, winreg.KEY_SET_VALUE) as k:
            if enabled:
                winreg.SetValueEx(k, _NAME, 0, winreg.REG_SZ, _command())
            else:
                try:
                    winreg.DeleteValue(k, _NAME)
                except FileNotFoundError:
                    pass
        return True
    except OSError:
        return False
