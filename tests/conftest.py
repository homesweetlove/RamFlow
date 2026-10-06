"""테스트 데이터는 저장소 안에 격리한다. 사용자 설정/임시 폴더에 쓰지 않는다."""
import os
import uuid
from pathlib import Path


def pytest_configure(config):
    root = Path(__file__).resolve().parents[1] / ".test-data"
    root.mkdir(parents=True, exist_ok=True)
    os.environ.setdefault("RAMFLOW_HOME", str(root / "data"))
    if not config.option.basetemp:
        config.option.basetemp = str(root / ("pytest-" + uuid.uuid4().hex))
