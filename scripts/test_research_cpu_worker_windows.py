"""Owned fixture correctness only, not policy performance evidence."""
import json
from pathlib import Path
import subprocess
import sys
import unittest

APP=Path(__file__).resolve().parent/'ResearchLab/bin/Release/net10.0-windows/ResearchLab.exe'

@unittest.skipUnless(sys.platform=='win32' and APP.exists(),'built Windows apphost required')
class CpuWorker(unittest.TestCase):
    def test_arithmetic_completed_unit_matches_independent_golden_vectors(self):
        # Independently computed xorshift64, 1,000,000 steps, unsigned 64-bit.
        for seed,expected in [(1,'9c52790c064cae4d'),(20261111,'bd2eeae7775b742c')]:
            result=subprocess.run([str(APP),'worker','--role','foreground','--mode','cpu-arithmetic','--mib','4','--threads','1','--seed',str(seed)],
                input='{"id":1,"verb":"work"}\n{"id":2,"verb":"stop"}\n',capture_output=True,text=True,timeout=20)
            self.assertEqual(0,result.returncode,result.stderr)
            rows=[json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual(expected,next(r['Checksum'] for r in rows if r['Id']==1))

if __name__=='__main__':unittest.main()
