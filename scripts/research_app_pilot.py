"""Plan exactly one A/B/C smoke session per app; execute each immutable slot once.
Build/test before execution. Foreground activation may need operator/desktop-tool help.
No analysis/build/test concurrently with measured phases. Failed slots are never replaced.
"""
import argparse
from datetime import datetime,timezone
import hashlib
import json
import os
from pathlib import Path
import random
import subprocess
from research_cpu_preflight import pressure_stable

def save_new(path,value):
    with path.open('x',encoding='utf-8') as f:json.dump(value,f,ensure_ascii=False,indent=2);f.flush();os.fsync(f.fileno())

def verify_preflight(directory,lab,threads,names=('started.json','result.json')):
    start_bytes=(directory/names[0]).read_bytes();result_bytes=(directory/names[1]).read_bytes()
    started=json.loads(start_bytes);result=json.loads(result_bytes)
    binary=hashlib.sha256(lab.with_suffix('.dll').read_bytes()).hexdigest()
    samples=[s['cpu_percent'] for s in result.get('samples',[]) if s.get('phase')=='Contention']
    if (started.get('admission_version')!='cpu-preflight-v2-sustained' or started.get('threads')!=threads or
        started.get('workload')!='xorshift64-1000000-v1' or started.get('controller_binary_sha256')!=binary or
        result.get('passed') is not True or result.get('cleanup') is not True or not pressure_stable(samples)):
        raise ValueError('Sustained CPU preflight not admitted, cleanup unconfirmed, or workload/binary mismatch; no new app comparison launched')
    return dict(started_sha256=hashlib.sha256(start_bytes).hexdigest(),result_sha256=hashlib.sha256(result_bytes).hexdigest(),managed_binary_sha256=binary,threads=threads)

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--output',type=Path,required=True);p.add_argument('--plan',action='store_true')
    p.add_argument('--app',choices=['chrome','code']);p.add_argument('--condition',choices=list('ABC'));p.add_argument('--lab',type=Path,default=Path('scripts/ResearchLab/bin/Release/net10.0-windows/ResearchLab.exe'))
    p.add_argument('--preflight',type=Path,help='Successful sustained-pressure diagnostic directory; mandatory for a NEW plan')
    p.add_argument('--seed',type=int,default=20261111);p.add_argument('--phase-seconds',type=int,default=10);p.add_argument('--contention-threads',type=int,default=14)
    args=p.parse_args();root=args.output.resolve()
    if args.plan:
        if args.preflight is None:p.error('A NEW plan requires --preflight with sustained-pressure evidence')
        try:proof=verify_preflight(args.preflight.resolve(),args.lab,args.contention_threads)
        except (OSError,ValueError,KeyError,TypeError) as error:p.error(str(error))
        root.mkdir(parents=True,exist_ok=False);rng=random.Random(args.seed);schedule=[]
        for source,target in [('started.json','admission-started.json'),('result.json','admission-result.json')]:
            with (root/target).open('xb') as f:f.write((args.preflight/source).read_bytes());f.flush();os.fsync(f.fileno())
        for app in ['chrome','code']:
            conditions=list('ABC');rng.shuffle(conditions)
            schedule.extend(dict(app=app,condition=c,session=f'{app}-{c}') for c in conditions)
        save_new(root/'pilot-plan.json',dict(protocol='real-app-abc-v3',background_workload='xorshift64-1000000-v1',seed=args.seed,created_utc=datetime.now(timezone.utc).isoformat(),schedule=schedule,
            phase_seconds=args.phase_seconds,contention_threads=args.contention_threads,admission=proof,scope='Six smoke sessions only; no main-study N or efficacy inference'))
        print(json.dumps(schedule));return 0
    if not args.app or not args.condition:p.error('Select --app and --condition for one planned slot')
    plan=json.loads((root/'pilot-plan.json').read_text());slot=next(x for x in plan['schedule'] if x['app']==args.app and x['condition']==args.condition)
    if plan['protocol']!='real-app-abc-v3':p.error('v1/v2 plans are closed; create a NEW v3 plan, never replace failed slots')
    try:
        proof=verify_preflight(root,args.lab,plan['contention_threads'],('admission-started.json','admission-result.json'))
        if proof!=plan.get('admission'):raise ValueError('Preflight changed after planning')
    except (OSError,ValueError,KeyError,TypeError) as error:p.error(str(error))
    save_new(root/(slot['session']+'.attempt.json'),dict(slot=slot,started_utc=datetime.now(timezone.utc).isoformat(),lab_sha256=hashlib.sha256(args.lab.read_bytes()).hexdigest()))
    command=[str(args.lab.resolve()),'app-session','--protocol',plan['protocol'],'--app',args.app,'--condition',args.condition,'--output',str(root/slot['session']),
        '--phase-seconds',str(plan['phase_seconds']),'--contention-threads',str(plan['contention_threads'])]
    with (root/(slot['session']+'.stdout.txt')).open('x',encoding='utf-8') as out,(root/(slot['session']+'.stderr.txt')).open('x',encoding='utf-8') as err:
        result=subprocess.run(command,stdout=out,stderr=err)
    save_new(root/(slot['session']+'.exit.json'),dict(exit_code=result.returncode,finished_utc=datetime.now(timezone.utc).isoformat()))
    print(slot['session'],'exit',result.returncode,flush=True);return result.returncode

if __name__=='__main__':raise SystemExit(main())
