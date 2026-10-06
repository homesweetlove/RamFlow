"""트레이 아이콘: Memory Pressure 색상 표시 + 빠른 메뉴."""
from __future__ import annotations

import time

from PySide6.QtGui import QAction
from PySide6.QtWidgets import QMenu, QSystemTrayIcon

from ..core.models import fmt_bytes
from .widgets import COLORS, LABELS, make_icon
from ..core.workload import MODE_LABELS


class Tray(QSystemTrayIcon):
    def __init__(self, window, backend, app):
        super().__init__(make_icon(COLORS["GREEN"]))
        self.window, self.backend, self.app = window, backend, app
        self._color = "GREEN"
        self._last_notify = 0.0
        menu = QMenu()
        a = QAction("RamFlow 열기", menu)
        a.triggered.connect(self.show_window)
        menu.addAction(a)
        self.dry = QAction("Dry Run", menu, checkable=True)
        self.dry.triggered.connect(lambda v: backend.call("set_settings", settings={"dry_run": v}))
        menu.addAction(self.dry)
        self.ai = QAction("AI 모드", menu, checkable=True)
        self.ai.triggered.connect(lambda v: backend.call("set_settings", settings={"ai_mode": v}))
        menu.addAction(self.ai)
        modes = menu.addMenu("작업 모드")
        for key, label in MODE_LABELS.items():
            action = QAction(label, modes)
            action.triggered.connect(lambda _=False, key=key: window._settings_call({"workload_mode": key}))
            modes.addAction(action)
        self.pause = QAction("최적화 일시 중지", menu, checkable=True)
        self.pause.triggered.connect(lambda on: window._settings_call({"auto_optimization": not on}))
        menu.addAction(self.pause)
        menu.addSeparator()
        q = QAction("종료", menu)
        q.triggered.connect(app.quit)
        menu.addAction(q)
        self.setContextMenu(menu)
        self.activated.connect(lambda r: self.show_window() if r == QSystemTrayIcon.Trigger else None)
        window.state_changed.connect(self.update_state)

    def show_window(self) -> None:
        self.window.showNormal()
        self.window.raise_()
        self.window.activateWindow()

    def update_state(self, st: dict) -> None:
        p, s = st["pressure"], st["snapshot"]
        color = p["color"]
        if color != self._color:
            self._color = color
            self.setIcon(make_icon(COLORS[color]))
            if color in ("ORANGE", "RED") and time.time() - self._last_notify > 300:
                self._last_notify = time.time()
                self.showMessage("메모리 압박 " + LABELS[color],
                                 f"Available {fmt_bytes(s['avail_phys'])} · 점수 {p['score']:.0f}",
                                 QSystemTrayIcon.Warning, 6000)
        self.setToolTip(f"RamFlow · {color.title()} {p['score']:.0f}\n"
                        f"Available {fmt_bytes(s['avail_phys'])} / Commit {s['commit_ratio'] * 100:.0f}%"
                        + ("\n[Dry Run]" if st["dry_run"] else ""))
        self.dry.blockSignals(True)
        self.dry.setChecked(st["dry_run"])
        self.dry.blockSignals(False)
        self.ai.blockSignals(True)
        self.ai.setChecked(st["ai_mode"])
        self.ai.blockSignals(False)
        self.pause.setChecked(st.get("optimization_paused", False))
