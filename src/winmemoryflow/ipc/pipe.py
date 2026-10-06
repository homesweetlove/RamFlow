"""Named Pipe IPC (JSON, 메시지 모드).

보안: 현재 사용자 SID의 세션 파이프에만 읽기/쓰기 권한을 허용하고,
원격 클라이언트는 거부한다. pickle을 쓰지 않고 JSON만 처리하며, 서비스는 허용된 명령만 실행한다.
"""
from __future__ import annotations

import json
import threading
import time

PIPE_NAME = r"\\.\pipe\RamFlow"
MAX_MSG = 1024 * 1024


def _identity():
    import win32api
    import win32security
    token = win32security.OpenProcessToken(win32api.GetCurrentProcess(), win32security.TOKEN_QUERY)
    try:
        sid = win32security.GetTokenInformation(token, win32security.TokenUser)[0]
        return win32security.ConvertSidToStringSid(sid)
    finally:
        token.Close()


def _pipe_name():
    return PIPE_NAME + "-" + _identity()


def authorize(cmd: str, args: dict, administrator: bool) -> None:
    """관리자 프로세스의 위험한 명령은 관리자 토큰을 가진 IPC 클라이언트만 호출한다."""
    privileged = cmd in {"terminate", "apply_pagefile", "restore_pagefile"}
    if cmd == "set_settings":
        settings = args.get("settings", {})
        if not isinstance(settings, dict):
            raise ValueError("설정 형식 오류")
        privileged |= settings.get("dry_run") is False
    if privileged and not administrator:
        raise PermissionError("이 명령은 관리자 토큰이 필요합니다. 관리자 권한의 RamFlow UI에서 실행하세요.")


def _read_message(win32file, handle, deadline=None) -> bytes:
    chunks = []
    while True:
        if deadline is None:
            hr, data = win32file.ReadFile(handle, 65536)
        else:
            hr, data = _read_chunk(win32file, handle, deadline)
        chunks.append(bytes(data))
        if sum(map(len, chunks)) > MAX_MSG:
            raise ValueError("메시지가 너무 큽니다")
        if hr != 234:      # ERROR_MORE_DATA
            break
    return b"".join(chunks)


def _wait_io(win32file, handle, overlap, deadline):
    import win32event
    timeout = max(1, int((deadline - time.monotonic()) * 1000))
    if win32event.WaitForSingleObject(overlap.hEvent, timeout) != win32event.WAIT_OBJECT_0:
        win32file.CancelIoEx(handle, overlap)
        try:
            win32file.GetOverlappedResult(handle, overlap, True)
        except Exception:
            pass
        raise TimeoutError("IPC 응답 시간 초과")
    return win32file.GetOverlappedResult(handle, overlap, False)


def _read_chunk(win32file, handle, deadline):
    import pywintypes
    import win32event
    overlap = pywintypes.OVERLAPPED()
    overlap.hEvent = win32event.CreateEvent(None, True, False, None)
    try:
        hr, buffer = win32file.ReadFile(handle, 65536, overlap)
        try:
            count = _wait_io(win32file, handle, overlap, deadline)
            return 0, bytes(buffer[:count])
        except pywintypes.error as error:
            if error.winerror == 234:
                return 234, bytes(buffer)
            raise
    finally:
        overlap.hEvent.Close()


def _write_message(win32file, handle, payload, deadline):
    import pywintypes
    import win32event
    if len(payload) > MAX_MSG:
        raise ValueError("메시지가 너무 큽니다")
    overlap = pywintypes.OVERLAPPED()
    overlap.hEvent = win32event.CreateEvent(None, True, False, None)
    try:
        win32file.WriteFile(handle, payload, overlap)
        _wait_io(win32file, handle, overlap, deadline)
    finally:
        overlap.hEvent.Close()


