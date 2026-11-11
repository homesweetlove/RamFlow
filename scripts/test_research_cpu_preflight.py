"""SYNTHETIC admission-rule examples, not CPU measurements."""
import unittest
import research_cpu_preflight as diagnostic
import tempfile
import subprocess
import sys
import json
import hashlib
from pathlib import Path

class SustainedAdmission(unittest.TestCase):
    def test_short_burst_does_not_admit_a_comparison(self):
        rule=diagnostic.pressure_stable
        self.assertFalse(rule([93.7,96.7,81.1,31.7,28.1,34.5,31.5,31.2]))
    def test_sustained_pressure_is_required(self):
        rule=diagnostic.pressure_stable
        self.assertTrue(rule([82,88,92,90,87,85,86,89]))
        self.assertFalse(rule([95,95,95]))
    def test_invalid_or_collapsing_observations_fail_closed(self):
        self.assertFalse(diagnostic.pressure_stable([90]*5+[float('nan')]))
        self.assertFalse(diagnostic.pressure_stable([90]*20+[40,40,40]))

    def test_new_plan_requires_sustained_pressure_evidence(self):
        repo=Path(__file__).resolve().parents[1]
        with tempfile.TemporaryDirectory(dir=repo/'work') as folder:
            output=Path(folder)/'not-admitted'
            result=subprocess.run([sys.executable,str(repo/'scripts/research_app_pilot.py'),'--plan','--output',str(output)],capture_output=True,text=True)
            self.assertNotEqual(0,result.returncode)
            self.assertFalse(output.exists())

    def test_admission_binds_the_completed_diagnostic_to_the_binary(self):
        from research_app_pilot import verify_preflight
        repo=Path(__file__).resolve().parents[1]
        with tempfile.TemporaryDirectory(dir=repo/'work') as folder:
            root=Path(folder);lab=root/'SYNTHETIC.exe';lab.with_suffix('.dll').write_bytes(b'SYNTHETIC FORMAT FIXTURE')
            started=dict(admission_version='cpu-preflight-v2-sustained',threads=14,workload='xorshift64-1000000-v1',controller_binary_sha256=hashlib.sha256(lab.with_suffix('.dll').read_bytes()).hexdigest())
            result=dict(passed=True,cleanup=True,samples=[dict(phase='Contention',cpu_percent=90) for _ in range(8)])
            (root/'started.json').write_text(json.dumps(started));(root/'result.json').write_text(json.dumps(result))
            self.assertEqual(14,verify_preflight(root,lab,14)['threads'])
            lab.with_suffix('.dll').write_bytes(b'CHANGED SYNTHETIC BINARY')
            with self.assertRaises(ValueError):verify_preflight(root,lab,14)

if __name__=='__main__':unittest.main()
