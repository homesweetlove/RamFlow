"""Disposable load-connectivity diagnostic. No app/policy effectiveness result.
No priorities, QoS, system settings or other processes are modified.
"""
import argparse
import ctypes as c
from datetime import datetime,timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import threading
import psutil

class Memory(c.Structure):
    _fields_=[('length',c.c_ulong),('load',c.c_ulong)]+[(x,c.c_ulonglong) for x in ['total','available','limit','free_commit','vtotal','vfree','extended']]

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--output',required=True,type=Path);parser.add_argument('--threads',type=int,default=14);args=parser.parse_args()
    if not 1<=args.threads<=min(16,psutil.cpu_count()):parser.error('Invalid bounded worker count')
    root=args.output.resolve();root.mkdir(parents=True,exist_ok=False);repo=Path(__file__).resolve().parents[1]
    app=repo/'scripts/ResearchLab/bin/Release/net10.0-windows/ResearchLab.exe';rows=[];errors=[];child=None;reader=None;phase='setup';cleanup=False
    def save(name,value):
        with (root/name).open('x',encoding='utf-8') as f:json.dump(value,f,indent=2);f.flush();os.fsync(f.fileno())
    def guard():
        m=Memory();m.length=c.sizeof(m)
        if not c.windll.kernel32.GlobalMemoryStatusEx(c.byref(m)):raise c.WinError()
        if m.available<2*2**30 or (m.limit-m.free_commit)/m.limit>.85:raise RuntimeError('Headroom admission/release gate')
        return m
    save('started.json',dict(scope=__doc__,threads=args.threads,workload='xorshift64-1000000-v1',controller_binary_sha256=hashlib.sha256(app.with_suffix('.dll').read_bytes()).hexdigest(),created_utc=datetime.now(timezone.utc).isoformat()))
    try:
        guard()
        with (root/'worker.stderr.txt').open('x',encoding='utf-8') as err:
            child=subprocess.Popen([str(app),'worker','--role','background','--mode','cpu-arithmetic','--mib','4','--threads',str(args.threads),'--active-threads','0','--seed','20261111'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=err,text=True)
            assert json.loads(child.stdout.readline())['Kind']=='ready'
            def drain():
                with (root/'worker.jsonl').open('x',encoding='utf-8') as f:
                    for line in child.stdout:f.write(line);f.flush()
            reader=threading.Thread(target=drain);reader.start();process=psutil.Process(child.pid);identity=process.create_time()
            save('owned.json',dict(pid=child.pid,create_time=identity,affinity=process.cpu_affinity()))
            psutil.cpu_percent();process.cpu_percent()
            with (root/'system.jsonl').open('x',encoding='utf-8') as raw:
                for i,(phase,active,count) in enumerate([('Normal',1,3),('Contention',args.threads,8),('Recovery',1,3)],1):
                    child.stdin.write(json.dumps(dict(id=i,verb='load',activeThreads=active,phase=phase))+'\n');child.stdin.flush()
                    for _ in range(count):
                        cpu=psutil.cpu_percent(interval=.5);m=guard()
                        row=dict(phase=phase,time_utc=datetime.now(timezone.utc).isoformat(),cpu_percent=cpu,worker_cpu_percent_normalized=process.cpu_percent()/psutil.cpu_count(),available_bytes=m.available,commit_fraction=(m.limit-m.free_commit)/m.limit)
                        rows.append(row);raw.write(json.dumps(row)+'\n');raw.flush()
            child.stdin.write('{"id":99,"verb":"stop"}\n');child.stdin.flush();child.wait(timeout=5);reader.join(timeout=3);cleanup=child.returncode==0 and not reader.is_alive()
    except Exception as error:errors.append(repr(error))
    finally:
        if child is not None and child.poll() is None:
            try:child.stdin.write('{"id":100,"verb":"stop"}\n');child.stdin.flush();child.wait(timeout=5)
            except Exception as error:errors.append('forced owned-child cleanup: '+repr(error));child.kill();child.wait()
        if reader is not None:reader.join(timeout=3)
    high=[r['cpu_percent'] for r in rows if r['phase']=='Contention'];attained=sum(v>=80 for v in high)>=3
    if not attained:errors.append('Measured CPU did not attain V1 CPU threshold in >=3 preflight samples')
    result=dict(scope='Fixture connectivity only; not app performance',passed=attained and cleanup and not errors,attained=attained,cleanup=cleanup,errors=errors,samples=rows)
    save('result.json',result);print(json.dumps(dict(passed=result['passed'],cpu_high=high,cleanup=cleanup,errors=errors)));return 0 if result['passed'] else 1

if __name__=='__main__':raise SystemExit(main())
