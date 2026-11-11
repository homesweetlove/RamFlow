"""Owned, visible real-app adapters. stdout is a JSON protocol, stderr diagnostics.
Profiles/workspaces are new and retained outside sealed evidence. No personal profile is read.
Windows job owns suspended-created process tree, including cleanup after driver failure.
"""
import argparse
import ctypes as c
from ctypes import wintypes as w
import functools
import hashlib
import http.server
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import threading
import time
import urllib.request

import psutil
import websocket

K=c.WinDLL('kernel32',use_last_error=True);U=c.WinDLL('user32',use_last_error=True)
class STARTUPINFO(c.Structure):
    _fields_=[('cb',w.DWORD),('reserved',w.LPWSTR),('desktop',w.LPWSTR),('title',w.LPWSTR),('x',w.DWORD),('y',w.DWORD),('xs',w.DWORD),('ys',w.DWORD),('xc',w.DWORD),('yc',w.DWORD),('fill',w.DWORD),('flags',w.DWORD),('show',w.WORD),('r2',w.WORD),('lp2',c.POINTER(c.c_byte)),('stdin',w.HANDLE),('stdout',w.HANDLE),('stderr',w.HANDLE)]
class PROCESSINFO(c.Structure):_fields_=[('process',w.HANDLE),('thread',w.HANDLE),('pid',w.DWORD),('tid',w.DWORD)]
class BASICLIMIT(c.Structure):
    _fields_=[('process_time',c.c_longlong),('job_time',c.c_longlong),('flags',w.DWORD),('minws',c.c_size_t),('maxws',c.c_size_t),('active',w.DWORD),('affinity',c.c_size_t),('priority',w.DWORD),('scheduling',w.DWORD)]
class IOC(c.Structure):_fields_=[(x,c.c_ulonglong) for x in ('ro','wo','oo','rb','wb','ob')]
class LIMIT(c.Structure):_fields_=[('basic',BASICLIMIT),('io',IOC),('process_mem',c.c_size_t),('job_mem',c.c_size_t),('peak_process',c.c_size_t),('peak_job',c.c_size_t)]
K.CreateJobObjectW.argtypes=[c.c_void_p,w.LPCWSTR];K.CreateJobObjectW.restype=w.HANDLE
K.SetInformationJobObject.argtypes=[w.HANDLE,c.c_int,c.c_void_p,w.DWORD]
K.CreateProcessW.argtypes=[w.LPCWSTR,w.LPWSTR,c.c_void_p,c.c_void_p,w.BOOL,w.DWORD,c.c_void_p,w.LPCWSTR,c.POINTER(STARTUPINFO),c.POINTER(PROCESSINFO)]
K.AssignProcessToJobObject.argtypes=[w.HANDLE,w.HANDLE]
K.ResumeThread.argtypes=[w.HANDLE];K.ResumeThread.restype=w.DWORD
K.CloseHandle.argtypes=[w.HANDLE];K.OpenProcess.argtypes=[w.DWORD,w.BOOL,w.DWORD];K.OpenProcess.restype=w.HANDLE
K.IsProcessInJob.argtypes=[w.HANDLE,w.HANDLE,c.POINTER(w.BOOL)]
K.GetProcessTimes.argtypes=[w.HANDLE,c.POINTER(w.FILETIME),c.POINTER(w.FILETIME),c.POINTER(w.FILETIME),c.POINTER(w.FILETIME)]
K.TerminateJobObject.argtypes=[w.HANDLE,w.UINT];K.TerminateProcess.argtypes=[w.HANDLE,w.UINT]
U.GetForegroundWindow.restype=w.HWND;U.SetForegroundWindow.argtypes=[w.HWND];U.ShowWindow.argtypes=[w.HWND,c.c_int]
U.GetWindowThreadProcessId.argtypes=[w.HWND,c.POINTER(w.DWORD)]
U.IsWindowVisible.argtypes=[w.HWND];U.PostMessageW.argtypes=[w.HWND,w.UINT,c.c_size_t,c.c_ssize_t]
CALLBACK=c.WINFUNCTYPE(w.BOOL,w.HWND,w.LPARAM)
U.EnumWindows.argtypes=[CALLBACK,w.LPARAM]

