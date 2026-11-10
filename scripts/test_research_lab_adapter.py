"""Pure configuration checks; no native workload is launched."""
import importlib.util
import pathlib
import sys
import tempfile
import unittest
from unittest.mock import patch, MagicMock
from types import SimpleNamespace
import json

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import research_lab_adapter as adapter
import research_matrix as matrix


class AdapterTests(unittest.TestCase):
    def args(self,root):
        root=pathlib.Path(root)
        return SimpleNamespace(outcome=root/'outcome.json',native_dir=root/'native',lab=root/'lab.exe',model=root/'model.json',
            condition='A',regime='healthy',block='SYNTHETIC',seed='1',seconds=2,background_threads=1,variant='None',allow_high_cpu=False)
    def test_launch_error_preserves_adapter_start_and_stream_files(self):
        with self.temporary() as root, patch.object(adapter.subprocess,'Popen',side_effect=OSError('SYNTHETIC launch denial')):
            self.assertEqual(adapter.run_adapter(self.args(root)),2)
            path=pathlib.Path(root)
            self.assertTrue((path/'adapter.started.json').is_file())
            self.assertTrue((path/'native.stdout.log').is_file() and (path/'native.stderr.log').is_file())
            done=json.loads((path/'adapter.finished.json').read_text())
            self.assertIn('SYNTHETIC',done['error']);self.assertFalse(done['evidence']['ok'])
            self.assertFalse((path/'outcome.json').exists())
    def test_native_success_without_seal_is_rejected(self):
        with self.temporary() as root:
            fake=MagicMock(pid=123);fake.wait.return_value=0
            with patch.object(adapter.subprocess,'Popen',return_value=fake):
                self.assertEqual(adapter.run_adapter(self.args(root)),2)
            self.assertFalse(json.loads((pathlib.Path(root)/'adapter.finished.json').read_text())['evidence']['ok'])
    def temporary(self):
        root = pathlib.Path(__file__).resolve().parents[1] / "work" / "test-lab-adapter"
        root.mkdir(parents=True, exist_ok=True)
        return tempfile.TemporaryDirectory(dir=root)
    def test_all_six_conditions_bind_same_fixture_and_seed_without_shell_commands(self):
        with self.temporary() as root:
            lab = pathlib.Path(root) / "ResearchLab.exe"
            model = pathlib.Path(root) / "frozen.json"
            lab.write_bytes(b"synthetic-config-fixture-not-executed")
            model.write_text("{}", encoding="utf-8")
            config = adapter.make_config(lab, model, seconds=2, blocks=3)
            matrix.validate_config(config)
            for condition, spec in config["conditions"].items():
                self.assertIn("{session_seed}", spec["command"])
                self.assertIn("{evidence_path}", spec["command"])
                self.assertEqual(spec["command"][spec["command"].index("--condition") + 1], condition)
                self.assertEqual(spec["adapter"], "explicit-command")
            self.assertIn("WithoutBackgroundCost", config["conditions"]["F"]["command"])
            self.assertFalse(config["observer"]["enabled"])  # Native independent observer is internal.

    def test_missing_model_and_safety_removal_are_rejected(self):
        with self.assertRaises(ValueError):
            adapter.make_config("missing.exe", "missing.json")
        with self.temporary() as root:
            lab = pathlib.Path(root) / "lab.exe"
            model = pathlib.Path(root) / "model.json"
            lab.touch(); model.touch()
            with self.assertRaises(ValueError):
                adapter.make_config(lab, model, variant="WithoutIdentityProtection")

    def test_high_cpu_requires_explicit_opt_in_and_all_conditions_share_load(self):
        with self.temporary() as root:
            lab, model = pathlib.Path(root) / "lab.exe", pathlib.Path(root) / "model.json"
            lab.touch(); model.touch()
            with self.assertRaises(ValueError):
                adapter.make_config(lab, model, background_threads=12)
            config = adapter.make_config(lab, model, background_threads=12, allow_high_cpu=True)
            matrix.validate_config(config)
            for condition in config["conditions"].values():
                command = condition["command"]
                self.assertEqual(command[command.index("--background-threads") + 1], "12")
                self.assertIn("--allow-high-cpu", command)


if __name__ == "__main__":
    unittest.main()
