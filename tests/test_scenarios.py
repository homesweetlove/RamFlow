"""Mock Provider 기반 시나리오 테스트. 실제 메모리를 소모하거나 시스템을 변경하지 않는다."""
import pytest

from winmemoryflow.config.settings import Settings
from winmemoryflow.core import llm_detector
from winmemoryflow.core.engine import Engine
from winmemoryflow.core.eventlog import EventLog
from winmemoryflow.core.mock_provider import MockProvider
from winmemoryflow.core.models import GB, MB, Level, MemorySnapshot
from winmemoryflow.core.pagefile_manager import DiskInfo, PagefileManager
from winmemoryflow.core.pressure_engine import PressureEngine


def run(scn, ticks=30, dry=True, step=60, fg=(903, "notepad.exe"), settings=None, prov=None):
    p = prov or MockProvider(scn, foreground=fg)
    s = settings or Settings(dry_run=dry)
    e = Engine(p, s, log=EventLog(enabled=False), pagefile=PagefileManager())
    st = None
    for _ in range(ticks):
        st = e.tick()
        p.advance(step)
    return p, e, st


def test_normal_does_nothing():
    p, e, st = run("normal", dry=False)
    assert st["pressure"]["level"] == 0
    assert p.trim_calls == [] and p.priority_calls == []


@pytest.mark.parametrize("scn,min_level", [
    ("8gb_90pct", 1), ("16gb_chrome100", 2), ("avail_under_500mb", 3),
    ("commit_95", 2), ("pagefile_shortage", 2), ("hard_fault_spike", 2)])
def test_scenarios_raise_pressure(scn, min_level):
    _, _, st = run(scn, ticks=15)
    assert st["pressure"]["level"] >= min_level, st["pressure"]


def test_dry_run_never_touches_system():
    p, e, _ = run("avail_under_500mb", dry=True, ticks=130)
    assert p.trim_calls == [] and p.priority_calls == [] and p.purge_calls == 0
    msgs = [x["detail"] for x in e.log.recent(500) if x["data"].get("dry_run")]
    assert any("DRY RUN" in m for m in msgs)
    assert any("감소시켰을 것입니다" in m for m in msgs)


def test_real_mode_trims_inactive_background_only():
    p, e, _ = run("avail_under_500mb", dry=False, ticks=130)
    assert p.trim_calls, "압박 상황에서 비활성 프로세스는 축소되어야 함"
    names = {x.name: x.pid for x in p._procs}
    protected = {names["system"], names["svchost.exe"], names["explorer.exe"], names["dwm.exe"], 903}
    assert not (set(p.trim_calls) & protected)


def test_foreground_app_never_trimmed():
    # chrome이 포그라운드 → chrome 전체(자식 포함) 보호
    p, e, _ = run("16gb_chrome100", dry=False, fg=(200, "chrome.exe"))
    chrome_pids = {x.pid for x in p._procs if x.name == "chrome.exe"}
    assert not (set(p.trim_calls) & chrome_pids)
    assert all(pid not in chrome_pids for pid, _ in p.priority_calls)


def test_whitelist_respected():
    s = Settings(dry_run=False, whitelist=["code.exe", "slack"])
    p, e, _ = run("avail_under_500mb", settings=s)
    ids = {x.pid for x in p._procs if x.name in ("code.exe", "slack.exe")}
    assert not (set(p.trim_calls) & ids)


def test_fullscreen_blocks_all_actions():
    prov = MockProvider("avail_under_500mb")
    prov.fullscreen = True
    p, e, _ = run(None, dry=False, prov=prov)
    assert p.trim_calls == [] and p.priority_calls == []


def test_auto_optimization_off():
    p, e, _ = run("avail_under_500mb", settings=Settings(dry_run=False, auto_optimization=False))
    assert p.trim_calls == []


def test_no_trim_spam_cooldown():
    p, e, _ = run("avail_under_500mb", dry=False, ticks=30, step=2)   # 짧은 시간 → 비활성 조건 미충족
    assert p.trim_calls == []
    p, e, _ = run("avail_under_500mb", dry=False, ticks=180, step=60)
    # 2시간 후부터 축소. 이후 쿨다운(15분)·백오프로 제한.
    from collections import Counter
    assert max(Counter(p.trim_calls).values()) <= 4


def test_critical_never_kills_but_recommends():
    prov = MockProvider("avail_under_500mb")
    prov.patch(avail_phys=int(0.25 * GB))
    p, e, st = run(None, ticks=10, prov=prov)
    assert st["pressure"]["level"] == 4
    assert "critical" in st["advice"] and st["advice"]["critical"]["candidates"]


def test_spike_does_not_flip_level():
    eng = PressureEngine()
    p = MockProvider("normal")
    for _ in range(20):
        eng.update(p.sample())
    spike = p.sample()
    spike.avail_phys = int(0.5 * GB)
    spike.hard_faults_per_sec = 4000
    st = eng.update(spike)
    assert st.level == Level.NORMAL     # 단 한 번의 급증은 레벨을 바꾸지 못한다


def test_hysteresis_recovery_is_gradual():
    eng = PressureEngine()
    p = MockProvider("avail_under_500mb")
    p.patch(avail_phys=int(0.25 * GB))
    for _ in range(15):
        eng.update(p.sample())
    assert eng.level == Level.CRITICAL
    p.set_scenario("normal")
    levels = []
    for _ in range(80):
        levels.append(eng.update(p.sample()).level)
    assert levels[0] == Level.CRITICAL and levels[-1] == Level.NORMAL


