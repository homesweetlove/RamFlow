"""Read-only English PDH counters. Missing/invalid counters stay null."""
import ctypes as c
import math

class Formatted(c.Structure):
    _fields_=[('status',c.c_ulong),('value',c.c_double)]

class CpuCounters:
    PATHS={
        'processor_time':r'\Processor Information(_Total)\% Processor Time',
        'processor_utility':r'\Processor Information(_Total)\% Processor Utility',
        'processor_performance':r'\Processor Information(_Total)\% Processor Performance',
        'processor_frequency_mhz':r'\Processor Information(_Total)\Processor Frequency',
        'queue_length':r'\System\Processor Queue Length',
    }
    def __init__(self):
        self.dll=c.WinDLL('pdh');self.query=c.c_void_p();self.handles={};self.errors={}
        self.dll.PdhOpenQueryW.argtypes=[c.c_wchar_p,c.c_size_t,c.POINTER(c.c_void_p)]
        self.dll.PdhAddEnglishCounterW.argtypes=[c.c_void_p,c.c_wchar_p,c.c_size_t,c.POINTER(c.c_void_p)]
        self.dll.PdhCollectQueryData.argtypes=[c.c_void_p]
        self.dll.PdhGetFormattedCounterValue.argtypes=[c.c_void_p,c.c_ulong,c.c_void_p,c.POINTER(Formatted)]
        self.dll.PdhCloseQuery.argtypes=[c.c_void_p]
        status=self.dll.PdhOpenQueryW(None,0,c.byref(self.query))
        if status:raise OSError('PdhOpenQuery:'+hex(status&0xffffffff))
        for name,path in self.PATHS.items():
            handle=c.c_void_p();status=self.dll.PdhAddEnglishCounterW(self.query,path,0,c.byref(handle))
            if status:self.errors[name]=status&0xffffffff
            else:self.handles[name]=handle
        self.dll.PdhCollectQueryData(self.query)
    def read(self):
        collected=self.dll.PdhCollectQueryData(self.query);result={}
        for name in self.PATHS:
            value=Formatted();handle=self.handles.get(name)
            status=self.dll.PdhGetFormattedCounterValue(handle,0x200|0x8000,None,c.byref(value)) if handle else self.errors[name]
            valid=not collected and not status and value.status<=1 and math.isfinite(value.value)
            result[name]=dict(value=value.value if valid else None,status=status&0xffffffff,cstatus=value.status if handle else None)
        return result
    def close(self):
        if self.query:self.dll.PdhCloseQuery(self.query);self.query=None
