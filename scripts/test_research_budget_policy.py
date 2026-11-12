"""SYNTHETIC policy replay tests; no Windows process resource writes."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

LAB=Path(__file__).resolve().parent/'ResearchLab/bin/Release/net10.0-windows/ResearchLab.exe'

class BudgetPolicyTests(unittest.TestCase):
    def replay(self,mode='feedback',ratio=2,stale=False,protected=False,count=20):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);source=root/'input.json';output=root/'output.json'
            source.write_text(json.dumps(dict(label='SYNTHETIC UNIT DATA',mode=mode,baseline={'completion':50,'save':20},baseline_count=count,protected=protected,
                epochs=[dict(epoch=i,time=i*.5,observations=None if stale and i>0 else {'completion':50*ratio,'save':20*ratio}) for i in range(20)])))
            r=subprocess.run([str(LAB),'budget-replay','--input',str(source),'--output',str(output)],capture_output=True,text=True,timeout=10)
            return r,json.loads(output.read_text()) if output.exists() else None

    def test_same_quota_different_placement(self):
        f,periodic=self.replay('fixed',ratio=.5);r,response=self.replay(ratio=2)
        self.assertEqual(f.returncode,0,f.stderr);self.assertEqual(r.returncode,0,r.stderr)
        self.assertEqual([i for i,x in enumerate(periodic['epochs']) if x['selected']],[0,1,2,5,6,10,11,15,16])
        self.assertEqual(sum(x['selected'] for x in response['epochs']),9)
        self.assertEqual([i for i,x in enumerate(response['epochs']) if x['selected']],list(range(9)))
        self.assertTrue(all(x['applied'] is False for x in response['epochs']))

    def test_low_risk_reserves_equal_dose_for_tail(self):
        r,v=self.replay(ratio=.5);self.assertEqual(r.returncode,0,r.stderr)
        self.assertEqual([i for i,x in enumerate(v['epochs']) if x['selected']],list(range(11,20)))

    def test_missing_observations_override_quota_fail_closed(self):
        r,v=self.replay(stale=True);self.assertEqual(r.returncode,0,r.stderr)
        self.assertFalse(v['quota_complete']);self.assertTrue(all(x['action']==0 for x in v['epochs'][4:]))

    def test_foreground_protection_remains_above_scheduler(self):
        r,v=self.replay(protected=True);self.assertEqual(r.returncode,0,r.stderr)
        self.assertTrue(any(x['selected'] for x in v['epochs']));self.assertTrue(all(x['action']==0 for x in v['epochs']))

    def test_insufficient_calibration_is_rejected(self):
        r,v=self.replay(count=19);self.assertNotEqual(r.returncode,0)
        self.assertIsNone(v)

if __name__=='__main__':unittest.main()
