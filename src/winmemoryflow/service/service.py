"""서비스 프로세스: 엔진 + Named Pipe 서버. 관리자 권한으로 실행한다."""
from __future__ import annotations

import time

from ..config.settings import Settings
from ..core.engine import Engine
from ..core.providers import WindowsProvider
from ..ipc.pipe import IpcServer
from .commands import dispatch


def run_service() -> None:
    engine = Engine(WindowsProvider(), Settings.load(), persist=True)
    engine.pagefile.refresh_disks_async()
    server = IpcServer(lambda cmd, args: dispatch(engine, cmd, args))
    server.start()
    engine.start_thread()
    print("WinMemoryFlow 서비스 실행 중 (Ctrl+C로 종료)")
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        pass
    finally:
        server.stop()
        engine.stop()
