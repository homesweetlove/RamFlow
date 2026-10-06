"""UI 공용 위젯과 테마."""
from __future__ import annotations

import time
import os
from collections import deque

from PySide6.QtCore import QPointF, QRectF, Qt, QTimer
from PySide6.QtGui import (QBrush, QColor, QFont, QIcon, QLinearGradient, QPainter, QPainterPath,
                           QPen, QPixmap, QFontDatabase)
from PySide6.QtWidgets import QFrame, QLabel, QVBoxLayout, QWidget

COLORS = {"GREEN": "#34c759", "YELLOW": "#ffd60a", "ORANGE": "#ff9f0a", "RED": "#ff453a"}
LABELS = {"GREEN": "정상", "YELLOW": "주의", "ORANGE": "높음", "RED": "심각"}


def configure_fonts(app):
    """Qt offscreen에서도 Windows 기본 한글 폰트를 명시적으로 로드한다."""
    folder = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "Fonts")
    for name in ("segoeui.ttf", "malgun.ttf"):
        path = os.path.join(folder, name)
        if os.path.isfile(path):
            QFontDatabase.addApplicationFont(path)
    app.setFont(QFont("Malgun Gothic", 10))

STYLE = """
* { font-family: 'Segoe UI Variable Text', 'Segoe UI', 'Malgun Gothic'; font-size: 13px; color: #e8e8ed; }
QMainWindow, QWidget#root { background: #17171b; }
QWidget#sidebar { background: #1e1e24; border-right: 1px solid #2c2c34; }
QPushButton#nav { text-align: left; padding: 10px 14px; border: none; border-radius: 8px; background: transparent; color: #a8a8b3; }
QPushButton#nav:hover { background: #2a2a33; color: #fff; }
QPushButton#nav:checked { background: #31313c; color: #fff; font-weight: 600; }
QFrame#card { background: #202027; border: 1px solid #2d2d36; border-radius: 12px; }
QLabel#cardTitle { color: #8d8d99; font-size: 11px; }
QLabel#cardValue { font-size: 20px; font-weight: 600; }
QLabel#cardSub { color: #8d8d99; font-size: 11px; }
QLabel#h1 { font-size: 22px; font-weight: 700; }
QLabel#hint { color: #8d8d99; }
QPushButton { background: #2f2f3a; border: 1px solid #3a3a46; border-radius: 8px; padding: 7px 14px; }
QPushButton:hover { background: #3a3a48; }
QPushButton#accent { background: #0a84ff; border: none; color: white; font-weight: 600; }
QPushButton#accent:hover { background: #3399ff; }
QPushButton#danger { background: #5a2623; border-color: #7a312d; }
QTableWidget, QPlainTextEdit, QTextEdit, QLineEdit, QSpinBox, QDoubleSpinBox, QComboBox {
  background: #1b1b21; border: 1px solid #2d2d36; border-radius: 8px; padding: 4px; selection-background-color: #0a84ff; }
QHeaderView::section { background: #202027; color: #8d8d99; border: none; padding: 6px; }
QTableWidget { gridline-color: #25252d; }
QCheckBox { spacing: 8px; }
QScrollBar:vertical { background: transparent; width: 10px; }
QScrollBar::handle:vertical { background: #3a3a46; border-radius: 5px; min-height: 30px; }
QScrollBar::add-line, QScrollBar::sub-line { height: 0; }
QToolTip { background: #2a2a33; color: #fff; border: 1px solid #3a3a46; }
"""


def make_icon(color_hex: str, ring: bool = True) -> QIcon:
    pm = QPixmap(64, 64)
    pm.fill(Qt.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.Antialiasing)
    c = QColor(color_hex)
    p.setBrush(QBrush(c))
    p.setPen(QPen(QColor(255, 255, 255, 200), 4) if ring else Qt.NoPen)
    p.drawEllipse(6, 6, 52, 52)
    p.end()
    return QIcon(pm)


class Card(QFrame):
    """작은 지표 카드."""

    def __init__(self, title: str):
        super().__init__()
        self.setObjectName("card")
        lay = QVBoxLayout(self)
        lay.setContentsMargins(14, 10, 14, 10)
        lay.setSpacing(2)
        self.t = QLabel(title)
        self.t.setObjectName("cardTitle")
        self.v = QLabel("-")
        self.v.setObjectName("cardValue")
        self.s = QLabel("")
        self.s.setObjectName("cardSub")
        for w in (self.t, self.v, self.s):
            lay.addWidget(w)

    def set(self, value: str, sub: str = "") -> None:
        self.v.setText(value)
        self.s.setText(sub)


