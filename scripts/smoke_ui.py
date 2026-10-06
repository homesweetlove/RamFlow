"""UI 스모크 테스트: 오프스크린으로 창을 띄우고 스크린샷을 저장한다.
사용: python scripts/smoke_ui.py [출력.png]"""
import os
import sys
import time
from pathlib import Path

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "src"))

from PySide6.QtWidgets import QApplication  # noqa: E402

from winmemoryflow.ui.backend import Backend  # noqa: E402
from winmemoryflow.core.engine import Engine  # noqa: E402
from winmemoryflow.core.mock_provider import MockProvider  # noqa: E402
from winmemoryflow.core.pagefile_manager import DiskInfo  # noqa: E402
from winmemoryflow.core.models import GB  # noqa: E402
from winmemoryflow.service.commands import dispatch  # noqa: E402
from winmemoryflow.ui.main_window import MainWindow  # noqa: E402
from winmemoryflow.ui.tray import Tray  # noqa: E402
from winmemoryflow.ui.widgets import STYLE, configure_fonts  # noqa: E402

out = sys.argv[1] if len(sys.argv) > 1 else "smoke.png"
Path(out).parent.mkdir(parents=True, exist_ok=True)
app = QApplication([])
configure_fonts(app)
app.setStyleSheet(STYLE)
class DemoBackend(Backend):
    mode = "embedded"
    def __init__(self):
        self.provider = MockProvider("16gb_chrome100", foreground=(300, "code.exe"))
        self.engine = Engine(self.provider)
        self.engine.pagefile.disks = [DiskInfo("C:", "NVMe", 200 * GB, 500 * GB)]
        for _ in range(20):
            self.engine.tick()
            self.provider.advance(60)
    def call(self, cmd, **args):
        if cmd == "pagefile_config":
            return {"config": {"system_managed": True}, "disks": [d.__dict__ for d in self.engine.pagefile.disks]}
        return dispatch(self.engine, cmd, args)
    def close(self):
        self.engine.stop()

b = DemoBackend()
w = MainWindow(b)
tray = Tray(w, b, app)
w.show()
end = time.time() + .5
while time.time() < end:
    app.processEvents()
    time.sleep(0.03)
w._check_llm()
w._pf_recommend()
w.grab().save(out)
print("backend:", b.mode, "| saved", out)
for i in range(w.stack.count()):
    w._go(i)
    app.processEvents()
    w.grab().save(str(Path(out).with_name(f"page-{i}.png")))
w.shutdown_workers()
b.close()
