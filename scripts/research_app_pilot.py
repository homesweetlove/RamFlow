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

def save_new(path,value):
    with path.open('x',encoding='utf-8') as f:json.dump(value,f,ensure_ascii=False,indent=2);f.flush();os.fsync(f.fileno())

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--output',type=Path,required=True);p.add_argument('--plan',action='store_true')
    p.add_argument('--app',choices=['chrome','code']);p.add_argument('--condition',choices=list('ABC'));p.add_argument('--lab',type=Path,default=Path('scripts/ResearchLab/bin/Release/net10.0-windows/ResearchLab.exe'))
    p.add_argument('--seed',type=int,default=20261111);p.add_argument('--phase-seconds',type=int,default=10);p.add_argument('--contention-threads',type=int,default=12)
    args=p.parse_args();root=args.output.resolve()
    if args.plan:
        root.mkdir(parents=True,exist_ok=False);rng=random.Random(args.seed);schedule=[]
        for app in ['chrome','code']:
            conditions=list('ABC');rng.shuffle(conditions)
            schedule.extend(dict(app=app,condition=c,session=f'{app}-{c}') for c in conditions)
        save_new(root/'pilot-plan.json',dict(protocol='real-app-abc-v2',seed=args.seed,created_utc=datetime.now(timezone.utc).isoformat(),schedule=schedule,
            phase_seconds=args.phase_seconds,contention_threads=args.contention_threads,scope='Six smoke sessions only; no main-study N or efficacy inference'))
        print(json.dumps(schedule));return 0
    if not args.app or not args.condition:p.error('Select --app and --condition for one planned slot')
    plan=json.loads((root/'pilot-plan.json').read_text());slot=next(x for x in plan['schedule'] if x['app']==args.app and x['condition']==args.condition)
    if plan['protocol']!='real-app-abc-v2':p.error('v1 plan is closed; create a NEW v2 plan, never replace failed slots')
    save_new(root/(slot['session']+'.attempt.json'),dict(slot=slot,started_utc=datetime.now(timezone.utc).isoformat(),lab_sha256=hashlib.sha256(args.lab.read_bytes()).hexdigest()))
    command=[str(args.lab.resolve()),'app-session','--protocol',plan['protocol'],'--app',args.app,'--condition',args.condition,'--output',str(root/slot['session']),
        '--phase-seconds',str(plan['phase_seconds']),'--contention-threads',str(plan['contention_threads'])]
    with (root/(slot['session']+'.stdout.txt')).open('x',encoding='utf-8') as out,(root/(slot['session']+'.stderr.txt')).open('x',encoding='utf-8') as err:
        result=subprocess.run(command,stdout=out,stderr=err)
    save_new(root/(slot['session']+'.exit.json'),dict(exit_code=result.returncode,finished_utc=datetime.now(timezone.utc).isoformat()))
    print(slot['session'],'exit',result.returncode,flush=True);return result.returncode

if __name__=='__main__':raise SystemExit(main())
