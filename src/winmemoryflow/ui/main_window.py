"""메인 윈도우: Windows 11 + Activity Monitor 스타일."""
from __future__ import annotations

import json
import time

from PySide6.QtCore import QThread, QTimer, Qt, Signal
from PySide6.QtWidgets import (QCheckBox, QComboBox, QDialog, QDoubleSpinBox, QFormLayout, QFrame,
                               QGridLayout, QHBoxLayout, QHeaderView, QLabel, QLineEdit, QMainWindow,
                               QMessageBox, QPlainTextEdit, QPushButton, QStackedWidget, QTableWidget,
                               QTableWidgetItem, QVBoxLayout, QWidget, QFileDialog)

from ..core.models import fmt_bytes
from ..core.pagefile_manager import MODE_LABELS, MODES
from .backend import Backend
from .startup import set_startup
from .widgets import COLORS, LABELS, Card, PressureGraph
from ..core.workload import MODE_LABELS as WORKLOAD_LABELS

LEVEL_TEXT = {
    0: "Level 0 · 정상 — Windows 메모리 관리자에 맡기고 아무것도 하지 않습니다",
    1: "Level 1 · 주의 — 30분 이상 비활성 앱의 우선순위/EcoQoS 조정",
    2: "Level 2 · 높음 — 2시간 이상 비활성 앱만 제한적으로 Working Set 축소",
    3: "Level 3 · 심각 — Pagefile / Commit 점검 및 권장",
    4: "Level 4 · 위험 — 종료 후보 추천 (자동 종료 없음)",
}
PRINCIPLE = "남는 메모리는 파일 캐시로 활용하고, 압박이 생기면 현재 작업의 응답성을 먼저 보호합니다."


class Worker(QThread):
    done = Signal(object, object)

    def __init__(self, fn):
        super().__init__()
        self.fn = fn

    def run(self):
        try:
            self.done.emit(self.fn(), None)
        except Exception as e:      # noqa: BLE001
            self.done.emit(None, str(e))


def _title(text: str, hint: str = "") -> QWidget:
    w = QWidget()
    l = QVBoxLayout(w)
    l.setContentsMargins(0, 0, 0, 6)
    h = QLabel(text)
    h.setObjectName("h1")
    l.addWidget(h)
    if hint:
        s = QLabel(hint)
        s.setObjectName("hint")
        s.setWordWrap(True)
        l.addWidget(s)
    return w


class CriticalDialog(QDialog):
    """Level 4: 종료 후보. 사용자가 직접 '종료'를 눌러야만 종료 요청이 전송된다."""

    def __init__(self, parent, backend: Backend, candidates: list[dict]):
        super().__init__(parent)
        self.setWindowTitle("메모리 부족 — 종료 후보")
        self.backend = backend
        lay = QVBoxLayout(self)
        lay.addWidget(QLabel("시스템이 메모리 부족 상태입니다. 아래 앱을 정리하면 도움이 됩니다.\n"
                             "WinMemoryFlow는 자동으로 종료하지 않습니다. 저장하지 않은 작업을 먼저 확인하세요."))
        for c in candidates:
            row = QHBoxLayout()
            tag = " (현재 사용 중)" if c.get("protected") else ""
            row.addWidget(QLabel(f"{c['name']}  PID {c['pid']}  ·  {fmt_bytes(c['private'])}{tag}"), 1)
            b = QPushButton("종료 요청")
            b.setObjectName("danger")
            b.clicked.connect(lambda _=False, c=c: self._kill(c))
            row.addWidget(b)
            lay.addLayout(row)
        self.resize(520, 120)

    def _kill(self, c: dict) -> None:
        if QMessageBox.question(self, "확인", f"{c['name']} (PID {c['pid']})에 종료를 요청할까요?\n"
                                "저장되지 않은 데이터가 사라질 수 있습니다.") != QMessageBox.Yes:
            return
        r = self.backend.call("terminate", pid=c["pid"])
        QMessageBox.information(self, "결과", r["message"])


