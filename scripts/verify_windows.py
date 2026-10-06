"""Windows 실측/IPC/자체 테스트 프로세스 우선순위 왕복 검증. 다른 앱은 변경하지 않는다."""
import json
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from winmemoryflow.core import win_api
from winmemoryflow.core.providers import WindowsProvider
from winmemoryflow.ipc.pipe import IpcClient, IpcServer


def main():
    provider = WindowsProvider()
    try:
        first = provider.sample()
        time.sleep(.5)
        snap = provider.sample()
        assert snap.total_phys > 0 and snap.commit_limit >= snap.commit_total
        assert provider.list_processes()
    finally:
        provider.close()
    # 変更対象はこの検証スクリプトが起動した、何も処理していない子プロセスだけ。
    child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(15)"],
                             creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    report = {}
    try:
        pid = child.pid
        memory = win_api.get_memory_priority(pid)
        cpu = win_api.get_cpu_priority(pid)
        eco = win_api.get_power_throttling(pid)
        assert memory is not None and cpu is not None
        assert win_api.set_memory_priority(pid, 3)
        assert win_api.get_memory_priority(pid) == 3
        assert win_api.set_memory_priority(pid, memory)
        assert win_api.set_cpu_priority(pid, 0x4000)
        assert win_api.get_cpu_priority(pid) == 0x4000
        assert win_api.set_cpu_priority(pid, cpu)
        if eco is not None:
            assert win_api.set_power_throttling(pid, (eco[0] | 1, eco[1] | 1))
            assert win_api.get_power_throttling(pid)[1] & 1
            assert win_api.set_power_throttling(pid, eco)
            assert win_api.get_power_throttling(pid) == eco
        report["priority_roundtrip"] = {"memory": True, "cpu": True, "ecoqos_supported": eco is not None}
    finally:
        child.terminate()
        child.wait(timeout=5)
    server = IpcServer(lambda cmd, args: {"command": cmd, "value": args.get("value")})
    server.start()
    try:
        deadline = time.monotonic() + 5
        while not IpcClient.available() and time.monotonic() < deadline:
            time.sleep(.05)
        if server._error:
            raise RuntimeError(f"IPC server error: {server._error}")
        assert IpcClient().request("echo", value="RamFlow") == {"command": "echo", "value": "RamFlow"}
        payload = "x" * 70000
        assert IpcClient().request("echo", value=payload)["value"] == payload
        for _ in range(5):
            assert IpcClient().request("ping")["command"] == "ping"
        report["named_pipe_roundtrip"] = True
        report["named_pipe_large_message"] = True
    finally:
        server.stop()
    report["telemetry"] = {"physical_ram_gb": round(snap.total_phys / 2**30, 1),
                           "standby_measured": snap.standby > 0, "disk_measured": snap.disk_busy_percent is not None}
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
