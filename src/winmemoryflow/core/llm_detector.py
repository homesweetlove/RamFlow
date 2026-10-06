"""LLM / Stable Diffusion 프로세스 감지와 모델 로딩 가능성 평가."""
from __future__ import annotations

import os
from dataclasses import dataclass, field
from enum import Enum

from .models import GB, MemorySnapshot, ProcessInfo, fmt_bytes

# 프로세스 이름(소문자, .exe 제외)에 포함되면 AI 런타임으로 간주
LLM_NAME_HINTS = ("ollama", "llama-server", "llama-cli", "llama-bench", "llamafile", "lm studio",
                  "lmstudio", "koboldcpp", "llama.cpp", "comfyui", "text-generation", "vllm")
# python/node 프로세스는 명령줄 키워드로 판정
PY_KEYWORDS = ("torch", "transformers", "diffusers", "stable-diffusion", "stable_diffusion", "comfyui",
               "webui", "vllm", "llama_cpp", "llama-cpp", "gguf", "invokeai", "automatic1111",
               "accelerate", "ggml", "text-generation-webui", "kohya")
MODEL_EXTS = (".gguf", ".safetensors", ".ckpt", ".bin", ".pth")


@dataclass
class AIProcess:
    pid: int
    name: str
    kind: str            # "runtime" | "python" | "custom"
    ws: int
    model_path: str | None = None
    model_bytes: int = 0


def _base(name: str) -> str:
    n = name.lower()
    return n[:-4] if n.endswith(".exe") else n


def detect(procs: list[ProcessInfo], custom: list[str] | None = None) -> list[AIProcess]:
    custom_b = [_base(c) for c in (custom or [])]
    found = []
    for p in procs:
        b = _base(p.name)
        kind = None
        if any(h in b for h in LLM_NAME_HINTS):
            kind = "runtime"
        elif custom_b and any(c == b or c in b for c in custom_b):
            kind = "custom"
        elif b.startswith(("python", "pythonw")) and p.cmdline:
            joined = " ".join(p.cmdline).lower()
            if any(k in joined for k in PY_KEYWORDS):
                kind = "python"
        if not kind:
            continue
        path, size = _find_model(p.cmdline)
        found.append(AIProcess(p.pid, p.name, kind, p.ws, path, size))
    return found


def _find_model(cmdline: list[str]) -> tuple[str | None, int]:
    for tok in cmdline:
        t = tok.strip('"')
        if t.lower().endswith(MODEL_EXTS):
            try:
                return t, os.path.getsize(t)
            except OSError:
                return t, 0
    return None, 0


class Verdict(str, Enum):
    COMFORTABLE = "comfortable"
    AFTER_RECLAIM = "after_reclaim"
    PAGEFILE_HEAVY = "pagefile_heavy"
    FAIL = "fail"


@dataclass
class Feasibility:
    verdict: Verdict
    message: str
    model_bytes: int
    expected_runtime: int
    available: int
    commit_free: int
    commit_limit: int
    swap_dependency: float             # 0~1, 실행 메모리 중 Pagefile 의존 추정 비율
    recommended_pagefile: int = 0      # 바이트, 0이면 추가 필요 없음
    notes: list[str] = field(default_factory=list)

    def to_dict(self) -> dict:
        d = dict(self.__dict__)
        d["verdict"] = self.verdict.value
        return d


def assess_model(model_bytes: int, snap: MemorySnapshot, overhead: float = 1.25,
                 extra_bytes: int = 0, reclaimable: int = 0) -> Feasibility:
    """모델 실행 전 메모리 부족 가능성을 예측한다.

    expected_runtime = model × overhead(KV 캐시/런타임 버퍼) + extra(컨텍스트 등)
    reclaimable: 비활성 프로세스 축소로 확보 가능한 추정치.
    """
    runtime = int(model_bytes * overhead) + extra_bytes
    avail = snap.avail_phys
    commit_free = max(0, snap.commit_limit - snap.commit_total)
    notes = ["GGUF를 mmap으로 로딩하는 런타임(llama.cpp 등)은 파일 기반 페이지라 Commit을 덜 쓸 수 있습니다."]
    swap_dep = max(0.0, runtime - avail - reclaimable) / runtime if runtime else 0.0
    rec_pf = 0

    if runtime <= avail * 0.85:
        v, msg = Verdict.COMFORTABLE, "RAM 여유가 충분해 원활하게 실행할 수 있습니다."
    elif runtime <= (avail + reclaimable) * 0.85:
        v, msg = Verdict.AFTER_RECLAIM, "비활성 앱의 메모리를 줄이면 RAM 안에서 실행할 수 있습니다."
    elif runtime <= commit_free * 0.9:
        v, msg = Verdict.PAGEFILE_HEAVY, "실행 가능하지만 Pagefile 의존도가 높으며 성능 저하가 예상됩니다."
        need = snap.commit_total + int(runtime * 1.15)
        if snap.commit_limit < need:
            rec_pf = need - snap.total_phys
    else:
        v = Verdict.FAIL
        msg = "Commit Limit이 부족해 모델 로딩이 실패할 가능성이 높습니다. Pagefile을 늘리세요."
        rec_pf = snap.commit_total + int(runtime * 1.15) - snap.total_phys
    if rec_pf:
        rec_pf = ((rec_pf + GB - 1) // GB) * GB
        notes.append(f"권장 Pagefile 총량: 약 {fmt_bytes(rec_pf)}")
    return Feasibility(v, msg, model_bytes, runtime, avail, commit_free, snap.commit_limit,
                       swap_dep, rec_pf, notes)


def describe(f: Feasibility, snap: MemorySnapshot) -> str:
    """사람이 읽는 요약."""
    return (f"Physical RAM: {fmt_bytes(snap.total_phys)}\nAvailable: {fmt_bytes(f.available)}\n"
            f"Model: {fmt_bytes(f.model_bytes)}\nExpected runtime memory: {fmt_bytes(f.expected_runtime)}\n"
            f"Current Commit Limit: {fmt_bytes(f.commit_limit)} (여유 {fmt_bytes(f.commit_free)})\n\n"
            f"결과: {f.message}")
