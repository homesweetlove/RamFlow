"""Real Windows API safety on a tiny process created by this test; no benchmark."""
import subprocess
import sys
import unittest

@unittest.skipUnless(sys.platform=='win32','Windows API required')
class OwnedQosTests(unittest.TestCase):
    def test_identity_mismatch_rejects_before_any_write(self):
        from owned_process_qos import OwnedQos,creation_filetime
        child=subprocess.Popen([sys.executable,'-c','import time;time.sleep(15)'])
        try:
            with self.assertRaises(PermissionError):OwnedQos(child,creation_filetime(child)+1)
        finally:child.terminate();child.wait(timeout=5)

    def test_execution_speed_override_reads_back_and_restores(self):
        from owned_process_qos import OwnedQos,creation_filetime
        child=subprocess.Popen([sys.executable,'-c','import time;time.sleep(15)']);owner=None
        try:
            owner=OwnedQos(child,creation_filetime(child));original=owner.original.copy()
            value=owner.normal();self.assertEqual(1,value['control']&1);self.assertEqual(0,value['state']&1)
            self.assertEqual(original,owner.restore());self.assertGreaterEqual(owner.successful_writes,1);self.assertLessEqual(owner.successful_writes,2)
        finally:
            try:
                if owner is not None:owner.restore()
            finally:child.terminate();child.wait(timeout=5)

if __name__=='__main__':unittest.main()
