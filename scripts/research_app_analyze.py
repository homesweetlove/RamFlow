"""Read-only smoke diagnostics. No confidence intervals or efficacy tests from n=1."""
import argparse
import csv
from datetime import datetime
import json
from pathlib import Path
import statistics
from research_logcheck import check_run

def read(path):return json.loads(path.read_text(encoding='utf-8-sig'))
def utc(text):return datetime.fromisoformat(text.replace('Z','+00:00'))
def summarize(root):
    plan=read(root/'pilot-plan.json');sessions=[];metrics=[];overheads=[];parity={}
    for slot in plan['schedule']:
        directory=root/slot['session']
        if not directory.exists():
            sessions.append(dict(**slot,status='unstarted',reason='Not run; never imputed as zero or successful'));continue
        integrity=check_run(directory)
        outcome=read(directory/'outcome.json') if (directory/'outcome.json').exists() else {}
        records=[json.loads(line) for line in (directory/'trial-events.jsonl').read_text().splitlines()]
        ready=next((r['data'] for r in records if r['kind']=='app-ready'),{})
        session=dict(**slot,status=outcome.get('status','unfinalized'),integrity=integrity,restored=outcome.get('restored'),cleanup_completed=outcome.get('cleanup_completed'),
            applies=outcome.get('applies'),protected_misinterventions=outcome.get('protected_misinterventions'),readback_failures=outcome.get('readback_failures'),
            version=ready.get('version'),binary_sha256=ready.get('binary_sha256'),failures=outcome.get('failures',[]))
        sessions.append(session)
        writes=[r['data'] for r in records if r['kind']=='resource-write-readback']
        active={tuple(r['observed'][k] for k in ('CpuPriority','MemoryPriority','PowerControl','PowerState'))+(tuple(r['observed']['CpuSets']),)
            for r in writes if r.get('matched') and r['requested']['CpuPriority']==0x4000 and r['requested']['PowerState']&1}
        parity[slot['session']]=sorted(active)
        session['active_readbacks']=sorted(active)
        session['app_cleanup']=[r['data']['cleanup'] for r in records if r['kind']=='app-cleanup']
        session['interventions_and_restores']=[dict(time_utc=r['time_utc'],phase=r['data']['phase'],kind=r['data']['entry']['Kind'],result=r['data']['entry']['Result']) for r in records if r['kind']=='engine-event' and r['data']['entry']['Kind'] in ('apply','restore')]
        if not integrity['ok'] or outcome.get('status')!='completed':continue
        for phase in outcome['phases']:
            for metric in phase['metrics']:
                metrics.append(dict(**slot,phase=phase['phase'],feature=metric['feature'],requests=metric['count'],p50_ms=metric['p50_ms'],p95_ms=metric['p95_ms'],
                    background_units_per_second=phase['background_units_per_second'],background_units=phase['background_units'],background_seconds=phase['background_seconds']))
        boundaries=[]
        for start in [r for r in records if r['kind']=='phase-start']:
            end=next(r for r in records if r['kind']=='phase-end' and r['data']['phase']==start['data']['phase'])
            boundaries.append((utc(start['time_utc']),utc(end['time_utc']),start['data']['phase']))
        raw=[json.loads(line) for file in sorted((directory/'independent').glob('*.raw.jsonl')) for line in file.read_text().splitlines()]
        grouped={}
        for sample in raw:
            phase=next((p for begin,end,p in boundaries if begin<=utc(sample['time_utc'])<=end),'outside-measured-phase')
            grouped.setdefault((phase,sample['role']),[]).append(sample)
        for (phase,role),rows in grouped.items():
            cpu=[r['cpu_percent'] for r in rows if r.get('status')=='ok' and r.get('cpu_percent') is not None]
            private=[r['private_bytes'] for r in rows if r.get('status')=='ok' and r.get('private_bytes') is not None]
            working=[r['working_set_bytes'] for r in rows if r.get('status')=='ok' and r.get('working_set_bytes') is not None]
            overheads.append(dict(**slot,phase=phase,role=role,samples=len(rows),available_cpu_samples=len(cpu),cpu_percent_mean=statistics.mean(cpu) if cpu else None,
                private_bytes_max=max(private) if private else None,working_set_bytes_max=max(working) if working else None,unavailable_samples=sum(r.get('status')!='ok' for r in rows)))
        for role in ['tray','optional-service']:
            if role=='tray' and slot['condition']!='A':continue
            overheads.append(dict(**slot,phase='all',role=role,samples=0,status='not-started',cpu_percent_mean=None,private_bytes_max=None,working_set_bytes_max=None))
    matched={app:bool(parity.get(app+'-B')) and parity.get(app+'-B')==parity.get(app+'-C') for app in ['chrome','code']}
    passed=len(sessions)==6 and all(s.get('status')=='completed' and s.get('integrity',{}).get('ok') for s in sessions) and all(matched.values())
    return dict(protocol='real-app-abc-v1',smoke_passed=passed,sessions=sessions,matched_B_C_readbacks=matched,metrics=metrics,overheads=overheads,
        inference='n=1 per app/condition smoke only. Do not use requests as independent blocks. No superiority, CI, main-study sample size, or old-version comparison claimed.',
        costs='CPU normalized to all logical processors. Engine shares process with controller; tray, app driver and independent observer are separate. Absent service/tray is null, not zero.')

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--input',required=True,type=Path);p.add_argument('--output',required=True,type=Path);args=p.parse_args()
    result=summarize(args.input.resolve());args.output.mkdir(parents=True,exist_ok=False)
    (args.output/'smoke-summary.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    for name in ['metrics','overheads']:
        rows=result[name];fields=list(dict.fromkeys(k for r in rows for k in r))
        with (args.output/(name+'.csv')).open('w',encoding='utf-8-sig',newline='') as stream:
            writer=csv.DictWriter(stream,fieldnames=fields);writer.writeheader();writer.writerows(rows)
    print(json.dumps(dict(smoke_passed=result['smoke_passed'],matched_B_C_readbacks=result['matched_B_C_readbacks'],sessions=[dict(session=s['session'],status=s['status'],integrity=s.get('integrity',{}).get('ok')) for s in result['sessions']]),indent=2))
    return 0 if result['smoke_passed'] else 1

if __name__=='__main__':raise SystemExit(main())