class IpcServer:
    def __init__(self, handler):
        """handler(cmd:str, args:dict) -> dict(JSON 직렬화 가능)"""
        self.handler = handler
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._slots = threading.BoundedSemaphore(16)
        self._ready = threading.Event()
        self._error = None
        self.last_client_error = None

    def start(self) -> None:
        self._thread = threading.Thread(target=self._run_server, name="ramflow-ipc", daemon=True)
        self._thread.start()
        if not self._ready.wait(5):
            raise TimeoutError("IPC 서버 시작 시간 초과")
        if self._error:
            raise RuntimeError(f"IPC 서버 시작 실패: {self._error}")

    def _run_server(self):
        try:
            self._serve()
        except Exception as error:
            self._error = error
            self._ready.set()

    def stop(self) -> None:
        self._stop.set()
        try:       # 대기 중인 ConnectNamedPipe를 깨운다
            IpcClient().request("ping")
        except Exception as error:
            self.last_client_error = error

    def _serve(self) -> None:
        import pywintypes
        import win32pipe
        import win32security

        sa = pywintypes.SECURITY_ATTRIBUTES()
        # 0x120183은 읽기/쓰기/속성/동기화만 포함하며 FILE_CREATE_PIPE_INSTANCE(4)는 제외한다.
        from ..core.win_api import is_admin
        # 일반 권한 서버는 자신의 토큰으로 파이프를 만들 권한도 필요하다.
        # 관리자 서비스에서는 일반 사용자에게 인스턴스 생성 권한을 주지 않는다.
        access = "0x120183" if is_admin() else "GA"
        sddl = f"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;{access};;;{_identity()})"
        sa.SECURITY_DESCRIPTOR = win32security.ConvertStringSecurityDescriptorToSecurityDescriptor(
            sddl, win32security.SDDL_REVISION_1)
        mode = (win32pipe.PIPE_TYPE_MESSAGE | win32pipe.PIPE_READMODE_MESSAGE | win32pipe.PIPE_WAIT
                | getattr(win32pipe, "PIPE_REJECT_REMOTE_CLIENTS", 8))
        first = True
        while not self._stop.is_set():
            handle = win32pipe.CreateNamedPipe(_pipe_name(), win32pipe.PIPE_ACCESS_DUPLEX | (0x80000 if first else 0), mode,
                                               win32pipe.PIPE_UNLIMITED_INSTANCES, 65536, 65536, 0, sa)
            first = False
            self._ready.set()
            try:
                win32pipe.ConnectNamedPipe(handle, None)
            except pywintypes.error as e:
                if e.winerror != 535:  # 클라이언트가 ConnectNamedPipe보다 먼저 연결한 경우
                    handle.Close()
                    continue
            if not self._slots.acquire(blocking=False):
                handle.Close()
                continue
            threading.Thread(target=self._client, args=(handle,), daemon=True).start()

    def _client(self, handle) -> None:
        import win32file
        import win32pipe
        import win32security
        try:
            msg = json.loads(_read_message(win32file, handle).decode("utf-8"))
            if not isinstance(msg, dict):
                raise ValueError("메시지는 JSON 객체여야 합니다")
            cmd, args = str(msg.get("cmd", "")), msg.get("args") or {}
            try:
                if not isinstance(args, dict):
                    raise ValueError("명령 인수 형식 오류")
                win32security.ImpersonateNamedPipeClient(handle)
                try:
                    sid = win32security.CreateWellKnownSid(win32security.WinBuiltinAdministratorsSid, None)
                    administrator = win32security.CheckTokenMembership(None, sid)
                finally:
                    win32security.RevertToSelf()
                authorize(cmd, args, administrator)
                resp = {"ok": True, "data": self.handler(cmd, args)}
            except Exception as e:
                resp = {"ok": False, "error": str(e)}
            win32file.WriteFile(handle, json.dumps(resp, ensure_ascii=False).encode("utf-8"))
            win32file.FlushFileBuffers(handle)
        except Exception as error:
            self.last_client_error = error
        finally:
            try:
                win32pipe.DisconnectNamedPipe(handle)
            finally:
                handle.Close()
                self._slots.release()


class IpcClient:
    def request(self, cmd: str, _timeout_sec=None, **args) -> dict:
        import win32file
        import win32pipe
        import pywintypes
        pipe = _pipe_name()
        timeout = _timeout_sec if _timeout_sec is not None else (90 if cmd in ("benchmark", "scan_models") else 5)
        deadline = time.monotonic() + timeout
        while True:
            try:
                handle = win32file.CreateFile(pipe, 0x120183, 0, None, win32file.OPEN_EXISTING,
                                              win32file.FILE_FLAG_OVERLAPPED, None)
                break
            except pywintypes.error as error:
                if error.winerror not in (2, 231) or time.monotonic() >= deadline:
                    raise
                time.sleep(.02)
        try:
            win32pipe.SetNamedPipeHandleState(handle, win32pipe.PIPE_READMODE_MESSAGE, None, None)
            _write_message(win32file, handle, json.dumps({"cmd": cmd, "args": args}).encode("utf-8"), deadline)
            resp = json.loads(_read_message(win32file, handle, deadline).decode("utf-8"))
        finally:
            handle.Close()
        if not isinstance(resp, dict):
            raise ValueError("IPC 응답 형식 오류")
        if not resp.get("ok"):
            raise RuntimeError(resp.get("error", "unknown"))
        return resp["data"]

    @staticmethod
    def available() -> bool:
        try:
            IpcClient().request("ping", _timeout_sec=.5)
            return True
        except Exception:
            return False
