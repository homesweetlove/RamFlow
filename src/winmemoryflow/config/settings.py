"""설정 로드/저장."""
from __future__ import annotations

import json
import os
import tempfile
import math
from dataclasses import asdict, dataclass, field, fields
from pathlib import Path


def data_dir() -> Path:
    """사용자별 데이터 폴더. 테스트/포터블 모드는 RAMFLOW_HOME으로 분리한다."""
    env = os.environ.get("RAMFLOW_HOME") or os.environ.get("WINMEMORYFLOW_HOME")
    candidates = []
    if env:
        candidates.append(Path(env))
    candidates.append(Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "RamFlow")
    for c in candidates:
        try:
            c.mkdir(parents=True, exist_ok=True)
            probe = c / ".w"
            probe.write_text("x")
            probe.unlink()
            return c
        except OSError:
            continue
    return Path(tempfile.gettempdir())


@dataclass
class Settings:
    # --- 동작 ---
    auto_optimization: bool = True        # 정책 엔진 사용 여부
    dry_run: bool = True                  # 기본 ON: 실제 변경 없이 로그만
    workload_mode: str = "auto"
    cpu_management: bool = True
    ecoqos: bool = True
    # --- Memory Pressure 임계값(점수) ---
    threshold_yellow: float = 25.0
    threshold_orange: float = 50.0
    threshold_red: float = 75.0
    threshold_critical: float = 90.0
    # --- 프로세스 목록 (소문자 이름, .exe 포함 여부 무관) ---
    whitelist: list[str] = field(default_factory=list)   # 절대 건드리지 않음
    blacklist: list[str] = field(default_factory=list)   # 압박 시 우선 축소(보호 대상은 여전히 제외)
    # --- AI / LLM ---
    ai_mode: bool = False
    ai_custom_processes: list[str] = field(default_factory=list)
    ai_model_size_gb: float = 0.0
    ai_overhead_factor: float = 1.25
    # --- Pagefile ---
    pagefile_recommendation: bool = True
    pagefile_auto_manage: bool = False    # 기본 OFF
    pagefile_mode: str = "balanced"       # windows_managed|balanced|memory_saving|heavy|llm|custom
    pagefile_custom_gb: float = 0.0
    # --- 기타 ---
    startup: bool = False
    logging: bool = True
    learning: bool = False                # 사용 패턴 학습(선택)
    allow_standby_purge: bool = False     # 이전 설정 호환용. RamFlow에서는 항상 금지.
    # --- 튜닝 ---
    sample_interval_sec: float = 2.0
    process_scan_interval_sec: float = 10.0
    inactive_minutes_l1: float = 30.0
    inactive_minutes_l2: float = 120.0
    min_trim_ws_mb_l1: float = 500.0
    min_trim_ws_mb_l2: float = 150.0
    trim_cooldown_min: float = 15.0

    @classmethod
    def load(cls, path: Path | None = None) -> "Settings":
        path = path or data_dir() / "config.json"
        s = cls()
        try:
            raw = json.loads(path.read_text(encoding="utf-8"))
            s.update(raw)
        except (OSError, ValueError):
            pass
        return s

    def update(self, raw: dict) -> None:
        """알려진 키만, 타입이 맞을 때만 반영한다 (IPC 입력 검증 겸용)."""
        if not isinstance(raw, dict):
            raise ValueError("설정은 JSON 객체여야 합니다.")
        candidate = asdict(self)
        for f in fields(self):
            if f.name not in raw:
                continue
            cur = getattr(self, f.name)
            val = raw[f.name]
            if isinstance(cur, bool) and isinstance(val, bool):
                candidate[f.name] = val
            elif isinstance(cur, (int, float)) and not isinstance(cur, bool) \
                    and isinstance(val, (int, float)) and not isinstance(val, bool):
                if not math.isfinite(val):
                    raise ValueError(f"유효하지 않은 숫자: {f.name}")
                candidate[f.name] = type(cur)(val)
            elif isinstance(cur, str) and isinstance(val, str):
                candidate[f.name] = val
            elif isinstance(cur, list) and isinstance(val, list):
                if len(val) > 256 or any(not isinstance(x, str) or len(x) > 260 for x in val):
                    raise ValueError(f"프로세스 목록 형식 오류: {f.name}")
                candidate[f.name] = [x.strip().lower() for x in val if x.strip()]
        thresholds = [candidate[k] for k in ("threshold_yellow", "threshold_orange", "threshold_red", "threshold_critical")]
        if not (0 < thresholds[0] < thresholds[1] < thresholds[2] < thresholds[3] <= 100):
            raise ValueError("압박 임계값은 0~100 범위에서 순서대로 증가해야 합니다.")
        bounds = {"sample_interval_sec": (0.5, 60), "process_scan_interval_sec": (1, 120),
                  "inactive_minutes_l1": (30, 1440), "inactive_minutes_l2": (120, 1440),
                  "min_trim_ws_mb_l1": (150, 1048576), "min_trim_ws_mb_l2": (150, 1048576),
                  "trim_cooldown_min": (15, 1440), "ai_model_size_gb": (0, 4096),
                  "ai_overhead_factor": (1, 4), "pagefile_custom_gb": (0, 4096)}
        for key, (lo, hi) in bounds.items():
            if not lo <= candidate[key] <= hi:
                raise ValueError(f"{key}: 허용 범위는 {lo}~{hi}입니다.")
        if candidate["workload_mode"] not in {"auto", "developer", "gaming", "ai", "browser", "office", "media", "battery", "background"}:
            raise ValueError("알 수 없는 작업 모드입니다.")
        if candidate["pagefile_mode"] not in {"windows_managed", "balanced", "memory_saving", "heavy", "llm", "custom"}:
            raise ValueError("알 수 없는 Pagefile 모드입니다.")
        candidate["allow_standby_purge"] = False
        candidate["pagefile_auto_manage"] = False
        for key, value in candidate.items():
            setattr(self, key, value)

    def save(self, path: Path | None = None) -> None:
        path = path or data_dir() / "config.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent, delete=False) as f:
            json.dump(asdict(self), f, ensure_ascii=False, indent=2)
            temporary = Path(f.name)
        try:
            temporary.replace(path)
        finally:
            temporary.unlink(missing_ok=True)

    def to_dict(self) -> dict:
        return asdict(self)
