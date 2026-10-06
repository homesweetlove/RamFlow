"""IPC/내장 모드 공통 명령 처리기. 허용된 명령만 실행한다."""
from __future__ import annotations
import math

from ..config.settings import data_dir
from ..core.benchmark import run_benchmark
from ..core.engine import Engine
from ..core.learning import UsageLearner  # noqa: F401  (타입 참조용)


def dispatch(engine: Engine, cmd: str, args: dict) -> dict:
    if not isinstance(args, dict):
        raise ValueError("명령 인수는 JSON 객체여야 합니다.")
    # 벤치마크/폴더 분석의 대기 시간에는 엔진의 모니터링을 계속 허용한다.
    if cmd == "benchmark":
        return run_benchmark(engine, _number(args.get("seconds", 5), 0.1, 30),
                             out_dir=data_dir() / "benchmarks")
    if cmd == "scan_models":
        from ..core.storage import scan_models
        folder = args.get("folder")
        if not isinstance(folder, str) or len(folder) > 4096:
            raise ValueError("폴더 경로 형식 오류")
        return scan_models(folder)
    with engine._lock:
        return _dispatch_locked(engine, cmd, args)


def _number(value, low, high):
    if isinstance(value, bool):
        raise ValueError("숫자를 입력하세요.")
    value = float(value)
    if not math.isfinite(value) or not low <= value <= high:
        raise ValueError(f"허용 범위: {low}~{high}")
    return value


def _dispatch_locked(engine: Engine, cmd: str, args: dict) -> dict:
    if cmd == "ping":
        return {"pong": True}
    if cmd == "get_state":
        return engine.state()
    if cmd == "get_history":
        return {"history": engine.monitor.recent(int(_number(args.get("n", 300), 1, 1000)))}
    if cmd == "get_events":
        return {"events": engine.log.recent(int(_number(args.get("n", 200), 1, 1000)))}
    if cmd == "get_settings":
        return engine.settings.to_dict()
    if cmd == "set_settings":
        return engine.apply_settings(args.get("settings", {}))
    if cmd == "check_llm":
        return engine.check_llm(_number(args["model_gb"], 0, 4096), _number(args.get("extra_gb", 0), 0, 4096))
    if cmd == "recommend_pagefile":
        mode = args.get("mode", engine.settings.pagefile_mode)
        from ..core.pagefile_manager import MODES
        if mode not in MODES:
            raise ValueError("알 수 없는 Pagefile 모드")
        model = args.get("model_gb")
        return engine.recommend_pagefile(mode, None if model is None else _number(model, 0, 4096))
    if cmd == "pagefile_config":
        from ..core.pagefile_manager import read_config
        return {"config": read_config(), "disks": [d.__dict__ for d in engine.pagefile.disks]}
    if cmd == "apply_pagefile":
        # 사용자가 UI에서 명시적으로 요청했을 때만. Dry Run이면 시뮬레이션만 수행.
        from ..core.pagefile_manager import Recommendation
        rec = Recommendation(**engine.recommend_pagefile(args.get("mode")))
        ok, msg = engine.pagefile.apply(rec, dry_run=engine.settings.dry_run)
        return {"ok": ok, "message": msg}
    if cmd == "learning_summary":
        return {"hours": engine.learner.summary()}
    if cmd == "restore_pagefile":
        ok, msg = engine.pagefile.restore(dry_run=engine.settings.dry_run)
        return {"ok": ok, "message": msg}
    if cmd == "terminate":
        ok, msg = engine.terminate_process(int(_number(args["pid"], 5, 2**32 - 1)))
        return {"ok": ok, "message": msg}
    raise ValueError(f"허용되지 않은 명령: {cmd}")
