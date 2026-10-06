"""Pagefile 분석·권장·(선택) 적용.

⚠ Windows는 pagefile 크기를 런타임에 즉시 늘릴 수 없고 변경은 보통 재부팅이 필요하다.
   그래서 "macOS처럼 자동 확장"이 아니라 '사용 패턴 기반 권장 → 사용자 승인 후 적용'을 제공한다.
"""
from __future__ import annotations

import json
import subprocess
import sys
import threading
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

import psutil

from .models import GB, fmt_bytes

MODES = ["windows_managed", "balanced", "memory_saving", "heavy", "llm", "custom"]
MODE_LABELS = {"windows_managed": "Windows Managed", "balanced": "Balanced", "memory_saving": "Memory Saving",
               "heavy": "Heavy Workload", "llm": "Local AI / LLM", "custom": "Custom"}

# 모드별 (수요 대비 여유 배수, 최소 pagefile GB, 최대 pagefile GB)
_MODE_PARAMS = {"balanced": (1.30, 2, 24), "memory_saving": (1.15, 1, 8), "heavy": (1.50, 4, 48)}

_DISK_PS = r"""
$r = foreach ($p in Get-Partition | Where-Object DriveLetter) {
  $pd = Get-PhysicalDisk | Where-Object { $_.DeviceId -eq "$($p.DiskNumber)" } | Select-Object -First 1
  [pscustomobject]@{Drive="$($p.DriveLetter):"; Media="$($pd.MediaType)"; Bus="$($pd.BusType)"}
}
$r | ConvertTo-Json -Compress
"""


@dataclass
class DiskInfo:
    drive: str
    kind: str        # NVMe | SATA SSD | HDD | Unknown
    free: int
    total: int

    @property
    def rank(self) -> int:
        return {"NVMe": 0, "SATA SSD": 1, "Unknown": 2, "HDD": 3}.get(self.kind, 2)


@dataclass
class Recommendation:
    mode: str
    drive: str | None
    disk_kind: str
    initial_mb: int
    max_mb: int
    needed_commit_limit: int
    rationale: list[str] = field(default_factory=list)
    managed_by_windows: bool = False

    def to_dict(self) -> dict:
        return dict(self.__dict__)


def classify_disk(media: str, bus: str) -> str:
    media, bus = (media or "").lower(), (bus or "").lower()
    if "nvme" in bus:
        return "NVMe"
    if media == "ssd":
        return "SATA SSD"
    if media == "hdd":
        return "HDD"
    return "Unknown"


def detect_disks(runner: Callable[[list[str]], tuple[int, str]] | None = None) -> list[DiskInfo]:
    """고정 디스크 목록과 종류. PowerShell(Storage 모듈)로 판별, 실패하면 Unknown."""
    kinds: dict[str, str] = {}
    if sys.platform == "win32":
        run = runner or _run
        try:
            rc, out = run(["powershell", "-NoProfile", "-Command", _DISK_PS])
            if rc == 0 and out.strip():
                data = json.loads(out)
                for d in data if isinstance(data, list) else [data]:
                    kinds[d["Drive"].upper()] = classify_disk(d.get("Media"), d.get("Bus"))
        except Exception:
            pass
    disks = []
    for part in psutil.disk_partitions(all=False):
        if "fixed" not in part.opts and sys.platform == "win32":
            continue
        try:
            u = psutil.disk_usage(part.mountpoint)
        except OSError:
            continue
        drive = part.mountpoint.rstrip("\\").upper()
        disks.append(DiskInfo(drive, kinds.get(drive, "Unknown"), u.free, u.total))
    return disks


