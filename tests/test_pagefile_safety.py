import json

import pytest

from winmemoryflow.core.pagefile_manager import PagefileManager, Recommendation


def backup(tmp_path):
    original = {"automatic_managed": False, "system_managed": False,
                "entries": [{"path": r"C:\pagefile.sys", "initial_mb": 2048, "max_mb": 4096}]}
    (tmp_path / "pagefile_backup.json").write_text(json.dumps(original), encoding="utf-8")
    return original


def test_restore_dry_run_executes_nothing(tmp_path):
    backup(tmp_path)
    calls = []
    pm = PagefileManager(tmp_path, runner=lambda cmd: calls.append(cmd) or (0, ""))
    assert pm.restore(dry_run=True)[0]
    assert not calls
    assert (tmp_path / "pagefile_backup.json").exists()


def test_restore_refuses_injected_backup(tmp_path):
    data = backup(tmp_path)
    data["entries"][0]["path"] = "C:\\x'; Start-Process evil; #"
    (tmp_path / "pagefile_backup.json").write_text(json.dumps(data), encoding="utf-8")
    calls = []
    pm = PagefileManager(tmp_path, runner=lambda cmd: calls.append(cmd) or (0, ""))
    assert not pm.restore(dry_run=False)[0]
    assert not calls


def test_existing_backup_is_preserved(tmp_path):
    original = backup(tmp_path)
    pm = PagefileManager(tmp_path, runner=lambda _: (_ for _ in ()).throw(AssertionError("백업 덮어쓰기 금지")))
    assert pm._backup()
    assert json.loads((tmp_path / "pagefile_backup.json").read_text()) == original


def test_invalid_drive_cannot_enter_powershell():
    rec = Recommendation("custom", "C:'; evil; #", "NVMe", 1024, 2048, 0)
    with pytest.raises(ValueError):
        PagefileManager._set_script(rec)
