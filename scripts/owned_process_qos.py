"""Execution-speed diagnostic writes restricted to a Popen-created process handle.
Caller must journal original/intent durably before normal() and restore in finally.
No priority, affinity, memory priority, power plan or other process is changed.
"""
import ctypes as c
import subprocess

class Throttle(c.Structure):_fields_=[('version',c.c_ulong),('control',c.c_ulong),('state',c.c_ulong)]

def api():
    k=c.WinDLL('kernel32',use_last_error=True)
    k.GetProcessTimes.argtypes=[c.c_void_p,c.POINTER(c.c_ulonglong),c.POINTER(c.c_ulonglong),c.POINTER(c.c_ulonglong),c.POINTER(c.c_ulonglong)]
    k.GetProcessInformation.argtypes=[c.c_void_p,c.c_int,c.c_void_p,c.c_ulong]
    k.SetProcessInformation.argtypes=[c.c_void_p,c.c_int,c.c_void_p,c.c_ulong]
    return k

def creation_filetime(process):
    if not isinstance(process,subprocess.Popen):raise TypeError('A Popen-owned process handle is required')
    values=[c.c_ulonglong() for _ in range(4)]
    if not api().GetProcessTimes(int(process._handle),*(c.byref(v) for v in values)):raise c.WinError(c.get_last_error())
    return values[0].value

class OwnedQos:
    def __init__(self,process,expected_creation):
        self.process=process;self.identity=expected_creation;self.successful_writes=0;self.write_attempts=0;self._check();self.original=self.read()
    def _check(self):
        if self.process.poll() is not None or creation_filetime(self.process)!=self.identity:raise PermissionError('Owned PID creation identity unavailable or changed; no write')
    def read(self):
        self._check();value=Throttle(1,0,0)
        if not api().GetProcessInformation(int(self.process._handle),4,c.byref(value),c.sizeof(value)):raise c.WinError(c.get_last_error())
        return dict(version=value.version,control=value.control,state=value.state)
    def _write(self,requested):
        self._check();value=Throttle(requested['version'],requested['control'],requested['state']);self.write_attempts+=1
        if not api().SetProcessInformation(int(self.process._handle),4,c.byref(value),c.sizeof(value)):raise c.WinError(c.get_last_error())
        self.successful_writes+=1;observed=self.read()
        if observed!=requested:raise OSError('OS QoS readback mismatch')
        return observed
    def normal(self):return self._write(dict(version=1,control=self.original['control']|1,state=self.original['state']&~1))
    def restore(self):
        return self.read() if self.read()==self.original else self._write(self.original)