def _run(cmd: list[str]) -> tuple[int, str]:
    cp = subprocess.run(cmd, capture_output=True, text=True, timeout=60,
                        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    return cp.returncode, (cp.stdout or "") + (cp.stderr if cp.returncode else "")


def read_config() -> dict:
    """현재 pagefile 설정 (레지스트리 PagingFiles)."""
    if sys.platform != "win32":
        return {"entries": [], "system_managed": True}
    import winreg
    key = r"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, key) as k:
            val, _ = winreg.QueryValueEx(k, "PagingFiles")
    except OSError:
        return {"entries": [], "system_managed": True}
    entries, managed = [], False
    for line in val if isinstance(val, list) else [val]:
        line = (line or "").strip()
        if not line:
            continue
        if line.startswith("?:"):
            managed = True
            continue
        parts = line.rsplit(" ", 2)
        try:
            path, init, mx = parts[0], int(parts[1]), int(parts[2])
        except (IndexError, ValueError):
            path, init, mx = line, 0, 0
        entries.append({"path": path, "initial_mb": init, "max_mb": mx})
        if init == 0 and mx == 0:
            managed = True
    return {"entries": entries, "system_managed": managed}


class PagefileManager:
    def __init__(self, backup_dir: Path | None = None,
                 runner: Callable[[list[str]], tuple[int, str]] | None = None):
        self.backup_dir = backup_dir
        self._runner = runner or _run
        self.disks: list[DiskInfo] = []
        self._disk_thread: threading.Thread | None = None

    def refresh_disks_async(self) -> None:
        if self._disk_thread and self._disk_thread.is_alive():
            return
        self._disk_thread = threading.Thread(
            target=lambda: setattr(self, "disks", detect_disks(self._runner)), daemon=True)
        self._disk_thread.start()

    # ---- 권장값 계산
    def recommend(self, mode: str, total_phys: int, current_commit: int, commit_p95: int = 0,
                  commit_peak: int = 0, llm_runtime: int = 0, custom_gb: float = 0.0,
                  disks: list[DiskInfo] | None = None) -> Recommendation:
        disks = self.disks if disks is None else disks
        if mode == "windows_managed":
            return Recommendation(mode, None, "", 0, 0, 0,
                                  ["Windows 자동 관리를 유지합니다. 변경이 필요 없습니다."], True)
        ram_gb = total_phys / GB
        demand = max(commit_p95, current_commit)
        peak = max(commit_peak, demand)
        why = [f"관측된 Commit 수요(p95/현재): {fmt_bytes(demand)}, 최대 {fmt_bytes(peak)}"]

        if mode == "custom":
            pf = max(1.0, custom_gb)
            lo = hi = pf
            max_gb = pf
            why.append(f"사용자 지정 {pf:.0f}GB")
        else:
            if mode == "llm":
                head, min_pf, max_pf = 1.40, (16 if ram_gb <= 16 else 8), 48
            else:
                head, min_pf, max_pf = _MODE_PARAMS.get(mode, _MODE_PARAMS["balanced"])
            target = max(demand * head, peak * 1.05)
            if mode == "llm" and llm_runtime:
                target = max(target, current_commit + llm_runtime * 1.15)
                why.append(f"모델 예상 실행 메모리 {fmt_bytes(llm_runtime)} 반영")
            raw = (target - total_phys) / GB
            pf = min(max(raw, min_pf), max_pf)
            why.append(f"필요 Commit Limit ≈ {fmt_bytes(target)} → Pagefile {raw:.1f}GB 계산, "
                       f"모드 범위 {min_pf}~{max_pf}GB로 보정")
            max_gb = min(max_pf, pf * 1.25)
        init_mb = int(-(-pf // 1) * 1024)
        max_mb = int(-(-max_gb // 1) * 1024)
        needed = total_phys + init_mb * 1024 * 1024

        drive, kind = None, "Unknown"
        usable = [d for d in disks if d.free >= max_mb * 1024 * 1024 + 10 * GB]
        if usable:
            best = sorted(usable, key=lambda d: (d.rank, d.drive != "C:", -d.free))[0]
            drive, kind = best.drive, best.kind
            why.append(f"배치 드라이브: {drive} ({kind}) — SSD/NVMe 우선")
            if kind == "HDD":
                why.append("⚠ SSD가 없어 HDD에 배치됩니다. Pagefile 의존 시 체감 성능 저하가 큽니다.")
        elif disks:
            why.append("⚠ 여유 공간이 충분한 드라이브가 없습니다. 디스크 공간을 확보하세요.")
        return Recommendation(mode, drive, kind, init_mb, max_mb, needed, why)

    # ---- 적용 (관리자, 재부팅 필요) — 기본 OFF, 사용자가 명시적으로 요청했을 때만
    def apply(self, rec: Recommendation, dry_run: bool = True) -> tuple[bool, str]:
        if sys.platform != "win32":
            return False, "Windows에서만 적용할 수 있습니다."
        if rec.managed_by_windows:
            script = self._managed_script()
            what = "Windows 자동 관리로 복원"
        elif not rec.drive:
            return False, "배치할 드라이브를 찾지 못했습니다."
        else:
            script = self._set_script(rec)
            what = f"{rec.drive}\\pagefile.sys 초기 {rec.initial_mb}MB / 최대 {rec.max_mb}MB"
        if dry_run:
            return True, f"[DRY RUN] 다음을 적용했을 것입니다(재부팅 필요): {what}"
        from .win_api import is_admin
        if not is_admin():
            return False, "관리자 권한이 필요합니다."
        if not self._backup():
            return False, "기존 설정을 안전하게 백업할 수 없어 변경을 취소했습니다."
        rc, out = self._runner(["powershell", "-NoProfile", "-Command", "$ErrorActionPreference='Stop'; " + script])
        if rc != 0:
            return False, f"적용 실패: {out.strip()[:300]}"
        return True, f"적용 완료: {what}. 변경은 재부팅 후 적용됩니다."

    def _backup(self) -> bool:
        if not self.backup_dir:
            return False
        path = self.backup_dir / "pagefile_backup.json"
        if path.exists():
            return True  # 최초 변경 전 설정을 덮어쓰지 않는다.
        try:
            rc, out = self._runner(["powershell", "-NoProfile", "-Command",
                                    "(Get-CimInstance Win32_ComputerSystem).AutomaticManagedPagefile | ConvertTo-Json"])
            automatic = json.loads(out.strip()) if rc == 0 else None
            if not isinstance(automatic, bool):
                return False
            original = read_config()
            original["automatic_managed"] = automatic
            self._configuration_script(original)
            self.backup_dir.mkdir(parents=True, exist_ok=True)
            with path.open("x", encoding="utf-8") as f:
                json.dump(original, f, ensure_ascii=False, indent=2)
            return True
        except (OSError, ValueError, TypeError):
            return False

    def restore(self, dry_run: bool = True) -> tuple[bool, str]:
        path = self.backup_dir / "pagefile_backup.json" if self.backup_dir else None
        if path is None or not path.exists():
            return False, "RamFlow가 변경한 Pagefile 백업이 없습니다."
        try:
            original = json.loads(path.read_text(encoding="utf-8"))
            script = self._configuration_script(original)
        except (OSError, ValueError, KeyError, TypeError):
            return False, "백업 형식이 유효하지 않아 복원을 중단했습니다."
        if dry_run:
            return True, "[DRY RUN] 최초 변경 전 Pagefile 설정을 복원합니다."
        from .win_api import is_admin
        if not is_admin():
            return False, "관리자 권한이 필요합니다."
        rc, out = self._runner(["powershell", "-NoProfile", "-Command", "$ErrorActionPreference='Stop'; " + script])
        if rc:
            return False, f"복원 실패: {out.strip()[:300]}"
        path.unlink()
        return True, "최초 변경 전 Pagefile 설정 복원 완료. 재부팅 후 적용됩니다."

    @staticmethod
    def _configuration_script(config: dict) -> str:
        automatic = config["automatic_managed"]
        if not isinstance(automatic, bool) or not isinstance(config["entries"], list):
            raise ValueError("백업 형식 오류")
        script = ("$cs = Get-CimInstance Win32_ComputerSystem; "
                  "Set-CimInstance -InputObject $cs -Property @{AutomaticManagedPagefile=$false}; "
                  "Get-CimInstance Win32_PageFileSetting | Remove-CimInstance; ")
        for entry in config["entries"]:
            path, initial, maximum = entry["path"], entry["initial_mb"], entry["max_mb"]
            if not isinstance(path, str) or not re.fullmatch(r"[A-Za-z]:\\[A-Za-z0-9_. \\-]+", path):
                raise ValueError("Pagefile 경로 오류")
            if (type(initial) is not int or type(maximum) is not int
                    or not 0 <= initial <= maximum <= 4194304):
                raise ValueError("Pagefile 크기 오류")
            script += (f"New-CimInstance -ClassName Win32_PageFileSetting -Property @{{Name='{path}'; "
                       f"InitialSize={initial}; MaximumSize={maximum}}} | Out-Null; ")
        if automatic:
            script += "Set-CimInstance -InputObject $cs -Property @{AutomaticManagedPagefile=$true}; "
        return script

    @staticmethod
    def _managed_script() -> str:
        return ("$cs = Get-CimInstance Win32_ComputerSystem; "
                "Set-CimInstance -InputObject $cs -Property @{AutomaticManagedPagefile=$true}")

    @staticmethod
    def _set_script(rec: Recommendation) -> str:
        if (not isinstance(rec.drive, str) or not re.fullmatch(r"[A-Za-z]:", rec.drive)
                or type(rec.initial_mb) is not int or type(rec.max_mb) is not int
                or not 1 <= rec.initial_mb <= rec.max_mb <= 4194304):
            raise ValueError("Pagefile 적용 값 오류")
        path = f"{rec.drive}\\pagefile.sys"
        return (
            "$cs = Get-CimInstance Win32_ComputerSystem; "
            "Set-CimInstance -InputObject $cs -Property @{AutomaticManagedPagefile=$false}; "
            "Get-CimInstance Win32_PageFileSetting | Remove-CimInstance; "
            f"New-CimInstance -ClassName Win32_PageFileSetting -Property @{{Name='{path}'; "
            f"InitialSize={rec.initial_mb}; MaximumSize={rec.max_mb}}} | Out-Null")
