"""실제 Windows 프로세스를 건드리지 않는 회귀 테스트."""
import json
import os
import time

import pytest

from winmemoryflow.config.settings import Settings
from winmemoryflow.core.engine import Engine
from winmemoryflow.core.eventlog import EventLog
from winmemoryflow.core.mock_provider import MockProvider
from winmemoryflow.core.models import GB, MB, ProcessInfo
from winmemoryflow.core.policy import PlannedAction
from winmemoryflow.core.storage import scan_models
from winmemoryflow.ipc.pipe import _read_message, authorize
from winmemoryflow.service.commands import dispatch


def busy_engine(dry=False):
    p = MockProvider("avail_under_500mb")
    p._procs.append(ProcessInfo(999, "idle-app.exe", 500 * MB, 500 * MB, create_time=12))
    e = Engine(p, Settings(dry_run=dry), log=EventLog(enabled=False))
    for _ in range(35):
        e.tick()
        p.advance(60)
    return p, e


def test_original_memory_cpu_and_ecoqos_restored_on_pause():
    p, e = busy_engine()
    assert p.memory_priorities[999] < 5
    assert p.cpu_priorities[999] == 0x4000
    assert p.power_states[999] == (1, 1)
    e.apply_settings({"auto_optimization": False})
    assert p.memory_priorities[999] == 5
    assert p.cpu_priorities[999] == 0x20
    assert p.power_states[999] == (0, 0)
    assert not e.executor.originals


def test_foreground_fast_restore_without_waiting_for_scan():
    p, e = busy_engine()
    p.fg = (999, "idle-app.exe")
    e._restore_foreground()
    assert p.memory_priorities[999] == 5
    assert p.cpu_priorities[999] == 0x20
    assert p.power_states[999] == (0, 0)


def test_stop_restores_every_actual_change():
    p, e = busy_engine()
    e.stop()
    assert not e.executor.originals
    assert p.memory_priorities[999] == 5


def test_dry_run_to_real_replans_without_simulated_state():
    p, e = busy_engine(dry=True)
    assert not p.priority_calls and not p.cpu_calls and not p.power_calls
    assert not e.executor.originals
    e.apply_settings({"dry_run": False})
    e.tick()
    assert p.memory_priorities[999] < 5
    assert p.cpu_priorities[999] == 0x4000
    e.stop()


def test_switch_to_dry_run_restores_existing_changes():
    p, e = busy_engine()
    e.apply_settings({"dry_run": True})
    assert p.memory_priorities[999] == 5
    assert not e.executor.originals
    calls = list(p.priority_calls)
    e.tick()
    assert p.priority_calls == calls


def test_restores_exact_original_not_assumed_normal():
    p = MockProvider()
    p._procs.append(ProcessInfo(999, "idle-app.exe", MB, MB, create_time=12))
    p.memory_priorities[999] = 4
    p.power_states[999] = (2, 2)
    e = Engine(p)
    actions = [PlannedAction("set_priority", 999, value=3, extra={"identity": 12}),
               PlannedAction("set_ecoqos", 999, value=1, extra={"identity": 12})]
    assert all(r.success for r in e.executor.execute(actions, p.now, False))
    e.stop()
    assert p.memory_priorities[999] == 4
    assert p.power_states[999] == (2, 2)


def test_pid_reuse_never_restores_onto_replacement():
    p, e = busy_engine()
    p._procs = [x for x in p._procs if x.pid != 999]
    p._procs.append(ProcessInfo(999, "replacement.exe", MB, MB, create_time=99))
    before = tuple(sum(pid == 999 for pid, _ in calls) for calls in (p.priority_calls, p.cpu_calls, p.power_calls))
    e.stop()
    assert before == tuple(sum(pid == 999 for pid, _ in calls) for calls in (p.priority_calls, p.cpu_calls, p.power_calls))


def test_stale_action_rechecks_current_foreground():
    p, e = busy_engine(dry=True)
    p.fg = (999, "idle-app.exe")
    result = e.executor.execute([PlannedAction("set_priority", 999, value=1, extra={"identity": 12})], p.now, False)
    assert not result[0].success and not p.priority_calls


