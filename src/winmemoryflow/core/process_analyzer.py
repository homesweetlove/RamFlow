"""프로세스 분석: 활동 분류(Foreground/Normal/Background/Idle)와 보호 대상 판정."""
from __future__ import annotations

import os
from collections import defaultdict

from ..config.settings import Settings
from .models import MB, Category, ProcessAssessment, ProcessInfo
from .workload import ALWAYS_ACTIVE, GROUPS, base

# Windows 핵심/시스템/드라이버 보조 프로세스 – 절대 조작하지 않는다.
SYSTEM_NAMES = {
    "system", "system idle process", "registry", "smss.exe", "csrss.exe", "wininit.exe",
    "services.exe", "lsass.exe", "winlogon.exe", "svchost.exe", "dwm.exe", "fontdrvhost.exe",
    "explorer.exe", "sihost.exe", "ctfmon.exe", "taskhostw.exe", "audiodg.exe", "memory compression",
    "memcompression", "searchhost.exe", "searchindexer.exe", "startmenuexperiencehost.exe",
    "shellexperiencehost.exe", "runtimebroker.exe", "textinputhost.exe", "lockapp.exe",
    "securityhealthservice.exe", "securityhealthsystray.exe", "msmpeng.exe", "nissrv.exe",
    "wudfhost.exe", "dllhost.exe", "conhost.exe", "spoolsv.exe", "vmmem", "vmmemwsl",
    "vmwp.exe", "vmcompute.exe", "nvcontainer.exe", "nvdisplay.container.exe",
    "rtkauduservice64.exe", "igfxem.exe", "amdrsserv.exe", "cncmd.exe",
    "avp.exe", "avastsvc.exe", "avgsvc.exe", "ekrn.exe", "bdagent.exe", "mbamservice.exe",
    "sense.exe", "mssense.exe", "crowdstrike.exe", "csfalconservice.exe", "vgc.exe", "beservice.exe",
    "easyanticheat.exe", "easyanticheat_eos.exe",
}
# 메모리 매핑 파일/자체 캐시를 대량 사용하는 서버·DB 계열
MMAP_HEAVY_NAMES = {"sqlservr.exe", "mysqld.exe", "postgres.exe", "mongod.exe", "redis-server.exe",
                    "elasticsearch.exe", "clickhouse.exe", "everything.exe"}


def _norm(name: str) -> str:
    return name.lower().strip()


def _base(name: str) -> str:
    n = _norm(name)
    return n[:-4] if n.endswith(".exe") else n


class ProcessAnalyzer:
    def __init__(self, settings: Settings):
        self.settings = settings
        self._last_active: dict[str, float] = {}   # 이름 → 마지막 포그라운드 시각
        self._self_pid = os.getpid()
        self._seen: set[tuple[int, float]] = set()

    def note_foreground(self, name: str, ts: float) -> None:
        if name:
            self._last_active[_norm(name)] = ts

    def analyze(self, procs: list[ProcessInfo], fg_name: str, ts: float,
                ai_names: set[str] | None = None, workload_mode: str = "background") -> list[ProcessAssessment]:
        ai_names = {_base(n) for n in (ai_names or set())}
        wl = {_base(n) for n in self.settings.whitelist}
        fg = _norm(fg_name)
        out = []
        alive = {(p.pid, p.create_time) for p in procs}
        for p in procs:
            n = _norm(p.name)
            identity = (p.pid, p.create_time)
            if identity not in self._seen:
                self._last_active[n] = ts
            if p.cpu_percent >= 2 or p.io_bytes_per_sec >= MB:
                self._last_active[n] = ts
            # 처음 보는 프로세스는 "방금 활성이었다"고 보수적으로 가정
            last = self._last_active.setdefault(n, ts)
            inactive = 0.0 if n == fg else max(0.0, ts - last)
            if n == fg:
                cat = Category.FOREGROUND
            elif inactive < 300:
                cat = Category.NORMAL
            elif inactive < 1800:
                cat = Category.BACKGROUND
            elif inactive < 7200:
                cat = Category.IDLE
            else:
                cat = Category.DEEP_IDLE
            reason = self._protect_reason(p, n, fg, wl, ai_names)
            if reason is None and not p.activity_known:
                reason = "활동/권한 정보 측정 불가"
            if reason is None and (base(n) in ALWAYS_ACTIVE or p.cpu_percent >= 2 or p.io_bytes_per_sec >= MB):
                reason = "미디어/다운로드/컴파일/서버 또는 활성 작업"
            if reason is None and base(n) in GROUPS.get(workload_mode, set()):
                reason = "현재 작업 그룹 보호"
            out.append(ProcessAssessment(p, cat, inactive, reason))
        self._seen = alive
        names = {_norm(p.name) for p in procs}
        self._last_active = {k: v for k, v in self._last_active.items() if k in names}
        return out

    def _protect_reason(self, p: ProcessInfo, n: str, fg: str, wl: set[str], ai: set[str]) -> str | None:
        b = _base(n)
        if p.pid <= 4 or p.pid == self._self_pid:
            return "시스템/자기 자신"
        if n == fg:
            return "현재 사용 중인 앱"
        if n in SYSTEM_NAMES:
            return "Windows 핵심/시스템 프로세스"
        if p.session == 0:
            return "서비스 세션(Session 0)"
        if p.session < 0:
            return "세션 정보 확인 불가"
        if b in wl:
            return "사용자 예외 목록"
        if b in ai:
            return "AI/LLM 대상 프로세스"
        if n in MMAP_HEAVY_NAMES:
            return "메모리 매핑/DB 계열 앱"
        # WS가 Private Bytes보다 훨씬 크면 공유/파일 매핑 비중이 큼 → 축소 효과가 낮고 위험
        if p.ws > 200 * MB and p.private < 0.4 * p.ws:
            return "메모리 매핑 파일 비중이 큰 앱"
        return None


def top_groups(assessments: list[ProcessAssessment], n: int = 8) -> list[dict]:
    """같은 이름의 프로세스(Chrome 등)를 합산한 Top Memory Processes."""
    g: dict[str, dict] = defaultdict(lambda: {"count": 0, "ws": 0, "private": 0, "category": None,
                                              "protected": None})
    for a in assessments:
        d = g[a.info.name]
        d["count"] += 1
        d["ws"] += a.info.ws
        d["private"] += a.info.private
        d["category"] = d["category"] or a.category.value
        d["protected"] = d["protected"] or a.protected_reason
    rows = [dict(name=k, **v) for k, v in g.items()]
    rows.sort(key=lambda r: r["ws"], reverse=True)
    return rows[:n]
