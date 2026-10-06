"""사용자가 지정한 폴더의 모델 파일을 읽기 전용으로 분석한다. 이동/삭제/업로드하지 않는다."""
from __future__ import annotations

import os
import stat
import time
from pathlib import Path

MODEL_EXTENSIONS = {".gguf", ".safetensors", ".ckpt", ".onnx", ".pt", ".pth"}


def scan_models(folder: str, max_files: int = 10000, now: float | None = None) -> dict:
    root = Path(os.path.abspath(Path(folder).expanduser()))
    if not root.is_dir():
        raise ValueError("분석할 폴더를 선택하세요.")
    now = time.time() if now is None else now
    rows, skipped, examined = [], 0, 0
    truncated = False
    def walk_error(_):
        nonlocal skipped
        skipped += 1
    for parent, dirs, files in os.walk(root, followlinks=False, onerror=walk_error):
        dirs[:] = [d for d in dirs if not _reparse(Path(parent) / d)]
        for name in files:
            examined += 1
            if examined > max_files:
                truncated = True
                break
            file = Path(parent) / name
            if file.suffix.lower() not in MODEL_EXTENSIONS or _reparse(file):
                continue
            try:
                info = file.stat()
                days = max(0, (now - max(info.st_atime, info.st_mtime)) / 86400)
                state = "HOT" if days < 7 else "WARM" if days < 30 else "COLD"
                rows.append({"name": name, "path": str(file), "size": info.st_size,
                             "state": state, "days": round(days, 1)})
            except OSError:
                skipped += 1
        if truncated:
            break
    rows.sort(key=lambda r: r["size"], reverse=True)
    return {"folder": str(root), "models": rows, "total_bytes": sum(r["size"] for r in rows),
            "cold_bytes": sum(r["size"] for r in rows if r["state"] == "COLD"),
            "skipped": skipped, "truncated": truncated,
            "note": "접근/수정 시각 기반 추정입니다. Windows의 접근 시각 갱신 설정에 따라 실제 사용 빈도와 다를 수 있습니다."}


def _reparse(path: Path) -> bool:
    try:
        info = path.lstat()
        return stat.S_ISLNK(info.st_mode) or bool(getattr(info, "st_file_attributes", 0) & 0x400)
    except OSError:
        return True