def checked(ok):
    if not ok:raise c.WinError(c.get_last_error())

def creation_filetime(pid):
    handle=K.OpenProcess(0x1000,False,pid);checked(handle)
    try:
        created,exited,kernel,user=(w.FILETIME() for _ in range(4))
        checked(K.GetProcessTimes(handle,c.byref(created),c.byref(exited),c.byref(kernel),c.byref(user)))
        return (created.dwHighDateTime<<32)|created.dwLowDateTime
    finally:K.CloseHandle(handle)

class OwnedApp:
    def __init__(self,arguments,env):
        self.job=K.CreateJobObjectW(None,None);checked(self.job)
        self.process=None;self.pid=None
        try:
            limits=LIMIT();limits.basic.flags=0x2000 # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            checked(K.SetInformationJobObject(self.job,9,c.byref(limits),c.sizeof(limits)))
            block=c.create_unicode_buffer('\0'.join(f'{k}={v}' for k,v in sorted(env.items()))+'\0\0')
            startup=STARTUPINFO();startup.cb=c.sizeof(startup);info=PROCESSINFO()
            checked(K.CreateProcessW(arguments[0],c.create_unicode_buffer(subprocess.list2cmdline(arguments)),None,None,False,
                0x4|0x400,block,None,c.byref(startup),c.byref(info))) # suspended + Unicode environment
            self.process=info.process;self.pid=info.pid
            try:
                checked(K.AssignProcessToJobObject(self.job,self.process))
                if K.ResumeThread(info.thread)==0xffffffff:raise c.WinError(c.get_last_error())
            except BaseException:
                K.TerminateProcess(self.process,1);raise
            finally:K.CloseHandle(info.thread)
        except BaseException:
            if self.process:K.CloseHandle(self.process)
            K.CloseHandle(self.job);raise
    def owns(self,pid):
        handle=K.OpenProcess(0x1000,False,pid)
        if not handle:return False
        try:
            answer=w.BOOL();return bool(K.IsProcessInJob(handle,self.job,c.byref(answer)) and answer.value)
        finally:K.CloseHandle(handle)
    def members(self):return [p for p in psutil.process_iter() if self.owns(p.pid)]
    def windows(self):
        windows=[]
        @CALLBACK
        def visit(hwnd,_):
            pid=w.DWORD();U.GetWindowThreadProcessId(hwnd,c.byref(pid))
            if U.IsWindowVisible(hwnd) and self.owns(pid.value):windows.append((hwnd,pid.value))
            return True
        U.EnumWindows(visit,0);return windows
    def foreground(self,activate=False):
        if activate:
            # CDP/extension readiness can precede the first visible top-level window.
            # Wait for the real UI, never substitute a synthetic foreground PID.
            deadline=time.monotonic()+45;attempts=[]
            while time.monotonic()<deadline:
                windows=self.windows()
                for hwnd,pid in windows:
                    U.ShowWindow(hwnd,9);accepted=bool(U.SetForegroundWindow(hwnd));attempts.append({'pid':pid,'accepted':accepted})
                    time.sleep(.1)
                    if self.foreground()['owned']:return self.foreground()
                time.sleep(.1)
            print(json.dumps({'kind':'foreground-startup-failed','windows':[(int(h),p) for h,p in self.windows()],'attempts':attempts}),file=sys.stderr,flush=True)
        pid=w.DWORD();U.GetWindowThreadProcessId(U.GetForegroundWindow(),c.byref(pid))
        try:created=creation_filetime(pid.value)
        except OSError:created=None
        return {'pid':pid.value,'creation_filetime':created,'owned':self.owns(pid.value)}
    def close(self):
        before=[{'pid':p.pid,'create_time':p.create_time()} for p in self.members()]
        for hwnd,_ in self.windows():U.PostMessageW(hwnd,0x10,0,0) # WM_CLOSE, only job-owned windows
        deadline=time.monotonic()+8
        while self.members() and time.monotonic()<deadline:time.sleep(.1)
        forced=bool(self.members())
        if forced:checked(K.TerminateJobObject(self.job,0))
        deadline=time.monotonic()+5
        while self.members() and time.monotonic()<deadline:time.sleep(.05)
        remaining=[p.pid for p in self.members()]
        K.CloseHandle(self.process);K.CloseHandle(self.job);self.job=None
        return {'completed':not remaining,'forced_job_cleanup':forced,'members_before_close':before,'remaining_pids':remaining}