def test_workload_group_protection():
    p, e = busy_engine()
    e.apply_settings({"workload_mode": "developer"})
    st = e.tick()
    code = next(x for x in st["processes"] if x["name"] == "code.exe")
    assert code["protected"] == "현재 작업 그룹 보호"
    assert st["workload"] == "developer"
    assert any(x["active"] for x in st["resource_groups"])


@pytest.mark.parametrize("name", ["spotify.exe", "ffmpeg.exe", "rustc.exe", "node.exe", "qbittorrent.exe", "python.exe"])
def test_background_active_work_is_protected(name):
    p, e = busy_engine(dry=True)
    p._procs.append(ProcessInfo(1234, name, 500 * MB, 500 * MB))
    e.tick()
    p.advance(8000)
    st = e.tick()
    assert next(x for x in st["processes"] if x["pid"] == 1234)["protected"]


def test_unknown_activity_is_protected():
    p, e = busy_engine(dry=True)
    p._procs.append(ProcessInfo(1234, "unknown.exe", MB, MB, activity_known=False))
    st = e.tick()
    # 다음 스캔을 확실하게 수행한다.
    p.advance(20)
    st = e.tick()
    assert next(x for x in st["processes"] if x["pid"] == 1234)["protected"]


@pytest.mark.parametrize("raw", [{"sample_interval_sec": 0}, {"threshold_red": 10},
                                  {"trim_cooldown_min": -1}, {"ai_model_size_gb": float("nan")},
                                  {"workload_mode": "realtime"}, {"pagefile_mode": "invalid"}])
def test_invalid_settings_atomic(raw):
    settings = Settings()
    original = settings.to_dict()
    with pytest.raises(ValueError):
        settings.update(raw)
    assert original == settings.to_dict()


def test_settings_atomic_persistence(tmp_path):
    path = tmp_path / "config.json"
    s = Settings(workload_mode="developer")
    s.save(path)
    assert Settings.load(path).workload_mode == "developer"
    path.write_text('{broken', encoding="utf-8")
    assert Settings.load(path).dry_run


def test_ipc_privileged_commands_require_elevated_client():
    for cmd, args in [("terminate", {"pid": 999}), ("apply_pagefile", {}),
                       ("restore_pagefile", {}), ("set_settings", {"settings": {"dry_run": False}})]:
        with pytest.raises(PermissionError):
            authorize(cmd, args, administrator=False)
        authorize(cmd, args, administrator=True)
    authorize("get_state", {}, False)
    authorize("set_settings", {"settings": {"dry_run": True}}, False)


def test_ipc_limits_final_chunk_too():
    class Files:
        @staticmethod
        def ReadFile(handle, count):
            return 0, b"x" * (1024 * 1024 + 1)
    with pytest.raises(ValueError):
        _read_message(Files, None)


def test_nonadmin_cannot_resume_or_enable_live_controls():
    p, e = busy_engine()
    e.apply_settings({"auto_optimization": False})
    original = e.settings.to_dict()
    with pytest.raises(PermissionError):
        dispatch(e, "set_settings", {"settings": {"auto_optimization": True}}, administrator=False)
    assert e.settings.to_dict() == original
    dispatch(e, "set_settings", {"settings": {"auto_optimization": True}}, administrator=True)
    with pytest.raises(PermissionError):
        dispatch(e, "set_settings", {"settings": {"ecoqos": True}}, administrator=False)
    dispatch(e, "set_settings", {"settings": {"auto_optimization": False}}, administrator=False)
    dispatch(e, "set_settings", {"settings": {"dry_run": True}}, administrator=False)
    dispatch(e, "set_settings", {"settings": {"auto_optimization": True}}, administrator=False)


