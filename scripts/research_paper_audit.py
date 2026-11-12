"""Read-only post-hoc audit of sealed ABC batches. Never runs apps or setters.
Readback-state dwell is wall time, not CPU share or continuous core residency.
Driver roundtrip is an exploratory secondary endpoint, not physical input latency.
"""
import argparse
from collections import defaultdict
import json
from pathlib import Path
import statistics

from research_logcheck import check_run
from research_analyze import quantile, paired_block_bootstrap, _write_csv
from research_app_pilot import save_new
from research_app_batch import file_hash

def restricted_seconds(changes,begin,end):
    if end<begin:raise ValueError('Invalid phase interval')
    state=False;cursor=begin;total=0.0
    for time,active in sorted(changes,key=lambda x:x[0]):
        if time<=begin:state=active;continue
        if time>=end:break
        if state:total+=time-cursor
        cursor=time;state=active
    if state:total+=end-cursor
    return total

def audit(root):
    plan=json.loads((root/'batch-plan.json').read_text());phases=[];actions=[];metrics=[];runs=[]
    for slot in plan['schedule']:
        d=root/slot['session'];check=check_run(d)
        if not check['ok']:raise ValueError(check)
        read=lambda name:json.loads((d/name).read_text(encoding='utf-8-sig'))
        o=read('outcome.json');manifest=read('run-manifest.json');original=read('resource-original.json');pid=original['pid']
        events=[json.loads(l) for l in (d/'trial-events.jsonl').read_text().splitlines()]
        at=lambda e:e['monotonic_ticks']/manifest['monotonic_frequency']
        changes=[];decision=None
        for e in events:
            if e['kind']=='resource-write-readback':
                w=e['data'];v=w['observed']
                if w['pid']!=pid or not w['matched'] or w['requested']!=v:raise ValueError('Readback identity/result mismatch')
                active=(v['CpuPriority'],v['MemoryPriority'],v['PowerControl'],v['PowerState'],v['CpuSets'])==(0x4000,3,1,1,[])
                if not active and v!=original['values']:raise ValueError('Unknown partial action state')
                changes.append((at(e),active))
            if e['kind']!='engine-event':continue
            a=e['data']['entry']
            if a['Kind']=='decision' and a['Pid']==pid:decision=a['Detail']
            if a['Kind'] in ('apply','restore'):
                i=decision['Input'] if decision else {};chosen=decision['Decision'] if decision else {}
                actions.append(dict(**slot,phase=e['data']['phase'],kind=a['Kind'],result=a['Result'],time_seconds=at(e),
                    cpu_percent=i.get('System',{}).get('CpuPercent'),cpu_pressure=i.get('CpuPressure'),memory_pressure=i.get('MemoryPressure'),
                    prior_action=i.get('CurrentAction'),chosen_action=chosen.get('Action'),reason=chosen.get('Reason'),
                    note='Latest preceding background decision; cleanup actions need not have a corresponding new decision'))
        for start in [e for e in events if e['kind']=='phase-start']:
            name=start['data']['phase'];end=next((e for e in events if e['kind']=='phase-end' and e['data']['phase']==name),None)
            if end is None:continue
            seconds=at(end)-at(start);restricted=restricted_seconds(changes,at(start),at(end))
            phase_actions=[a for a in actions if a['session']==slot['session'] and a['phase']==name]
            sample=[e['data']['sample']['CpuPercent'] for e in events if e['kind']=='system-sample' and e['data']['phase']==name]
            phases.append(dict(**slot,phase=name,duration_seconds=seconds,restricted_readback_seconds=restricted,restricted_readback_fraction=restricted/seconds,
                applies=sum(a['kind']=='apply' and a['result']=='applied' for a in phase_actions),restores=sum(a['kind']=='restore' and a['result']=='restored' for a in phase_actions),
                system_cpu_mean=statistics.mean(sample) if sample else None))
        if o['status']=='completed':
            for p in o['phases']:
                for m in p['metrics']:
                    metrics.append(dict(**slot,phase=p['phase'],feature=m['feature'],requests=m['count'],
                        handler_or_api_p95_ms=quantile(m['latencies_ms'],.95),driver_roundtrip_p95_ms=quantile(m['roundtrip_ms'],.95),
                        background_units_per_second=p['background_units_per_second']))
        runs.append(dict(**slot,status=o['status'],source_outcome_sha256=file_hash(d/'outcome.json'),source_events_sha256=file_hash(d/'trial-events.jsonl')))
    comparisons=[]
    for app,feature in [('chrome','search'),('chrome','sort'),('code','completion'),('code','save')]:
        r=[m for m in metrics if m['app']==app and m['feature']==feature and m['phase']=='Contention']
        for base,candidate in [('A','B'),('A','C'),('B','C')]:
            a={m['block']:m for m in r if m['condition']==base};b={m['block']:m for m in r if m['condition']==candidate};both=sorted(a.keys()&b.keys())
            comparisons.append(dict(app=app,feature=feature,baseline=base,candidate=candidate,blocks=both,
                **paired_block_bootstrap([(a[k]['driver_roundtrip_p95_ms'],b[k]['driver_roundtrip_p95_ms']) for k in both],iterations=10000,seed=20261112)))
    scripts=Path(__file__).resolve().parent
    return dict(scope=__doc__,post_hoc=True,analysis_source_sha256={name:file_hash(scripts/name) for name in
        ('research_paper_audit.py','research_analyze.py','research_logcheck.py','research_app_batch.py')},
        source_plan_sha256=file_hash(root/'batch-plan.json'),runs=runs,phases=phases,actions=actions,metrics=metrics,roundtrip_comparisons=comparisons)

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--input',type=Path,required=True);p.add_argument('--output',type=Path,required=True);args=p.parse_args()
    root=args.input.resolve();output=args.output.resolve()
    if output==root or root in output.parents:p.error('Output must be outside sealed input')
    result=audit(root);output.mkdir(parents=True,exist_ok=False)
    save_new(output/'audit.json',result)
    for name in ('runs','phases','actions','metrics','roundtrip_comparisons'):_write_csv(output/(name+'.csv'),result[name])
    save_new(output/'artifact-index.json',dict(files=[dict(path=f.name,bytes=f.stat().st_size,sha256=file_hash(f)) for f in sorted(output.iterdir())]))
    print(json.dumps(dict(sealed_runs=len(result['runs']),phase_rows=len(result['phases']),action_rows=len(result['actions']),output=str(output))))

if __name__=='__main__':main()