class MainWindow(QMainWindow):
    state_changed = Signal(dict)

    def __init__(self, backend: Backend):
        super().__init__()
        self.backend = backend
        self.setWindowTitle("RamFlow")
        self.resize(1120, 760)
        self._workers: list[Worker] = []
        self._last_critical_ts = 0.0
        self._last_event_seq = 0
        self._state: dict = {}

        root = QWidget()
        root.setObjectName("root")
        self.setCentralWidget(root)
        outer = QHBoxLayout(root)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.setSpacing(0)

        # ---- 사이드바
        side = QWidget()
        side.setObjectName("sidebar")
        side.setFixedWidth(190)
        sl = QVBoxLayout(side)
        sl.setContentsMargins(12, 16, 12, 12)
        brand = QLabel("RamFlow")
        brand.setStyleSheet("font-size:16px;font-weight:700;padding:4px 6px 14px 6px;")
        sl.addWidget(brand)
        self.stack = QStackedWidget()
        self.nav: list[QPushButton] = []
        pages = [("대시보드", self._page_dashboard()), ("AI / LLM", self._page_ai()),
                 ("Pagefile", self._page_pagefile()), ("로그", self._page_log()),
                 ("벤치마크", self._page_bench()), ("설정", self._page_settings()),
                 ("작업 그룹 / 프로세스", self._page_resources()), ("모델 저장소", self._page_storage())]
        for i, (name, w) in enumerate(pages):
            b = QPushButton(name)
            b.setObjectName("nav")
            b.setCheckable(True)
            b.clicked.connect(lambda _=False, i=i: self._go(i))
            sl.addWidget(b)
            self.nav.append(b)
            self.stack.addWidget(w)
        sl.addStretch(1)
        self.mode_lbl = QLabel("")
        self.mode_lbl.setObjectName("hint")
        self.mode_lbl.setWordWrap(True)
        sl.addWidget(self.mode_lbl)
        outer.addWidget(side)
        outer.addWidget(self.stack, 1)
        self._go(0)

        self.timer = QTimer(self)
        self.timer.timeout.connect(self.poll)
        self.timer.start(2000)
        self._prefill_history()
        self.poll()
        self._load_settings()

    def _go(self, i: int) -> None:
        for k, b in enumerate(self.nav):
            b.setChecked(k == i)
        self.stack.setCurrentIndex(i)
        if i == 5:
            self._load_settings()

    def shutdown_workers(self):
        self.timer.stop()
        for worker in list(self._workers):
            worker.wait()

    def _run(self, fn, cb) -> None:
        w = Worker(fn)
        self._workers.append(w)

        def fin(res, err):
            self._workers.remove(w)
            cb(res, err)
        w.done.connect(fin)
        w.start()

    # ------------------------------------------------------------ 대시보드
    def _page_dashboard(self) -> QWidget:
        w = QWidget()
        lay = QVBoxLayout(w)
        lay.setContentsMargins(24, 20, 24, 16)
        head = QHBoxLayout()
        head.addWidget(_title("System Resources", PRINCIPLE), 1)
        self.dry_chk = QCheckBox("Dry Run (실제 변경 안 함)")
        self.dry_chk.toggled.connect(self._toggle_dry)
        head.addWidget(self.dry_chk, 0, Qt.AlignTop)
        lay.addLayout(head)
        mode_row = QHBoxLayout()
        mode_row.addWidget(QLabel("작업 모드"))
        self.workload_combo = QComboBox()
        for key, label in WORKLOAD_LABELS.items():
            self.workload_combo.addItem(label, key)
        self.workload_combo.currentIndexChanged.connect(self._set_workload)
        mode_row.addWidget(self.workload_combo)
        self.pause_chk = QCheckBox("최적화 일시 중지")
        self.pause_chk.toggled.connect(lambda on: self._settings_call({"auto_optimization": not on}))
        mode_row.addWidget(self.pause_chk)
        self.resource_lbl = QLabel("")
        self.resource_lbl.setObjectName("hint")
        mode_row.addWidget(self.resource_lbl, 1)
        lay.addLayout(mode_row)

        top = QHBoxLayout()
        badge = QFrame()
        badge.setObjectName("card")
        badge.setFixedWidth(230)
        bl = QVBoxLayout(badge)
        self.score_lbl = QLabel("--")
        self.score_lbl.setStyleSheet("font-size:54px;font-weight:700;")
        self.state_lbl = QLabel("대기 중")
        self.state_lbl.setStyleSheet("font-size:16px;font-weight:600;")
        self.avg_lbl = QLabel("")
        self.avg_lbl.setObjectName("hint")
        self.level_lbl = QLabel("")
        self.level_lbl.setObjectName("hint")
        self.level_lbl.setWordWrap(True)
        for x in (self.score_lbl, self.state_lbl, self.avg_lbl, self.level_lbl):
            bl.addWidget(x)
        bl.addStretch(1)
        top.addWidget(badge)
        self.graph = PressureGraph()
        top.addWidget(self.graph, 1)
        lay.addLayout(top)

        self.banner = QPushButton("")
        self.banner.setObjectName("danger")
        self.banner.hide()
        self.banner.clicked.connect(self._show_critical)
        lay.addWidget(self.banner)

        grid = QGridLayout()
        self.cards = {k: Card(t) for k, t in [
            ("ram", "Physical RAM"), ("comp", "Compressed"), ("cache", "Cached"), ("avail", "Available"),
            ("pf", "Pagefile"), ("commit", "Commit"), ("hf", "Hard Fault/sec"), ("stand", "Standby / Modified")]}
        for i, c in enumerate(self.cards.values()):
            grid.addWidget(c, i // 4, i % 4)
        lay.addLayout(grid)

        lay.addWidget(QLabel("Top Memory Processes"))
        self.table = QTableWidget(0, 4)
        self.table.setHorizontalHeaderLabels(["프로세스", "개수", "메모리(WS)", "상태"])
        self.table.horizontalHeader().setSectionResizeMode(0, QHeaderView.Stretch)
        self.table.verticalHeader().hide()
        self.table.setEditTriggers(QTableWidget.NoEditTriggers)
        self.table.setSelectionMode(QTableWidget.NoSelection)
        lay.addWidget(self.table, 1)
        self.action_lbl = QLabel("")
        self.action_lbl.setObjectName("hint")
        self.action_lbl.setWordWrap(True)
        self.action_lbl.setMaximumHeight(48)
        lay.addWidget(self.action_lbl)
        return w

    def _prefill_history(self) -> None:
        try:
            for h in self.backend.call("get_history", n=150)["history"]:
                self.graph.points.append((h["t"], h["score"]))
        except Exception:
            pass

    def poll(self) -> None:
        try:
            st = self.backend.call("get_state")
        except Exception as e:      # 서비스 종료 등
            self.mode_lbl.setText(f"연결 끊김: {e}")
            return
        if not st.get("ready"):
            return
        self._state = st
        s, p = st["snapshot"], st["pressure"]
        mode = st.get("workload", "background")
        unified = st.get("system_pressure", {}).get("score", p["score"])
        disk = s.get("disk_busy_percent")
        disk_label = "측정 불가" if disk is None else f"{disk:.0f}%"
        self.resource_lbl.setText(f"통합 {unified:.0f} · CPU {s.get('cpu_percent', 0):.0f}% · 디스크 {disk_label}\n"
                                 f"{WORKLOAD_LABELS.get(mode, mode)} · 현재 앱: {st.get('foreground', {}).get('name') or '없음'}")
        self.workload_combo.blockSignals(True)
        self.workload_combo.setCurrentIndex(max(0, self.workload_combo.findData(st.get("workload_mode", "auto"))))
        self.workload_combo.blockSignals(False)
        self.pause_chk.blockSignals(True)
        self.pause_chk.setChecked(st.get("optimization_paused", False))
        self.pause_chk.blockSignals(False)
        self._update_resources(st)
        color = p["color"]
        self.graph.push(time.time(), p["score"], color)
        self.score_lbl.setText(f"{p['score']:.0f}")
        self.score_lbl.setStyleSheet(f"font-size:54px;font-weight:700;color:{COLORS[color]};")
        self.state_lbl.setText(f"{color.title()} · {LABELS[color]}")
        self.avg_lbl.setText(f"30초 평균 {p['avg_30s']:.0f} · 5분 평균 {p['avg_5m']:.0f}")
        self.level_lbl.setText(LEVEL_TEXT[p["level"]])
        tot = s["total_phys"]
        self.cards["ram"].set(f"{fmt_bytes(s['used_phys'])} / {fmt_bytes(tot)}", f"사용 {s['used_phys'] / tot * 100:.0f}%")
        self.cards["comp"].set(fmt_bytes(s["compressed"]),
                               "압축 활성" if s["compression_active"] else "측정 불가(관리자 권한 필요할 수 있음)")
        self.cards["cache"].set(fmt_bytes(s["cached"]), "파일 캐시 — 클수록 좋음")
        self.cards["avail"].set(fmt_bytes(s["avail_phys"]), f"{s['avail_ratio'] * 100:.0f}% (Standby 포함)")
        pf = s["pagefile_total"]
        self.cards["pf"].set(f"{fmt_bytes(s['pagefile_used'])} / {fmt_bytes(pf)}", "추정(CommitLimit−RAM)")
        self.cards["commit"].set(f"{fmt_bytes(s['commit_total'])} / {fmt_bytes(s['commit_limit'])}",
                                 f"{s['commit_ratio'] * 100:.0f}% 사용")
        self.cards["hf"].set(f"{s['hard_faults_per_sec']:.0f}", "Pages Input/sec 근사 (페이지 수)")
        self.cards["stand"].set(fmt_bytes(s["standby"]), f"Modified {fmt_bytes(s['modified'])}")

        rows = st["top_processes"]
        self.table.setRowCount(len(rows))
        for r, d in enumerate(rows):
            name = d["name"].removesuffix(".exe")
            status = d["protected"] or d["category"]
            for c, txt in enumerate([name, str(d["count"]), fmt_bytes(d["ws"]), status]):
                it = QTableWidgetItem(txt)
                it.setToolTip(txt)
                if c in (1, 2):
                    it.setTextAlignment(Qt.AlignRight | Qt.AlignVCenter)
                self.table.setItem(r, c, it)
        acts = st.get("last_actions", [])
        if acts:
            self.action_lbl.setText("최근 조치: " + " | ".join(a["msg"] for a in acts[:2]))
        self.dry_chk.blockSignals(True)
        self.dry_chk.setChecked(st["dry_run"])
        self.dry_chk.blockSignals(False)
        self.mode_lbl.setText(("서비스 연결됨" if self.backend.mode == "service" else "내장 모드(서비스 미실행)")
                              + (" · 관리자" if st["admin"] else " · 일반 권한"))
        crit = st.get("advice", {}).get("critical")
        if crit and crit["ts"] != self._last_critical_ts and p["level"] >= 4:
            self._last_critical_ts = crit["ts"]
            self._crit = crit["candidates"]
            self.banner.setText("⚠ 메모리 부족 임박 — 종료 후보 보기 (자동 종료하지 않습니다)")
            self.banner.show()
        elif p["level"] < 4:
            self.banner.hide()
        self.state_changed.emit(st)
        if self.stack.currentIndex() == 3 and self.isVisible():
            self._refresh_log()

    def _show_critical(self) -> None:
        CriticalDialog(self, self.backend, getattr(self, "_crit", [])).exec()

    def _toggle_dry(self, on: bool) -> None:
        if not on and QMessageBox.question(
                self, "Dry Run 해제", "Dry Run을 끄면 정책이 실제로 Working Set/Memory Priority를 변경합니다.\n"
                "계속할까요?") != QMessageBox.Yes:
            self.dry_chk.blockSignals(True)
            self.dry_chk.setChecked(True)
            self.dry_chk.blockSignals(False)
            return
        self._settings_call({"dry_run": on})

    def _settings_call(self, settings):
        try:
            self.backend.call("set_settings", settings=settings)
        except Exception as e:
            QMessageBox.warning(self, "설정 변경 실패", str(e))
        self.poll()

    def _set_workload(self, _):
        self._settings_call({"workload_mode": self.workload_combo.currentData()})

    def _page_resources(self):
        w = QWidget()
        lay = QVBoxLayout(w)
        lay.setContentsMargins(24, 20, 24, 16)
        lay.addWidget(_title("작업 그룹 / 프로세스", "현재 작업 그룹과 보호 사유를 확인하세요. 실행 중인 컴파일·AI·미디어·다운로드는 보호합니다."))
        self.groups_lbl = QLabel("")
        self.groups_lbl.setWordWrap(True)
        lay.addWidget(self.groups_lbl)
        self.process_table = QTableWidget(0, 5)
        self.process_table.setHorizontalHeaderLabels(["PID", "프로세스", "Working Set", "활동", "보호 사유"])
        self.process_table.horizontalHeader().setSectionResizeMode(4, QHeaderView.Stretch)
        self.process_table.setEditTriggers(QTableWidget.NoEditTriggers)
        self.process_table.verticalHeader().hide()
        lay.addWidget(self.process_table, 1)
        return w

    def _update_resources(self, st):
        self.groups_lbl.setText("   |   ".join(f"{g['label']}: {g['count']}개 / {fmt_bytes(g['ws'])} / 보호 {g['protected']}개"
                                               for g in st.get("resource_groups", [])))
        rows = sorted(st.get("processes", []), key=lambda p: p["ws"], reverse=True)[:200]
        self.process_table.setRowCount(len(rows))
        for i, p in enumerate(rows):
            for j, value in enumerate((str(p["pid"]), p["name"], fmt_bytes(p["ws"]),
                                        f"{p['category']} ({p['inactive_sec'] / 60:.0f}분)", p["protected"] or "—")):
                self.process_table.setItem(i, j, QTableWidgetItem(value))

    def _page_storage(self):
        w = QWidget()
        lay = QVBoxLayout(w)
        lay.setContentsMargins(24, 20, 24, 16)
        lay.addWidget(_title("모델 저장소", "선택한 폴더의 GGUF / Safetensors / ONNX 모델을 분석합니다. HOT·WARM·COLD는 파일 시각 기반 추정입니다."))
        button = QPushButton("폴더 선택 및 분석")
        button.clicked.connect(self._scan_storage)
        lay.addWidget(button, 0, Qt.AlignLeft)
        self.storage_summary = QLabel("모델 폴더를 선택하세요. 파일 분석은 읽기 전용입니다.")
        self.storage_summary.setWordWrap(True)
        lay.addWidget(self.storage_summary)
        self.storage_table = QTableWidget(0, 4)
        self.storage_table.setHorizontalHeaderLabels(["모델", "크기", "상태", "경로"])
        self.storage_table.horizontalHeader().setSectionResizeMode(3, QHeaderView.Stretch)
        self.storage_table.setEditTriggers(QTableWidget.NoEditTriggers)
        self.storage_table.verticalHeader().hide()
        lay.addWidget(self.storage_table, 1)
        return w

    def _scan_storage(self):
        folder = QFileDialog.getExistingDirectory(self, "모델 폴더 선택")
        if not folder:
            return
        self.storage_summary.setText("분석 중…")
        def done(result, error):
            if error:
                self.storage_summary.setText(error)
                return
            self.storage_summary.setText(f"{len(result['models'])}개 · 총 {fmt_bytes(result['total_bytes'])} · COLD {fmt_bytes(result['cold_bytes'])}\n"
                                         + result["note"] + ("\n파일 수 제한으로 일부만 분석했습니다." if result["truncated"] else ""))
            self.storage_table.setRowCount(len(result["models"]))
            for i, m in enumerate(result["models"]):
                for j, value in enumerate((m["name"], fmt_bytes(m["size"]), m["state"], m["path"])):
                    self.storage_table.setItem(i, j, QTableWidgetItem(value))
        self._run(lambda: self.backend.call("scan_models", folder=folder), done)

    # ------------------------------------------------------------ AI/LLM
    def _page_ai(self) -> QWidget:
        w = QWidget()
        lay = QVBoxLayout(w)
        lay.setContentsMargins(24, 20, 24, 16)
        lay.addWidget(_title("AI / LLM 모드", "로컬 LLM·Stable Diffusion 실행 전에 메모리 부족 가능성을 예측하고, "
                             "대상 프로세스는 건드리지 않은 채 다른 앱의 메모리를 일찍 줄입니다."))
        self.ai_chk = QCheckBox("AI 모드 사용")
        self.ai_chk.toggled.connect(lambda v: self.backend.call("set_settings", settings={"ai_mode": v}))
        lay.addWidget(self.ai_chk)
        form = QFormLayout()
        self.model_gb = QDoubleSpinBox()
        self.model_gb.setRange(0.1, 512)
        self.model_gb.setValue(6.5)
        self.model_gb.setSuffix(" GB")
        self.extra_gb = QDoubleSpinBox()
        self.extra_gb.setRange(0, 256)
        self.extra_gb.setSuffix(" GB")
        form.addRow("모델 파일 크기", self.model_gb)
        form.addRow("추가 메모리(컨텍스트 등)", self.extra_gb)
        lay.addLayout(form)
        b = QPushButton("실행 가능성 평가")
        b.setObjectName("accent")
        b.clicked.connect(self._check_llm)
        lay.addWidget(b, 0, Qt.AlignLeft)
        self.llm_out = QPlainTextEdit()
        self.llm_out.setReadOnly(True)
        self.llm_out.setMaximumHeight(210)
        lay.addWidget(self.llm_out)
        lay.addWidget(QLabel("감지된 AI/LLM 프로세스"))
        self.ai_table = QTableWidget(0, 4)
        self.ai_table.setHorizontalHeaderLabels(["프로세스", "PID", "메모리", "모델"])
        self.ai_table.horizontalHeader().setSectionResizeMode(0, QHeaderView.Stretch)
        self.ai_table.verticalHeader().hide()
        lay.addWidget(self.ai_table, 1)
        self.state_changed.connect(self._update_ai_table)
        return w

    def _update_ai_table(self, st: dict) -> None:
        ai = st.get("ai_processes", [])
        self.ai_table.setRowCount(len(ai))
        for r, a in enumerate(ai):
            vals = [a["name"], str(a["pid"]), fmt_bytes(a["ws"]),
                    fmt_bytes(a["model_bytes"]) if a["model_bytes"] else "-"]
            for c, v in enumerate(vals):
                self.ai_table.setItem(r, c, QTableWidgetItem(v))

    def _check_llm(self) -> None:
        try:
            r = self.backend.call("check_llm", model_gb=self.model_gb.value(), extra_gb=self.extra_gb.value())
        except Exception as e:
            self.llm_out.setPlainText(f"오류: {e}")
            return
        txt = r["text"] + "\n\n" + "\n".join("• " + n for n in r["notes"])
        txt += f"\n\nPagefile 의존 추정: {r['swap_dependency'] * 100:.0f}%"
        self.llm_out.setPlainText(txt)

    # ------------------------------------------------------------ Pagefile
    def _page_pagefile(self) -> QWidget:
        w = QWidget()
        lay = QVBoxLayout(w)
        lay.setContentsMargins(24, 20, 24, 16)
        lay.addWidget(_title("Pagefile 관리", "Windows 자동 관리의 확장은 OS에 맡깁니다. 이 앱에서 변경한 설정은 재부팅이 필요할 수 있습니다. "
                             "그래서 사용 패턴 기반 '권장값'을 계산하고, 사용자가 승인할 때만 적용합니다."))
        row = QHBoxLayout()
        self.pf_mode = QComboBox()
        for m in MODES:
            self.pf_mode.addItem(MODE_LABELS[m], m)
        self.pf_mode.setCurrentIndex(1)
        row.addWidget(QLabel("모드"))
        row.addWidget(self.pf_mode)
        b = QPushButton("권장값 계산")
        b.setObjectName("accent")
        b.clicked.connect(self._pf_recommend)
        row.addWidget(b)
        a = QPushButton("권장값 적용…")
        a.setObjectName("danger")
        a.clicked.connect(self._pf_apply)
        row.addWidget(a)
        restore = QPushButton("이전 설정 복원…")
        restore.clicked.connect(self._pf_restore)
        row.addWidget(restore)
        row.addStretch(1)
        lay.addLayout(row)
        self.pf_out = QPlainTextEdit()
        self.pf_out.setReadOnly(True)
        lay.addWidget(self.pf_out, 1)
        return w

    def _pf_recommend(self) -> None:
        mode = self.pf_mode.currentData()
        try:
            cfg = self.backend.call("pagefile_config")
            rec = self.backend.call("recommend_pagefile", mode=mode)
        except Exception as e:
            self.pf_out.setPlainText(f"오류: {e}")
            return
        lines = ["[현재 설정]", json.dumps(cfg["config"], ensure_ascii=False),
                 "디스크: " + ", ".join(f"{d['drive']} {d['kind']} 여유 {fmt_bytes(d['free'])}" for d in cfg["disks"])
                 if cfg["disks"] else "디스크 정보 수집 중... 잠시 후 다시 시도하세요.", "", "[권장]"]
        if rec["managed_by_windows"]:
            lines.append("Windows 자동 관리 유지")
        else:
            lines.append(f"드라이브 {rec['drive']} ({rec['disk_kind']}) · 초기 {rec['initial_mb'] // 1024}GB · "
                         f"최대 {rec['max_mb'] // 1024}GB")
        lines += ["", "[근거]"] + ["• " + r for r in rec["rationale"]]
        self.pf_out.setPlainText("\n".join(lines))

    def _pf_apply(self) -> None:
        mode = self.pf_mode.currentData()
        dry = self._state.get("dry_run", True)
        msg = ("Dry Run 상태입니다. 시뮬레이션만 수행합니다." if dry else
               "관리자 권한으로 pagefile 설정을 변경합니다. 재부팅 후 적용되며 이전 설정은 백업됩니다.")
        if QMessageBox.question(self, "Pagefile 변경", msg + "\n계속할까요?") != QMessageBox.Yes:
            return
        self._run(lambda: self.backend.call("apply_pagefile", mode=mode),
                  lambda r, e: self.pf_out.appendPlainText("\n" + (e or r["message"])))

    def _pf_restore(self):
        if QMessageBox.question(self, "Pagefile 복원", "RamFlow 최초 변경 전 설정으로 복원할까요? 재부팅이 필요할 수 있습니다.") != QMessageBox.Yes:
            return
        self._run(lambda: self.backend.call("restore_pagefile"),
                  lambda r, e: self.pf_out.appendPlainText("\n" + (e or r["message"])))

    # ------------------------------------------------------------ 로그
    def _page_log(self) -> QWidget:
        w = QWidget()
        lay = QVBoxLayout(w)
        lay.setContentsMargins(24, 20, 24, 16)
        lay.addWidget(_title("이벤트 로그", "Memory Pressure 변화, Working Set 최적화, Pagefile, Hard Fault 급증, LLM 감지, 경고"))
        self.log_view = QPlainTextEdit()
        self.log_view.setReadOnly(True)
        self.log_view.setMaximumBlockCount(2000)
        lay.addWidget(self.log_view, 1)
        return w

    def _refresh_log(self) -> None:
        try:
            events = self.backend.call("get_events", n=300)["events"]
        except Exception:
            return
        from ..core.eventlog import EventLog
        for e in events:
            if e["seq"] > self._last_event_seq:
                self._last_event_seq = e["seq"]
                self.log_view.appendPlainText(EventLog.format(e) + "\n")

    # ------------------------------------------------------------ 벤치마크
    def _page_bench(self) -> QWidget:
        w = QWidget()
        lay = QVBoxLayout(w)
        lay.setContentsMargins(24, 20, 24, 16)
        lay.addWidget(_title("Benchmark", "측정 → 정책 1회 적용 → 재측정. Dry Run이면 차이는 측정 노이즈입니다. "
                             "결과는 JSON/CSV로 저장됩니다."))
        self.bench_btn = QPushButton("벤치마크 실행 (약 20초)")
        self.bench_btn.setObjectName("accent")
        self.bench_btn.clicked.connect(self._bench)
        lay.addWidget(self.bench_btn, 0, Qt.AlignLeft)
        self.bench_out = QPlainTextEdit()
        self.bench_out.setReadOnly(True)
        lay.addWidget(self.bench_out, 1)
        return w

    def _bench(self) -> None:
        self.bench_btn.setEnabled(False)
        self.bench_out.setPlainText("측정 중... 다른 작업을 하지 말고 잠시 기다려 주세요.")

        def done(r, err):
            self.bench_btn.setEnabled(True)
            if err:
                self.bench_out.setPlainText("오류: " + err)
                return
            lines = [f"Dry Run: {r['dry_run']}", f"저장: {r.get('saved_to', '-')}", "",
                     f"{'항목':<34}{'Before':>14}{'After':>14}"]
            for k, v in r["before"].items():
                if isinstance(v, (int, float)):
                    lines.append(f"{k:<34}{v:>14.1f}{r['after'][k]:>14.1f}")
            lines += ["", "조치:"] + [("  " + a["message"]) for a in r["actions"]]
            self.bench_out.setPlainText("\n".join(lines))
        self._run(lambda: self.backend.call("benchmark", seconds=5), done)

    # ------------------------------------------------------------ 설정
    _BOOLS = [("auto_optimization", "Auto Optimization (정책 엔진 사용)"), ("dry_run", "Dry Run (실제 변경 없이 로그만)"),
              ("ai_mode", "AI Mode"), ("pagefile_recommendation", "Pagefile Recommendation"),
              ("cpu_management", "비활성 앱 CPU Priority 관리"), ("ecoqos", "비활성 앱 EcoQoS 관리"),
              ("startup", "Windows 시작 시 자동 실행"), ("logging", "Logging"),
              ("learning", "사용 패턴 학습 (선택)")]
    _FLOATS = [("threshold_yellow", "Yellow 임계값"), ("threshold_orange", "Orange 임계값"),
               ("threshold_red", "Red 임계값"), ("threshold_critical", "Critical 임계값")]
    _LISTS = [("whitelist", "프로세스 화이트리스트 (절대 건드리지 않음, 쉼표 구분)"),
              ("blacklist", "프로세스 블랙리스트 (압박 시 우선 축소 대상)"),
              ("ai_custom_processes", "AI 대상 프로세스 직접 지정")]

    def _page_settings(self) -> QWidget:
        w = QWidget()
        lay = QVBoxLayout(w)
        lay.setContentsMargins(24, 20, 24, 16)
        lay.addWidget(_title("설정"))
        self.s_bool = {}
        for k, t in self._BOOLS:
            c = QCheckBox(t)
            self.s_bool[k] = c
            lay.addWidget(c)
        form = QFormLayout()
        self.s_float = {}
        for k, t in self._FLOATS:
            d = QDoubleSpinBox()
            d.setRange(5, 100)
            self.s_float[k] = d
            form.addRow(t, d)
        self.s_list = {}
        for k, t in self._LISTS:
            e = QLineEdit()
            self.s_list[k] = e
            form.addRow(t, e)
        lay.addLayout(form)
        b = QPushButton("저장")
        b.setObjectName("accent")
        b.clicked.connect(self._save_settings)
        lay.addWidget(b, 0, Qt.AlignLeft)
        lay.addStretch(1)
        return w

    def _load_settings(self) -> None:
        try:
            s = self.backend.call("get_settings")
        except Exception:
            return
        for k, c in self.s_bool.items():
            c.setChecked(bool(s.get(k)))
        for k, d in self.s_float.items():
            d.setValue(float(s.get(k, 0)))
        for k, e in self.s_list.items():
            e.setText(", ".join(s.get(k, [])))
        self.ai_chk.blockSignals(True)
        self.ai_chk.setChecked(bool(s.get("ai_mode")))
        self.ai_chk.blockSignals(False)

    def _save_settings(self) -> None:
        raw = {k: c.isChecked() for k, c in self.s_bool.items()}
        raw.update({k: d.value() for k, d in self.s_float.items()})
        raw.update({k: [x.strip() for x in e.text().split(",") if x.strip()] for k, e in self.s_list.items()})
        if not raw["dry_run"] and self._state.get("dry_run", True):
            if QMessageBox.question(self, "Dry Run 해제", "실제 시스템 변경이 활성화됩니다. 계속할까요?") != QMessageBox.Yes:
                return
        try:
            self.backend.call("set_settings", settings=raw)
            if not set_startup(raw["startup"]):
                raise RuntimeError("Windows 시작 프로그램 설정에 실패했습니다.")
        except Exception as e:
            QMessageBox.warning(self, "저장 실패", str(e))
            return
        QMessageBox.information(self, "저장", "설정을 저장했습니다.")
