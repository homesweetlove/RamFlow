"""패키징된 두 실행파일에서 실제 Qt 창 생성과 정상 종료까지 검증한다."""
import os
import subprocess
from pathlib import Path

root = Path(__file__).resolve().parents[1]
env = dict(os.environ, QT_QPA_PLATFORM="offscreen", RAMFLOW_HOME=str(root / ".test-data" / "packaged"))
for name, args in [("RamFlow", ["--smoke-test"]), ("RamFlow-cli", ["ui", "--smoke-test"])]:
    executable = root / "dist" / name / (name + ".exe")
    result = subprocess.run([str(executable), *args], env=env, capture_output=True, timeout=20,
                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if result.returncode or b"Traceback" in result.stderr:
        raise RuntimeError(f"{name}: {result.stderr.decode(errors='replace')}")
    print(name, "GUI packaged smoke passed")
