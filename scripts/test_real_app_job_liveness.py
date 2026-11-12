"""Mocked Win32 handle states; no app launch or resource setter."""
import sys
import unittest
from unittest.mock import patch

@unittest.skipUnless(sys.platform=='win32','Win32 driver import required')
class JobLiveness(unittest.TestCase):
    def verify(self,wait,in_job=True):
        from real_apps import driver
        app=object.__new__(driver.OwnedApp);app.job=999
        def membership(handle,job,answer):answer._obj.value=in_job;return 1
        with patch.object(driver.K,'OpenProcess',return_value=123),patch.object(driver.K,'IsProcessInJob',side_effect=membership),patch.object(driver.K,'WaitForSingleObject',return_value=wait),patch.object(driver.K,'CloseHandle') as close:
            try:return app.owns(987654)
            finally:close.assert_called_once_with(123)
    def test_signaled_retained_pid_is_exited(self):self.assertFalse(self.verify(0))
    def test_nonsignaled_owned_pid_is_alive(self):self.assertTrue(self.verify(0x102))
    def test_unowned_process_is_never_selected(self):self.assertFalse(self.verify(0x102,False))
    def test_wait_failure_cannot_claim_cleanup(self):
        with self.assertRaises(OSError):self.verify(0xffffffff)

if __name__=='__main__':unittest.main()
