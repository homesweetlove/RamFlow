"""이벤트 로그: 메모리 내 링버퍼 + 파일(JSONL, 텍스트) 기록."""
from __future__ import annotations

import json
import threading
import time
from collections import deque
from pathlib import Path

# 이벤트 종류
PRESSURE = "pressure"
WORKING_SET = "working_set"
PRIORITY = "priority"
PAGEFILE = "pagefile"
HARD_FAULT = "hard_fault"
LLM = "llm"
WARNING = "warning"
INFO = "info"
STANDBY = "standby"


class EventLog:
    def __init__(self, directory: Path | None = None, enabled: bool = True, capacity: int = 1000):
        self.enabled = enabled
        self._buf: deque[dict] = deque(maxlen=capacity)
        self._lock = threading.Lock()
        self._dir = directory
        self._seq = 0

    def add(self, kind: str, title: str, detail: str = "", level: str = "info",
            ts: float | None = None, **data) -> dict:
        ev = {"ts": ts if ts is not None else time.time(), "kind": kind,
              "title": title, "detail": detail, "level": level, "data": data}
        with self._lock:
            self._seq += 1
            ev["seq"] = self._seq
            self._buf.append(ev)
        if self.enabled and self._dir:
            self._write(ev)
        return ev

    def recent(self, n: int = 100) -> list[dict]:
        with self._lock:
            return list(self._buf)[-n:]

    @staticmethod
    def format(ev: dict) -> str:
        t = time.strftime("%H:%M:%S", time.localtime(ev["ts"]))
        body = f"{t}\n{ev['title']}"
        if ev.get("detail"):
            body += f"\n{ev['detail']}"
        return body

    def _write(self, ev: dict) -> None:
        try:
            jl = self._dir / "events.jsonl"
            if jl.exists() and jl.stat().st_size > 5 * 1024 * 1024:
                jl.replace(self._dir / "events.1.jsonl")
            with jl.open("a", encoding="utf-8") as f:
                f.write(json.dumps(ev, ensure_ascii=False) + "\n")
        except OSError:
            pass