class PressureGraph(QWidget):
    """Memory Pressure 실시간 그래프. 시간 기반 스크롤 + 값 이징으로 부드럽게 표시한다."""
    WINDOW = 300.0   # 5분

    def __init__(self):
        super().__init__()
        self.setMinimumHeight(190)
        self.points: deque[tuple[float, float]] = deque(maxlen=1000)
        self.color = "GREEN"
        self._disp = 0.0
        self._target = 0.0
        self._timer = QTimer(self)
        self._timer.setInterval(33)
        self._timer.timeout.connect(self._frame)

    def showEvent(self, e):          # 보일 때만 애니메이션 → 숨김/트레이 상태에서 CPU 0
        self._timer.start()
        super().showEvent(e)

    def hideEvent(self, e):
        self._timer.stop()
        super().hideEvent(e)

    def push(self, t: float, score: float, color: str) -> None:
        self.points.append((t, score))
        self._target = score
        self.color = color

    def _frame(self) -> None:
        self._disp += (self._target - self._disp) * 0.18
        self.update()

    def paintEvent(self, _):
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        r = QRectF(self.rect()).adjusted(34, 8, -8, -20)
        p.fillRect(self.rect(), QColor("#202027"))
        # 임계값 밴드
        bands = [(0, 25, "GREEN"), (25, 50, "YELLOW"), (50, 75, "ORANGE"), (75, 100, "RED")]
        for lo, hi, c in bands:
            col = QColor(COLORS[c])
            col.setAlpha(16)
            y1 = r.bottom() - r.height() * hi / 100
            y0 = r.bottom() - r.height() * lo / 100
            p.fillRect(QRectF(r.left(), y1, r.width(), y0 - y1), col)
        p.setPen(QPen(QColor("#3a3a46"), 1, Qt.DashLine))
        f = QFont(self.font())
        f.setPointSize(8)
        p.setFont(f)
        for v in (0, 25, 50, 75, 100):
            y = r.bottom() - r.height() * v / 100
            p.drawLine(QPointF(r.left(), y), QPointF(r.right(), y))
            p.setPen(QColor("#8d8d99"))
            p.drawText(QRectF(0, y - 8, 30, 16), Qt.AlignRight | Qt.AlignVCenter, str(v))
            p.setPen(QPen(QColor("#3a3a46"), 1, Qt.DashLine))
        p.setPen(QColor("#8d8d99"))
        p.drawText(QRectF(r.left(), r.bottom() + 2, 60, 16), Qt.AlignLeft, "-5분")
        p.drawText(QRectF(r.right() - 40, r.bottom() + 2, 40, 16), Qt.AlignRight, "지금")

        if not self.points:
            return
        now = time.time()
        pts = [(now - t, s) for t, s in self.points if now - t <= self.WINDOW + 5]
        xy = [QPointF(r.right() - r.width() * age / self.WINDOW,
                      r.bottom() - r.height() * max(0, min(100, s)) / 100) for age, s in pts]
        xy.append(QPointF(r.right(), r.bottom() - r.height() * max(0, min(100, self._disp)) / 100))
        if len(xy) < 2:
            return
        path = QPainterPath(xy[0])
        for a, b in zip(xy, xy[1:]):   # 중점 기반 부드러운 곡선
            mid = (a.x() + b.x()) / 2
            path.cubicTo(QPointF(mid, a.y()), QPointF(mid, b.y()), b)
        col = QColor(COLORS.get(self.color, "#34c759"))
        fill = QPainterPath(path)
        fill.lineTo(xy[-1].x(), r.bottom())
        fill.lineTo(xy[0].x(), r.bottom())
        fill.closeSubpath()
        g = QLinearGradient(0, r.top(), 0, r.bottom())
        top = QColor(col)
        top.setAlpha(110)
        bot = QColor(col)
        bot.setAlpha(8)
        g.setColorAt(0, top)
        g.setColorAt(1, bot)
        p.setClipRect(r.adjusted(0, -4, 4, 2))
        p.fillPath(fill, QBrush(g))
        p.setPen(QPen(col, 2.2, Qt.SolidLine, Qt.RoundCap, Qt.RoundJoin))
        p.drawPath(path)
        p.setBrush(col)
        p.setPen(Qt.NoPen)
        p.drawEllipse(xy[-1], 4, 4)
