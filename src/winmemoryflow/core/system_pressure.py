"""실제로 측정한 자원만 합산한다. 온도 센서는 확인되지 않으면 미지원으로 표시한다."""
from .models import MemorySnapshot


def system_pressure(snap: MemorySnapshot, memory_score: float) -> dict:
    components = {"memory": round(memory_score, 1), "cpu": round(max(0, min(100, snap.cpu_percent)), 1),
                  "disk": None if snap.disk_busy_percent is None else round(max(0, min(100, snap.disk_busy_percent)), 1),
                  "thermal": None,
                  "battery": (round(100 - snap.battery_percent, 1)
                              if snap.on_battery and snap.battery_percent is not None else None)}
    weights = {"memory": .55, "cpu": .25, "disk": .15, "battery": .05}
    valid = [(components[k], w) for k, w in weights.items() if components[k] is not None]
    score = sum(v * w for v, w in valid) / sum(w for _, w in valid)
    # Commit/물리 메모리 위기는 다른 자원의 낮은 값으로 숨기지 않는다.
    score = max(score, memory_score * .85)
    return {"score": round(score, 1), "components": components}
