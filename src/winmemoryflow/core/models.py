"""공용 데이터 모델."""
from __future__ import annotations

from dataclasses import asdict, dataclass, field
from enum import Enum, IntEnum
from typing import Any

GB = 1024 ** 3
MB = 1024 ** 2


class Level(IntEnum):
    """단계별 대응 레벨."""
    NORMAL = 0
    MODERATE = 1
    HIGH = 2
    SEVERE = 3
    CRITICAL = 4


class Color(str, Enum):
    GREEN = "GREEN"
    YELLOW = "YELLOW"
    ORANGE = "ORANGE"
    RED = "RED"


LEVEL_COLOR = {
    Level.NORMAL: Color.GREEN,
    Level.MODERATE: Color.YELLOW,
    Level.HIGH: Color.ORANGE,
    Level.SEVERE: Color.RED,
    Level.CRITICAL: Color.RED,
}


class Category(str, Enum):
    """프로세스 활동 분류 (Windows Memory Priority 대응용)."""
    FOREGROUND = "Foreground"
    NORMAL = "Normal"
    BACKGROUND = "Background"
    IDLE = "Idle"
    DEEP_IDLE = "Deep Idle"


@dataclass
class MemorySnapshot:
    timestamp: float
    total_phys: int
    avail_phys: int
    cached: int = 0
    standby: int = 0
    modified: int = 0
    compressed: int = 0
    commit_total: int = 0
    commit_limit: int = 0
    pagefile_total: int = 0
    pagefile_used: int = 0
    hard_faults_per_sec: float = 0.0   # Pages Input/sec (hard fault 근사)
    page_faults_per_sec: float = 0.0
    pages_output_per_sec: float = 0.0  # Pagefile 쓰기 IO 근사
    compression_active: bool = False
    cpu_percent: float = 0.0
    disk_busy_percent: float | None = None
    on_battery: bool = False
    battery_percent: float | None = None

    @property
    def used_phys(self) -> int:
        return max(0, self.total_phys - self.avail_phys)

    @property
    def commit_ratio(self) -> float:
        return self.commit_total / self.commit_limit if self.commit_limit else 0.0

    @property
    def avail_ratio(self) -> float:
        return self.avail_phys / self.total_phys if self.total_phys else 1.0

    def to_dict(self) -> dict[str, Any]:
        d = asdict(self)
        d["used_phys"] = self.used_phys
        d["commit_ratio"] = self.commit_ratio
        d["avail_ratio"] = self.avail_ratio
        return d


@dataclass
class ProcessInfo:
    pid: int
    name: str
    ws: int                 # Working Set (바이트)
    private: int = 0        # Private Bytes
    exe: str | None = None
    cmdline: list[str] = field(default_factory=list)
    session: int = 1        # 0 = 서비스 세션
    create_time: float = 0.0  # PID 재사용 구분
    cpu_percent: float = 0.0
    io_bytes_per_sec: float = 0.0
    activity_known: bool = True


@dataclass
class ProcessAssessment:
    info: ProcessInfo
    category: Category
    inactive_sec: float
    protected_reason: str | None = None   # None이면 조작 가능

    def to_dict(self) -> dict[str, Any]:
        return {
            "pid": self.info.pid, "name": self.info.name, "ws": self.info.ws,
            "private": self.info.private, "category": self.category.value,
            "inactive_sec": self.inactive_sec, "protected": self.protected_reason,
        }


@dataclass
class PressureState:
    score: float                 # EMA 적용 점수 0~100
    raw_score: float
    level: Level
    color: Color
    components: dict[str, float]
    avg_30s: float = 0.0
    avg_5m: float = 0.0
    hard_critical: bool = False

    def to_dict(self) -> dict[str, Any]:
        return {
            "score": round(self.score, 1), "raw_score": round(self.raw_score, 1),
            "level": int(self.level), "color": self.color.value,
            "components": {k: round(v, 1) for k, v in self.components.items()},
            "avg_30s": round(self.avg_30s, 1), "avg_5m": round(self.avg_5m, 1),
            "hard_critical": self.hard_critical,
        }


def fmt_bytes(n: float) -> str:
    n = float(n)
    if n >= GB:
        return f"{n / GB:.1f}GB"
    if n >= MB:
        return f"{n / MB:.0f}MB"
    return f"{n / 1024:.0f}KB"