def test_standby_purge_removed_even_with_legacy_opt_in():
    p0 = MockProvider("hard_fault_spike")
    p0.patch(avail_phys=int(0.6 * GB), commit_total=int(23.5 * GB))
    run(None, dry=False, prov=p0, ticks=8)
    assert p0.purge_calls == 0      # 옵션 OFF면 절대 개입하지 않음
    s = Settings(dry_run=False, allow_standby_purge=True)
    p2 = MockProvider("hard_fault_spike")
    p2.patch(avail_phys=int(0.6 * GB), commit_total=int(23.5 * GB))
    run(None, settings=s, prov=p2, ticks=8)
    assert p2.purge_calls == 0      # 이전 ZIP 설정이 있어도 비공식 API 실행 금지


# ---------------- LLM
def test_llm_example_pagefile_heavy():
    snap = MemorySnapshot(timestamp=0, total_phys=8 * GB, avail_phys=int(4.2 * GB), commit_total=6 * GB,
                          commit_limit=18 * GB)
    f = llm_detector.assess_model(int(6.5 * GB), snap, overhead=1.25)
    assert f.verdict == llm_detector.Verdict.PAGEFILE_HEAVY
    assert "Pagefile 의존도가 높으며" in f.message
    assert 8.0 < f.expected_runtime / GB < 8.2


def test_llm_fail_gives_pagefile_recommendation():
    snap = MemorySnapshot(timestamp=0, total_phys=8 * GB, avail_phys=2 * GB, commit_total=9 * GB,
                          commit_limit=12 * GB)
    f = llm_detector.assess_model(8 * GB, snap)
    assert f.verdict == llm_detector.Verdict.FAIL and f.recommended_pagefile > 0


def test_llm_comfortable():
    snap = MemorySnapshot(timestamp=0, total_phys=32 * GB, avail_phys=20 * GB, commit_total=10 * GB,
                          commit_limit=60 * GB)
    assert llm_detector.assess_model(4 * GB, snap).verdict == llm_detector.Verdict.COMFORTABLE


def test_llm_detection_and_protection():
    from winmemoryflow.core.models import ProcessInfo
    procs = [ProcessInfo(1, "ollama_llama_server.exe", 5 * GB),
             ProcessInfo(2, "python.exe", GB, cmdline=["python", "-c", "import torch"]),
             ProcessInfo(3, "python.exe", GB, cmdline=["python", "app.py"]),
             ProcessInfo(4, "mytool.exe", GB)]
    found = llm_detector.detect(procs, ["mytool"])
    assert {f.pid for f in found} == {1, 2, 4}


def test_ai_mode_protects_target_and_logs_detection():
    p, e, _ = run("llm_loading", dry=False, ticks=20, settings=Settings(dry_run=False, ai_mode=True))
    target = next(x.pid for x in p._procs if x.name == "ollama_llama_server.exe")
    assert target not in p.trim_calls
    assert any(x["kind"] == "llm" for x in e.log.recent(100))


# ---------------- Pagefile
DISKS = [DiskInfo("C:", "SATA SSD", 200 * GB, 500 * GB), DiskInfo("D:", "NVMe", 300 * GB, 1000 * GB),
         DiskInfo("E:", "HDD", 900 * GB, 2000 * GB)]


@pytest.mark.parametrize("ram", [8, 16])
def test_llm_mode_small_ram_gets_16_to_32gb(ram):
    r = PagefileManager().recommend("llm", ram * GB, int(ram * 0.7 * GB), disks=DISKS)
    assert 16 * 1024 <= r.initial_mb <= 32 * 1024
    assert r.drive == "D:" and r.disk_kind == "NVMe"


def test_llm_mode_32gb_is_usage_based():
    low = PagefileManager().recommend("llm", 32 * GB, 10 * GB, disks=DISKS)
    high = PagefileManager().recommend("llm", 32 * GB, 40 * GB, commit_p95=45 * GB, disks=DISKS)
    assert low.initial_mb < high.initial_mb


def test_recommendation_depends_on_usage_not_ram_multiple():
    a = PagefileManager().recommend("balanced", 16 * GB, 8 * GB, disks=DISKS)
    b = PagefileManager().recommend("balanced", 16 * GB, 30 * GB, commit_p95=32 * GB, disks=DISKS)
    assert b.initial_mb > a.initial_mb
    assert a.initial_mb != 32 * 1024


def test_hdd_only_warns_and_no_space_handled():
    r = PagefileManager().recommend("heavy", 16 * GB, 20 * GB, disks=[DiskInfo("E:", "HDD", 500 * GB, 1000 * GB)])
    assert r.drive == "E:" and any("HDD" in x for x in r.rationale)
    r = PagefileManager().recommend("heavy", 16 * GB, 20 * GB, disks=[DiskInfo("C:", "NVMe", 5 * GB, 100 * GB)])
    assert r.drive is None


def test_windows_managed_means_no_change():
    r = PagefileManager().recommend("windows_managed", 8 * GB, 4 * GB)
    assert r.managed_by_windows


def test_pagefile_apply_dry_run_runs_nothing():
    calls = []
    pm = PagefileManager(runner=lambda c: calls.append(c) or (0, ""))
    rec = pm.recommend("llm", 8 * GB, 4 * GB, disks=DISKS)
    import sys
    ok, msg = pm.apply(rec, dry_run=True)
    assert calls == []
    if sys.platform == "win32":
        assert ok and "DRY RUN" in msg


def test_pagefile_shortage_logs_advice():
    p, e, st = run("pagefile_shortage", ticks=20)
    assert any(x["kind"] == "pagefile" for x in e.log.recent(100))
    assert st["advice"].get("pagefile")


def test_settings_validation():
    s = Settings()
    s.update({"dry_run": "yes", "threshold_red": 80, "unknown": 1, "whitelist": ["Chrome.EXE", ""]})
    assert s.dry_run is True and s.threshold_red == 80 and s.whitelist == ["chrome.exe"]
