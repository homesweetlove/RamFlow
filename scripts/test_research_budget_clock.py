"""Synthetic actuator delays only; no measured performance or OS setters."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

LAB=Path(__file__).resolve().parent/'ResearchLab/bin/Release/net10.0-windows/ResearchLab.exe'

class ReadbackClock(unittest.TestCase):
    def replay(self,on,delay=.25,restore=.015):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);src=root/'in.json';out=root/'out.json'
            src.write_text(json.dumps(dict(epochs=[dict(selected=i in on,transition_delay=delay if i in on else restore) for i in range(20)],final_restore_delay=restore)))
            p=subprocess.run([str(LAB),'budget-clock-replay','--input',str(src),'--output',str(out)],capture_output=True,text=True)
            self.assertEqual(p.returncode,0,p.stdout+p.stderr);return json.loads(out.read_text())
    def test_transition_cost_does_not_consume_on_credit(self):
        r=self.replay({0,1,2,5,6,10,11,15,16})
        self.assertAlmostEqual(r['actual_on_seconds'],4.515,places=8)
        self.assertLess(r['end_seconds'],12)
    def test_tail_allocation_and_continuous_allocation_have_same_dose(self):
        for on in (set(range(9)),set(range(11,20))):
            self.assertAlmostEqual(self.replay(on)['actual_on_seconds'],4.515,places=8)
    def test_extreme_actuator_cost_is_visible_not_hidden(self):
        self.assertGreater(self.replay({0,1,2,5,6,10,11,15,16},delay=1)['end_seconds'],12)

if __name__=='__main__':unittest.main()