def read_url(url,data=None,token=None):
    req=urllib.request.Request(url,data=None if data is None else json.dumps(data).encode(),headers={'Content-Type':'application/json',**({'Authorization':'Bearer '+token} if token else {})})
    with urllib.request.urlopen(req,timeout=45) as response:return json.load(response)

class CDP:
    def __init__(self,url):self.socket=websocket.create_connection(url,timeout=45,suppress_origin=True);self.sequence=0
    def call(self,method,**params):
        self.sequence+=1;self.socket.send(json.dumps(dict(id=self.sequence,method=method,params=params)))
        while True:
            reply=json.loads(self.socket.recv())
            if reply.get('id')==self.sequence:
                if 'error' in reply:raise RuntimeError(reply['error'])
                return reply.get('result',{})
    def evaluate(self,expression):
        result=self.call('Runtime.evaluate',expression=expression,awaitPromise=True,returnByValue=True)
        if 'exceptionDetails' in result:raise RuntimeError(result['exceptionDetails'])
        return result['result'].get('value')

class QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self,*args):pass

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--app',choices=['chrome','code'],required=True);parser.add_argument('--runtime',type=Path,required=True)
    args=parser.parse_args();root=args.runtime.resolve();root.mkdir(parents=True,exist_ok=False)
    assets=Path(__file__).resolve().parent;app=None;server=None;cdp=None;closed=False
    token=secrets.token_hex(32);env=os.environ.copy();env.pop('ELECTRON_RUN_AS_NODE',None)
    env.update(RAMFLOW_APP_DIR=str(root),RAMFLOW_APP_TOKEN=token)
    try:
        if args.app=='chrome':
            executable=Path(os.environ.get('PROGRAMFILES','C:/Program Files'))/'Google/Chrome/Application/chrome.exe'
            server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(QuietHandler,directory=str(assets)))
            threading.Thread(target=server.serve_forever,daemon=True).start()
            url=f'http://127.0.0.1:{server.server_port}/browser.html'
            app=OwnedApp([str(executable),'--user-data-dir='+str(root/'profile'),'--remote-debugging-port=0','--no-first-run','--no-default-browser-check','--disable-extensions',url],env)
            deadline=time.monotonic()+50
            while not (root/'profile/DevToolsActivePort').exists():
                if time.monotonic()>deadline:raise TimeoutError('Chrome CDP startup')
                time.sleep(.1)
            port=(root/'profile/DevToolsActivePort').read_text().splitlines()[0]
            while True:
                pages=read_url(f'http://127.0.0.1:{port}/json/list');page=next((x for x in pages if x.get('url')==url),None)
                if page:break
                if time.monotonic()>deadline:raise TimeoutError('Chrome workload page')
                time.sleep(.1)
            cdp=CDP(page['webSocketDebuggerUrl'])
            while cdp.evaluate('typeof window.prepare')!='function':
                if time.monotonic()>deadline:raise TimeoutError('Chrome workload JavaScript')
                time.sleep(.1)
            browser_version=read_url(f'http://127.0.0.1:{port}/json/version')['Browser'];features=['search','sort']
        else:
            executable=Path(os.environ['LOCALAPPDATA'])/'Programs/Microsoft VS Code/Code.exe'
            workspace=root/'workspace';workspace.mkdir()
            (workspace/'fixture.ts').write_text('interface Item { id:number; label:string; score:number; group:number }\nconst item:Item={id:1,label:"one",score:2,group:3};\nconst dataset:Item[]=[\n'+''.join(f'{{id:{i},label:"record-{i}",score:{i%997},group:{i%10}}},\n' for i in range(5000))+'];\n// start',encoding='utf-8')
            (root/'profile/User').mkdir(parents=True)
            (root/'profile/User/settings.json').write_text(json.dumps({'security.workspace.trust.enabled':False,'workbench.startupEditor':'none','update.mode':'none','extensions.autoUpdate':False,'telemetry.telemetryLevel':'off','files.autoSave':'off'}))
            app=OwnedApp([str(executable),'--user-data-dir',str(root/'profile'),'--extensions-dir',str(root/'extensions'),'--extensionDevelopmentPath='+str(assets/'extension'),'--new-window','--skip-welcome','--skip-release-notes',str(workspace)],env)
            deadline=time.monotonic()+90
            while not (root/'extension-ready.json').exists():
                if time.monotonic()>deadline:raise TimeoutError('VS Code extension activation')
                time.sleep(.2)
            ready=json.loads((root/'extension-ready.json').read_text());browser_version=ready['version'];features=['completion','save']
            if not app.owns(ready['pid']):raise RuntimeError('Extension host is not in owned job')
        foreground=app.foreground(activate=True)
        if not foreground['owned']:raise RuntimeError('Owned app did not obtain actual OS foreground')
        print(json.dumps({'kind':'ready','app':args.app,'app_pid':app.pid,'app_creation_filetime':creation_filetime(app.pid),'foreground':foreground,'version':browser_version,
            'python':sys.version,'psutil':psutil.__version__,'websocket_client':websocket.__version__,
            'executable':str(executable),'binary_sha256':hashlib.sha256(executable.read_bytes()).hexdigest(),'features':features,'runtime':str(root)}),flush=True)
        for line in sys.stdin:
            command=json.loads(line);verb=command['verb'];identifier=command['id']
            if verb=='stop':
                if cdp:cdp.socket.close();cdp=None
                cleanup=app.close();closed=True
                print(json.dumps({'id':identifier,'kind':'stopped','cleanup':cleanup}),flush=True);break
            feature=command['feature']
            foreground=app.foreground()
            if not foreground['owned']:raise RuntimeError('Foreground focus left owned app; trial invalidated')
            clock=time.perf_counter()
            if args.app=='chrome':
                rect=cdp.evaluate(f'window.prepare({json.dumps(feature)},{identifier})')
                cdp.call('Input.dispatchMouseEvent',type='mousePressed',x=rect['x'],y=rect['y'],button='left',clickCount=1)
                cdp.call('Input.dispatchMouseEvent',type='mouseReleased',x=rect['x'],y=rect['y'],button='left',clickCount=1)
                result=cdp.evaluate('window.pending')
            else:result=read_url(f"http://127.0.0.1:{ready['port']}/",{'feature':feature,'id':identifier},token)
            result.update(kind='observation',foreground_before=foreground,foreground_after=app.foreground(),roundtrip_ms=(time.perf_counter()-clock)*1000)
            if not result['foreground_after']['owned']:raise RuntimeError('Foreground focus changed during request')
            print(json.dumps(result),flush=True)
    finally:
        if cdp:cdp.socket.close()
        if app and not closed:
            cleanup=app.close();print(json.dumps({'kind':'emergency-cleanup','cleanup':cleanup}),file=sys.stderr,flush=True)
        if server:server.shutdown();server.server_close()

if __name__=='__main__':main()
