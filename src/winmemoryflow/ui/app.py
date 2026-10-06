"""GUI 앱 진입점."""
from __future__ import annotations

import sys

from PySide6.QtWidgets import QApplication, QMessageBox
from PySide6.QtCore import QLockFile
from ..config.settings import data_dir

from .backend import create_backend
from .main_window import MainWindow
from .tray import Tray
from .widgets import STYLE, configure_fonts


def run_ui(start_hidden: bool = False) -> None:
    app = QApplication(sys.argv)
    configure_fonts(app)
    app.setApplicationName("RamFlow")
    app.setQuitOnLastWindowClosed(False)    # 창을 닫아도 트레이에서 계속 동작
    app.setStyleSheet(STYLE)
    lock = QLockFile(str(data_dir() / "ui.lock"))
    lock.setStaleLockTime(0)
    if not lock.tryLock(0):
        QMessageBox.information(None, "RamFlow", "이미 실행 중입니다. 시스템 트레이에서 RamFlow를 열어주세요.")
        return
    backend = create_backend()
    win = MainWindow(backend)
    tray = Tray(win, backend, app)
    tray.show()
    win.setWindowIcon(tray.icon())
    if not start_hidden and "--tray" not in sys.argv:
        win.show()
    app.aboutToQuit.connect(backend.close)
    app.aboutToQuit.connect(win.shutdown_workers)
    app.aboutToQuit.connect(lock.unlock)
    sys.exit(app.exec())
