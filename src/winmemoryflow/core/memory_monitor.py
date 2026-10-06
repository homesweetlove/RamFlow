"""실시간 메모리 수집 + 짧은 히스토리 버퍼 (UI 그래프, 서비스 재접속 시 복원용)."""
from __future__ import annotations

from collections import deque

from .models import MemorySnapshot, PressureState
from .providers import SystemProvider


class MemoryMonitor:
    def __init__(self, provider: SystemProvider, history: int = 900):
        self.provider = provider
        self.history: deque[dict] = deque(maxlen=history)   # 2초 간격이면 약 30분

    def poll(self) -> MemorySnapshot:
        return self.provider.sample()

    def record(self, s: MemorySnapshot, p: PressureState) -> None:
        self.history.append({
            "t": s.timestamp, "score": round(p.score, 1), "used": s.used_phys, "avail": s.avail_phys,
            "compressed": s.compressed, "cached": s.cached, "commit": s.commit_total,
            "hf": round(s.hard_faults_per_sec, 1), "level": int(p.level)})

    def recent(self, n: int = 300) -> list[dict]:
        return list(self.history)[-n:]
