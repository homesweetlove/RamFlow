"""CLI 진입점: python -m winmemoryflow [ui|service|monitor|simulate|llm-check|pagefile|benchmark]"""
from __future__ import annotations

import argparse
import json
import sys
import time


def _cmd_monitor(args) -> None:
    from .config.settings import Settings
    from .core.engine import Engine
    from .core.models import fmt_bytes
    from .core.providers import WindowsProvider
    e = Engine(WindowsProvider(), Settings())
    for _ in range(args.count):
        st = e.tick()
        s, p = st["snapshot"], st["pressure"]
        print(f"[{p['color']:<6}] 점수 {p['score']:5.1f} | RAM {fmt_bytes(s['used_phys'])}/{fmt_bytes(s['total_phys'])} "
              f"| Avail {fmt_bytes(s['avail_phys'])} | Comp {fmt_bytes(s['compressed'])} "
              f"| Standby {fmt_bytes(s['standby'])} | Commit {fmt_bytes(s['commit_total'])}/{fmt_bytes(s['commit_limit'])} "
              f"| HF {s['hard_faults_per_sec']:.0f}/s")
        time.sleep(1.0)
    print("Top:", ", ".join(f"{r['name']} {fmt_bytes(r['ws'])}" for r in st["top_processes"][:5]))


def _cmd_simulate(args) -> None:
    """Mock 시나리오로 정책을 Dry Run 한다 (실제 시스템 무변경)."""
    from .config.settings import Settings
    from .core.engine import Engine
    from .core.eventlog import EventLog
    from .core.mock_provider import SCENARIOS, MockProvider
    if args.scenario not in SCENARIOS:
        sys.exit(f"시나리오: {', '.join(SCENARIOS)}")
    p = MockProvider(args.scenario, foreground=(903, "notepad.exe"))
    s = Settings(dry_run=True)
    e = Engine(p, s, log=EventLog(enabled=False))
    for i in range(args.ticks):
        st = e.tick()
        p.advance(60)   # 비활성 시간이 흐르도록 틱마다 1분 가속
    for ev in e.log.recent(100):
        print(e.log.format(ev), "\n")
    print("최종 상태:", st["pressure"]["color"], st["pressure"]["score"])


def _cmd_llm(args) -> None:
    from .config.settings import Settings
    from .core.engine import Engine
    from .core.providers import WindowsProvider
    e = Engine(WindowsProvider(), Settings())
    e.tick()
    r = e.check_llm(args.model_gb, args.extra_gb)
    print(r["text"])
    for n in r["notes"]:
        print("-", n)


def _cmd_pagefile(args) -> None:
    from .config.settings import Settings
    from .core.engine import Engine
    from .core.pagefile_manager import detect_disks, read_config
    from .core.providers import WindowsProvider
    e = Engine(WindowsProvider(), Settings(pagefile_mode=args.mode, ai_model_size_gb=args.model_gb))
    e.pagefile.disks = detect_disks()
    e.tick()
    print("현재 설정:", json.dumps(read_config(), ensure_ascii=False))
    print("디스크:", [(d.drive, d.kind, d.free // 2**30) for d in e.pagefile.disks])
    print(json.dumps(e.recommend_pagefile(), ensure_ascii=False, indent=2))


def _cmd_benchmark(args) -> None:
    from .config.settings import Settings, data_dir
    from .core.benchmark import run_benchmark
    from .core.engine import Engine
    from .core.providers import WindowsProvider
    e = Engine(WindowsProvider(), Settings(dry_run=not args.real))
    e.tick()
    r = run_benchmark(e, args.seconds, out_dir=data_dir() / "benchmarks")
    print(json.dumps(r, ensure_ascii=False, indent=2))


def main() -> None:
    for st in (sys.stdout, sys.stderr):
        try:
            st.reconfigure(encoding="utf-8")
        except (AttributeError, ValueError):
            pass
    ap = argparse.ArgumentParser(prog="ramflow", description="Windows용 지능형 리소스 오케스트레이터")
    sub = ap.add_subparsers(dest="cmd")
    ui = sub.add_parser("ui", help="GUI/트레이 실행 (기본값)")
    ui.add_argument("--tray", action="store_true", help="트레이에서 시작")
    sub.add_parser("service", help="관리자 권한 서비스 실행")
    m = sub.add_parser("monitor", help="콘솔 실시간 모니터")
    m.add_argument("--count", type=int, default=10)
    s = sub.add_parser("simulate", help="Mock 시나리오 Dry Run")
    s.add_argument("scenario", nargs="?", default="8gb_90pct")
    s.add_argument("--ticks", type=int, default=60)
    l = sub.add_parser("llm-check", help="모델 실행 가능성 평가")
    l.add_argument("model_gb", type=float)
    l.add_argument("--extra-gb", type=float, default=0.0)
    p = sub.add_parser("pagefile", help="Pagefile 권장값")
    p.add_argument("--mode", default="balanced")
    p.add_argument("--model-gb", type=float, default=0.0)
    b = sub.add_parser("benchmark", help="Before/After 벤치마크")
    b.add_argument("--seconds", type=float, default=5)
    b.add_argument("--real", action="store_true", help="Dry Run 해제(실제 적용)")
    storage = sub.add_parser("storage", help="모델 폴더 읽기 전용 분석")
    storage.add_argument("folder")
    args = ap.parse_args()
    if args.cmd in ("monitor", "simulate"):
        number = args.count if args.cmd == "monitor" else args.ticks
        if not 1 <= number <= 10000:
            ap.error("횟수는 1~10000 범위입니다.")
    if args.cmd in ("llm-check", "pagefile", "benchmark"):
        import math
        for key in ("model_gb", "extra_gb", "seconds"):
            value = getattr(args, key, None)
            if value is not None and (not math.isfinite(value) or value < 0 or value > (30 if key == "seconds" else 4096)):
                ap.error(f"{key} 범위가 유효하지 않습니다.")

    if args.cmd == "service":
        from .service.service import run_service
        run_service()
    elif args.cmd == "monitor":
        _cmd_monitor(args)
    elif args.cmd == "simulate":
        _cmd_simulate(args)
    elif args.cmd == "llm-check":
        _cmd_llm(args)
    elif args.cmd == "pagefile":
        _cmd_pagefile(args)
    elif args.cmd == "benchmark":
        _cmd_benchmark(args)
    elif args.cmd == "storage":
        from .core.storage import scan_models
        print(json.dumps(scan_models(args.folder), ensure_ascii=False, indent=2))
    else:
        from .ui.app import run_ui
        run_ui(start_hidden=getattr(args, "tray", False))


if __name__ == "__main__":
    main()
