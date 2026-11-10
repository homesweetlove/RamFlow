"""Logging fault/cancellation integration only, not an efficacy experiment."""
import json
import os
from pathlib import Path
import subprocess
import time
import uuid
import hashlib
import sys
import research_logcheck
import research_analyze

root=Path(__file__).resolve().parents[1]
lab=root/'scripts/ResearchLab/bin/Release/net10.0-windows/ResearchLab.exe'
native_tests=root/'native/RamFlow.Tests/bin/Release/net10.0-windows/RamFlow.Tests.exe'
if os.name!='nt':
    print('SKIP logging Windows integration: Windows required');raise SystemExit(0)
run=root/'work'/('logging-integration-'+uuid.uuid4().hex);run.mkdir()

def direct(name,extra):
    path=run/name
    r=subprocess.run([str(lab),'trial','--output',str(path),*extra],capture_output=True,text=True,timeout=60)
    (run/(name+'.stdout.log')).write_text(r.stdout,encoding='utf-8')
    (run/(name+'.stderr.log')).write_text(r.stderr,encoding='utf-8')
    return path,r

for name,args in [('bad-args',['--seconds','invalid']),('commit-mismatch',['--commit','0'*40])]:
    path,result=direct(name,args)
    assert result.returncode!=0
    check=research_logcheck.check_run(path)
    assert check['ok'] and check['status']=='failed' and not check['performance_ready'],check
    assert json.loads((path/'failure.json').read_text())['error']
    assert not any(json.loads(line)['kind']=='process-owned' for line in (path/'trial-events.jsonl').read_text().splitlines())

batch=run/'bad-batch'
r=subprocess.run([str(lab),'calibrate','--output',str(batch),'--fit-blocks','0'],capture_output=True,text=True,timeout=20)
assert r.returncode!=0
assert json.loads((batch/'batch.failure.json').read_text())['plan_created'] is False
assert json.loads((batch/'completion.json').read_text())['status']=='failed'
assert 'failure' in (batch/'trial-events.jsonl').read_text()

# Abrupt termination of a logger-only child; no worker or policy is started.
partial=run/'forced-log-only'
with (run/'forced.stdout.log').open('wb') as stdout,(run/'forced.stderr.log').open('wb') as stderr:
    child=subprocess.Popen([str(native_tests),'--evidence-open-child',str(partial)],stdout=stdout,stderr=stderr)
    try:
        deadline=time.monotonic()+15
        while time.monotonic()<deadline:
            events=partial/'trial-events.jsonl'
            if events.exists() and 'diagnostic-ready' in events.read_text(encoding='utf-8'):break
            if child.poll() is not None:raise AssertionError('Logger-only child exited early')
            time.sleep(.05)
        else:raise AssertionError('Logger-only ready timeout')
    finally:
        # Popen's own created process handle; no process discovery/PID reuse.
        child.kill();child.wait(timeout=10)
assert (partial/'trial-events.jsonl').stat().st_size>0
assert not research_logcheck.check_run(partial)['ok']
assert not (partial/'completion.json').exists()

# Graceful cancellation before treatment in a disposable A/no-engine trial.
cancelled=run/'cancelled'
with (run/'cancel.stdout.log').open('wb') as stdout,(run/'cancel.stderr.log').open('wb') as stderr:
    child=subprocess.Popen([str(lab),'trial','--output',str(cancelled),'--condition','A','--regime','healthy',
        '--seconds','2','--background-threads','1','--foreground-mib','4','--background-mib','4'],stdout=stdout,stderr=stderr)
    try:
        deadline=time.monotonic()+25
        while time.monotonic()<deadline:
            events=cancelled/'trial-events.jsonl'
            if events.exists() and '"name":"pre-intervention"' in events.read_text(encoding='utf-8'):break
            if child.poll() is not None:
                value=json.loads((cancelled/'outcome.json').read_text())
                if any('headroom' in str(e) or 'existing RAM Flow instance' in str(e) or 'No interactive user session' in str(e) for e in value.get('failures',[])):
                    print('SKIP live cancellation: environment guard; setup/forced-log checks passed; retained at',run);raise SystemExit(0)
                raise AssertionError(value)
            time.sleep(.05)
        else:raise AssertionError('Cancellation readiness timeout')
        (cancelled/'cancel.request').write_text('{"reason":"SYNTHETIC cancellation verification"}',encoding='utf-8')
        code=child.wait(timeout=35)
    finally:
        if child.poll() is None:child.kill();child.wait(timeout=10)
assert code!=0
check=research_logcheck.check_run(cancelled)
assert check['ok'] and check['status']=='aborted' and not check['performance_ready'],check
value=json.loads((cancelled/'outcome.json').read_text())
assert value['restored'] and value['cleanup_completed'] and value['actual_apply_count']==0
assert value['failures'] and value['evidence_run_id']

# End-to-end adapter delivery, stream capture and alias/integrity analysis gate.
delivery=run/'adapter';delivery.mkdir();dummy=run/'unused-model.json';dummy.write_text('{"label":"SYNTHETIC unused A input"}',encoding='utf-8')
r=subprocess.run([sys.executable,str(root/'scripts/research_lab_adapter.py'),'run','--lab',str(lab),'--model',str(dummy),
    '--native-dir',str(delivery/'native'),'--outcome',str(delivery/'outcome.json'),'--condition','A','--regime','healthy',
    '--block','logging-only-functional','--seed','1','--seconds','2','--background-threads','1'],capture_output=True,text=True,timeout=60)
assert r.returncode==0,r.stderr
done=json.loads((delivery/'adapter.finished.json').read_text())
assert done['evidence']['performance_ready'] and not done['forced_stop']
assert done['stdout_sha256']==hashlib.sha256((delivery/'native.stdout.log').read_bytes()).hexdigest()
assert (delivery/'outcome.json').read_bytes()==(delivery/'native/outcome.json').read_bytes()
analysis=research_analyze.analyze_directory(delivery,foreground_tolerance_ms=1,bootstrap_iterations=200,seed=1)
assert sum(row['eligible'] for row in analysis['ledger'])==1
assert len(analysis['blocks'])==1
print('Logging integration passed: setup/commit/batch failures, abrupt logger-only termination, graceful cancellation, adapter streams/alias/integrity gate; retained at',run)
