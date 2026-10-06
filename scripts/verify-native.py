""".NET 포터블 UI/IPC/설치 검증. 실제 사용자 설정·프로세스·레지스트리를 변경하지 않는다."""
import json
import os
from pathlib import Path
import subprocess
import shutil
import sys
import time

root = Path(__file__).resolve().parents[1]
app = root / 'dist' / 'RamFlow-native'
data = root / '.test-data' / 'native-packaged'
data.mkdir(parents=True, exist_ok=True)
def run(args, timeout=30):
    result = subprocess.run([str(x) for x in args], capture_output=True, timeout=timeout,
                            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    if result.returncode:
        raise RuntimeError(result.stderr.decode(errors='replace') + result.stdout.decode(errors='replace'))
    return result.stdout
run([app/'RamFlow.exe', '--smoke-test', '--screenshot', root/'artifacts'/'native-dashboard.png'])
state = json.loads(run([app/'RamFlow.Service.exe', '--simulate']))
assert state['Snapshot']['RamTotal'] > 0
child = subprocess.Popen([str(app/'RamFlow.Service.exe'), '--serve', '--data-root', str(data/'engine')],
                         creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0), stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
try:
    for attempt in range(30):
        if child.poll() is not None:
            raise RuntimeError('Isolated engine did not start; another engine may be active')
        try:
            live = json.loads(run([app/'RamFlow.Service.exe', '--ipc-state'], timeout=5))
            if live['EnginePid'] == child.pid and live['Snapshot']['RamTotal'] > 0:
                break
        except (RuntimeError, subprocess.TimeoutExpired):
            pass
        time.sleep(.1)
    else:
        raise RuntimeError('Packaged engine telemetry/IPC did not become ready')
    run([app/'RamFlow.Service.exe', '--ipc-stop'])
    child.wait(timeout=10)
    assert child.returncode == 0
finally:
    if child.poll() is None:
        child.terminate()
        child.wait(timeout=5)
# 테스트 전용 경로에만 복사한다. 시작 메뉴/자동 시작/SCM은 모두 건너뛴다.
target = data / 'RamFlow'
shell = shutil.which('pwsh') or shutil.which('powershell')
if not shell:
    raise RuntimeError('PowerShell not found')
if target.exists():
    raise RuntimeError('Isolated installer target already exists; preserve it for inspection')
run([shell, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', app/'Install.ps1', '-SourceDirectory', app, '-TargetDirectory', target, '-NoIntegration'])
assert (target/'RamFlow.exe').exists()
assert json.loads((target/'RamFlow-install.json').read_text(encoding='utf-8-sig'))['product'] == 'homesweetlove.RamFlow'
run([shell, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', app/'Uninstall.ps1', '-TargetDirectory', target, '-NoIntegration'])
assert not target.exists()
print('Native packaged UI, simulation, full monitoring/IPC and isolated install/uninstall passed')
