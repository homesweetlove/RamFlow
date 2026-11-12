"""Scheduling diagnosis on one disposable fixture, not app efficacy.
Default is read-only. --qos-probe compares execution-speed QoS on that owned handle
and restores it; priority, affinity, memory priority and power plans are never set.
"""
import argparse
from datetime import datetime,timezone
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time
import ctypes as c
import psutil
from cpu_native_counters import CpuCounters
from research_cpu_preflight import Memory,pressure_stable
from owned_process_qos import OwnedQos

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--output',type=Path,required=True);p.add_argument('--threads',type=int,default=14);p.add_argument('--qos-probe',action='store_true');args=p.parse_args()
    if not 1<=args.threads<=min(16,psutil.cpu_count()):p.error('Invalid bounded thread count')
    root=args.output.resolve();root.mkdir(parents=True,exist_ok=False);repo=Path(__file__).resolve().parents[1]
    app=repo/'scripts/ResearchLab/bin/Release/net10.0-windows/ResearchLab.exe'
    def save(name,value):
        with (root/name).open('x',encoding='utf-8') as f:json.dump(value,f,indent=2);f.flush();os.fsync(f.fileno())
    def guard():
        value=Memory();value.length=c.sizeof(value)
        if not c.windll.kernel32.GlobalMemoryStatusEx(c.byref(value)):raise c.WinError()
        if value.available<2*2**30 or (value.limit-value.free_commit)/value.limit>.85:raise RuntimeError('Headroom gate released')
        return value
    files=['scripts/ResearchLab/CpuInspection.cs','scripts/ResearchLab/FixtureWorker.cs','scripts/ResearchLab/CpuWork.cs','scripts/cpu_native_counters.py','scripts/research_cpu_diagnose.py','scripts/owned_process_qos.py']
    sources={name:hashlib.sha256((repo/name).read_bytes()).hexdigest() for name in files}
    for name in files:
        target=root/'source'/name;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes((repo/name).read_bytes())
    manifest=dict(profile='owned-cpu-qos-probe-v1' if args.qos_probe else 'owned-cpu-diagnostic-v1',created_utc=datetime.now(timezone.utc).isoformat(),scope=__doc__,threads=args.threads,
        source_commit=subprocess.check_output(['git','rev-parse','HEAD'],cwd=repo,text=True).strip(),source_dirty=bool(subprocess.check_output(['git','status','--porcelain'],cwd=repo,text=True).strip()),
        source_sha256=sources,binary_sha256=hashlib.sha256(app.with_suffix('.dll').read_bytes()).hexdigest(),counter_paths=CpuCounters.PATHS,
        planned_control_writes='Owned execution-speed override and exact restoration only' if args.qos_probe else 0,scope_limit='Instrumented diagnostic only; last sampled CPU is not a continuous scheduler trace')
    save('manifest.json',manifest)
    child=None;reader=None;counter=None;qos=None;restored=not args.qos_probe;errors=[];rows=[];cleanup=False;sequence=0;ready=threading.Event();replies=queue.Queue()
    stderr=(root/'stderr.txt').open('x',encoding='utf-8');events=(root/'events.jsonl').open('x',encoding='utf-8');epoch=time.perf_counter()
    def event(kind,data,durable=False):
        nonlocal sequence
        sequence+=1;events.write(json.dumps(dict(sequence=sequence,time_utc=datetime.now(timezone.utc).isoformat(),monotonic_seconds=time.perf_counter()-epoch,kind=kind,data=data))+'\n');events.flush()
        if durable:os.fsync(events.fileno())
    def request(verb,**data):
        identifier=request.next_id;request.next_id+=1
        event('request-begin',dict(id=identifier,verb=verb,**data))
        child.stdin.write(json.dumps(dict(id=identifier,verb=verb,**data))+'\n');child.stdin.flush()
        reply=replies.get(timeout=15)
        if isinstance(reply,Exception):raise reply
        if reply['Id']!=identifier:raise RuntimeError('Reply identity mismatch')
        event('request-end',reply);return reply
    request.next_id=1
    try:
        event('start',{});guard();counter=CpuCounters()
        child=subprocess.Popen([str(app),'worker','--role','background','--mode','cpu-arithmetic','--mib','4','--threads',str(args.threads),'--active-threads','0','--seed','20261111','--diagnostics'],
            stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=stderr,text=True)
        def drain():
            try:
                with (root/'worker.jsonl').open('x',encoding='utf-8') as raw:
                    for line in child.stdout:
                        raw.write(line);raw.flush();value=json.loads(line)
                        if value.get('Kind')=='ready':ready.set()
                        if value.get('Id',0)>0:replies.put(value)
            except Exception as error:replies.put(error)
        reader=threading.Thread(target=drain);reader.start()
        if not ready.wait(15):raise TimeoutError('Fixture readiness timeout')
        initial=request('inspect')['Telemetry'];save('owned.json',initial)
        assert initial['pid']==child.pid and initial['control_writes']==0
        expected_values=initial['values'].copy()
        if args.qos_probe:
            qos=OwnedQos(child,initial['creation_filetime'])
            save('restore-intent.json',dict(pid=child.pid,creation_filetime=initial['creation_filetime'],original=qos.original,scope='Only execution-speed QoS; journal before any write'))
        process=psutil.Process(child.pid);psutil.cpu_percent();psutil.cpu_percent(percpu=True);process.cpu_percent()
        with (root/'samples.jsonl').open('x',encoding='utf-8') as raw:
            phases=[('Normal',1,10,'Normal',None),('Contention',args.threads,16,'Contention',None),('Recovery',1,6,'Recovery',None)]
            if args.qos_probe:phases=[('Normal',1,10,'Normal',None),('default-qos',args.threads,12,'Contention',None),('normal-qos',args.threads,12,'Contention','normal'),('default-restored',args.threads,12,'Contention','restore'),('Recovery',1,6,'Recovery',None)]
            for phase,active,count,worker_phase,change in phases:
                if change:
                    event('qos-write-intent',dict(phase=phase,pid=child.pid,creation_filetime=initial['creation_filetime'],action=change,original=qos.original),True)
                    observed=qos.normal() if change=='normal' else qos.restore()
                    expected_values['PowerControl']=observed['control'];expected_values['PowerState']=observed['state']
                    restored=observed==qos.original
                    event('qos-readback',dict(phase=phase,observed=observed,restored=restored),True)
                changed=request('load',activeThreads=active,phase=worker_phase);event('phase-start',dict(phase=phase,reply=changed))
                for _ in range(count):
                    busy=psutil.cpu_percent(interval=.5);percore=psutil.cpu_percent(percpu=True);memory=guard();pdh=counter.read()
                    native=request('inspect');info=native['Telemetry']
                    if info['pid']!=initial['pid'] or info['creation_filetime']!=initial['creation_filetime'] or info['control_writes']!=0:raise RuntimeError('Owned identity/write invariant failed')
                    if info['values']!=expected_values:raise RuntimeError('Fixture resource settings changed unexpectedly')
                    if info['active_threads']!=active:raise RuntimeError('Requested worker count was not retained')
                    if native['Units']>0 and native['Checksum']!='bd2eeae7775b742c':raise RuntimeError('Completed work checksum mismatch')
                    row=dict(phase=phase,time_utc=datetime.now(timezone.utc).isoformat(),monotonic_seconds=time.perf_counter()-epoch,
                        system_time_cpu_percent=busy,per_core_time_cpu_percent=percore,process_time_cpu_percent_normalized=process.cpu_percent()/psutil.cpu_count(),
                        pdh=pdh,worker_reply=native,available_bytes=memory.available,commit_fraction=(memory.limit-memory.free_commit)/memory.limit)
                    rows.append(row);raw.write(json.dumps(row)+'\n');raw.flush()
                event('phase-end',dict(phase=phase))
        child.stdin.write('{"id":999,"verb":"stop"}\n');child.stdin.flush();child.wait(timeout=5);reader.join(timeout=3)
        cleanup=child.returncode==0 and not reader.is_alive();event('child-exit',dict(exit_code=child.returncode,cleanup=cleanup))
    except Exception as error:errors.append(repr(error));event('failure',dict(error=repr(error)))
    finally:
        if child is not None and child.poll() is None:
            if qos is not None:
                try:
                    event('qos-restore-intent',dict(original=qos.original),True);observed=qos.restore();restored=observed==qos.original
                    event('qos-final-readback',dict(observed=observed,restored=restored),True)
                except Exception as error:errors.append('restore failed:'+repr(error));restored=False
            try:child.stdin.write('{"id":1000,"verb":"stop"}\n');child.stdin.flush();child.wait(timeout=5)
            except Exception as error:errors.append('owned force-stop:'+repr(error));child.kill();child.wait()
        if reader is not None:reader.join(timeout=3)
        if counter is not None:counter.close()
        stderr.close()
    high=[r['system_time_cpu_percent'] for r in rows if r['phase'] in ('Contention','default-qos')]
    status='completed' if cleanup and restored and not errors else 'failed'
    result=dict(status=status,cleanup_completed=cleanup,restored=restored,control_writes=qos.successful_writes if qos else 0,headroom_ok=not any('Headroom' in e for e in errors),
        errors=errors,samples=len(rows),sustained_time_cpu=pressure_stable(high),inference='Owned-process diagnostic completed is not policy/app efficacy or admission for a different input stratum')
    save('result.json',result);event('terminal',result);events.close()
    save('artifact-index.json',dict(profile=manifest['profile'],files=[dict(path=f.relative_to(root).as_posix(),bytes=f.stat().st_size,sha256=hashlib.sha256(f.read_bytes()).hexdigest()) for f in sorted(root.rglob('*')) if f.is_file()]))
    print(json.dumps(result));return 0 if status=='completed' else 1

if __name__=='__main__':raise SystemExit(main())
