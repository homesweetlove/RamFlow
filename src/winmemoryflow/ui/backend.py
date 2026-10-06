"""UI가 사용하는 백엔드 추상화: 서비스(Named Pipe) 우선, 없으면 내장 엔진."""
from __future__ import annotations

from ..config.settings import Settings
from ..core.engine import Engine
from ..core.providers import WindowsProvider
from ..ipc.pipe import IpcClient
from ..service.commands import dispatch


class Backend:
    mode = "base"

    def call(self, cmd: str, **args) -> dict:
        raise NotImplementedError

    def close(self) -> None:
        pass


class IpcBackend(Backend):
    mode = "service"

    def __init__(self) -> None:
        self._c = IpcClient()

    def call(self, cmd: str, **args) -> dict:
        return self._c.request(cmd, **args)


class EmbeddedBackend(Backend):
    """서비스가 없을 때 UI 프로세스 안에서 엔진을 직접 실행 (권한 제한: 일부 프로세스는 조작 불가)."""
    mode = "embedded"

    def __init__(self) -> None:
        self.engine = Engine(WindowsProvider(), Settings.load(), persist=True)
        self.engine.pagefile.refresh_disks_async()
        self.engine.tick()
        self.engine.start_thread()

    def call(self, cmd: str, **args) -> dict:
        return dispatch(self.engine, cmd, args)

    def close(self) -> None:
        self.engine.stop()


def create_backend() -> Backend:
    if IpcClient.available():
        return IpcBackend()
    return EmbeddedBackend()