def test_failed_restore_retried_after_disabling_policy(monkeypatch):
    p, e = busy_engine()
    setter = p.set_cpu_priority
    attempts = []
    def fail_once(pid, value):
        if pid == 999 and value == 0x20 and not attempts:
            attempts.append(pid)
            return False
        return setter(pid, value)
    monkeypatch.setattr(p, "set_cpu_priority", fail_once)
    e.apply_settings({"cpu_management": False})
    assert p.cpu_priorities[999] == 0x4000
    assert ("set_cpu_priority", 999) in e.executor.pending_restores
    e.tick()
    assert p.cpu_priorities[999] == 0x20
    assert ("set_cpu_priority", 999) not in e.executor.originals
    assert not e.executor.pending_restores


@pytest.mark.parametrize("gap", [False, True])
def test_reused_pid_captures_and_restores_replacement_originals(gap):
    p, e = busy_engine()
    p._procs = [x for x in p._procs if x.pid != 999]
    if gap:
        p.advance(20)
        e.tick()
    p.memory_priorities.pop(999, None)
    p.cpu_priorities.pop(999, None)
    p.power_states.pop(999, None)
    p._procs.append(ProcessInfo(999, "replacement.exe", 500 * MB, 500 * MB, create_time=99))
    for _ in range(35):
        p.advance(60)
        e.tick()
    assert p.memory_priorities[999] < 5
    assert p.cpu_priorities[999] == 0x4000
    assert p.power_states[999] == (1, 1)
    assert all(identity == 99 for (kind, pid), (identity, _) in e.executor.originals.items() if pid == 999)
    e.stop()
    assert p.memory_priorities[999] == 5
    assert p.cpu_priorities[999] == 0x20
    assert p.power_states[999] == (0, 0)


def test_temporary_identity_failure_keeps_restore_records(monkeypatch):
    p, e = busy_engine()
    identity = p.process_identity
    monkeypatch.setattr(p, "process_identity", lambda pid: None if pid == 999 else identity(pid))
    e.tick()
    assert ("set_cpu_priority", 999) in e.executor.originals
    e.apply_settings({"cpu_management": False})
    assert ("set_cpu_priority", 999) in e.executor.pending_restores
    assert p.cpu_priorities[999] == 0x4000
    monkeypatch.setattr(p, "process_identity", identity)
    e.tick()
    assert p.cpu_priorities[999] == 0x20
    assert not e.executor.pending_restores
    e.stop()


def test_command_rejects_nan_and_negative_inputs():
    _, e = busy_engine(dry=True)
    for cmd, args in [("get_events", {"n": -1}), ("check_llm", {"model_gb": float("nan")}),
                       ("recommend_pagefile", {"mode": "injected"}), ("benchmark", {"seconds": -1})]:
        with pytest.raises(ValueError):
            dispatch(e, cmd, args)


def test_model_scan_readonly_and_states(tmp_path):
    now = time.time()
    for name, days in [("hot.gguf", 1), ("warm.onnx", 15), ("cold.safetensors", 60)]:
        p = tmp_path / name
        p.write_bytes(b"model")
        os.utime(p, (now - days * 86400, now - days * 86400))
    (tmp_path / "not-a-model.txt").write_text("skip")
    report = scan_models(str(tmp_path), now=now)
    assert {m["state"] for m in report["models"]} == {"HOT", "WARM", "COLD"}
    assert report["cold_bytes"] == 5
    assert len(list(tmp_path.iterdir())) == 4
    assert scan_models(str(tmp_path), max_files=1)["truncated"]


def test_healthy_manual_optimization_does_not_force_pressure():
    p = MockProvider("normal")
    e = Engine(p, Settings(dry_run=False))
    e.tick()
    p.advance(8000)
    e.tick()
    e.optimize_now()
    assert not p.priority_calls and not p.trim_calls


def test_termination_honors_dry_run(monkeypatch):
    p, e = busy_engine(dry=True)
    def unexpected(*args):
        raise AssertionError("Dry Run에서 실제 종료 API를 호출하면 안 됩니다")
    monkeypatch.setattr("winmemoryflow.core.engine.psutil.Process", unexpected)
    ok, message = e.terminate_process(999)
    assert ok and "DRY RUN" in message
