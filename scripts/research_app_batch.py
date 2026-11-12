"""One bounded, immutable real-app A/B/C batch; analysis only after children exit.
Build/test first. Windows foreground activation may require desktop assistance.
No automatic retry, tuning, efficacy verdict, or request-level independent N.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import itertools
import json
import os
from pathlib import Path
import random
import statistics
import subprocess
import sys

from research_app_pilot import save_new, verify_preflight
from research_app_analyze import read, utc
from research_analyze import paired_block_bootstrap, bars_svg, _write_csv, _fmt
from research_logcheck import check_run

PROTOCOL='real-app-abc-v4-normal-qos'
REPO=Path(__file__).resolve().parents[1]

def schedule(blocks,seed):
    if type(blocks) is not int or blocks<6 or blocks>30 or blocks%6:raise ValueError('Use 6..30 blocks, multiple of six for complete order balance')
    rng=random.Random(seed);orders={}
    for app in ('chrome','code'):
        orders[app]=[]
        for _ in range(blocks//6):
            part=list(itertools.permutations('ABC'));rng.shuffle(part);orders[app].extend(part)
    result=[]
    for b in range(1,blocks+1):
        apps=['chrome','code'];rng.shuffle(apps)
        for app in apps:
            for position,c in enumerate(orders[app][b-1],1):
                result.append(dict(block=b,app=app,condition=c,position=position,session=f'b{b:02d}/{app}-{c}'))
    return result

def file_hash(path):return hashlib.sha256(path.read_bytes()).hexdigest()

def provenance(lab):
    git=lambda *args:subprocess.run(['git',*args],cwd=REPO,capture_output=True,text=True,check=True).stdout.strip()
    if git('status','--porcelain'):raise ValueError('Commit source before measured batch; dirty checkout refused')
    names=git('ls-files','native/RamFlow.Core','scripts/ResearchLab','scripts/real_apps').splitlines()
    names+=['scripts/'+x for x in ('research_app_batch.py','research_app_pilot.py','research_app_analyze.py','research_analyze.py','research_logcheck.py','research_cpu_preflight.py','owned_process_qos.py')]
    files={name:file_hash(REPO/name) for name in sorted(set(names)) if (REPO/name).is_file()}
    binaries=[lab,lab.with_suffix('.dll'),lab.parent/'RamFlow.Core.dll',REPO/'native/RamFlow.Tray/bin/Release/net10.0-windows/RamFlow.Tray.exe',REPO/'native/RamFlow.Tray/bin/Release/net10.0-windows/RamFlow.Tray.dll']
    files.update({str(p.resolve().relative_to(REPO)):file_hash(p) for p in binaries})
    return dict(commit=git('rev-parse','HEAD'),files=files)

def verify_frozen(plan):
    for name,digest in plan['provenance']['files'].items():
        if file_hash(REPO/name)!=digest:raise ValueError('Frozen source/binary changed: '+name)

def run_schedule(root,plan,execute,validate=check_run):
    results=[];stop=False;consecutive=0
    for index,slot in enumerate(plan['schedule'],1):
        if stop:
            results.append(dict(**slot,status='not_run_safety_stop'));continue
        attempt=root/(slot['session']+'.attempt.json');attempt.parent.mkdir(parents=True,exist_ok=True)
        save_new(attempt,dict(slot=slot,started_utc=datetime.now(timezone.utc).isoformat()))
        print(json.dumps(dict(event='session-start',index=index,total=len(plan['schedule']),**slot)),flush=True)
        try:
            code=execute(slot);d=root/slot['session'];integrity=validate(d)
            o=read(d/'outcome.json') if (d/'outcome.json').exists() else {}
            status=o.get('status','unfinalized')
            no_bg=o.get('restoration_status')=='not-required-no-background-launched'
            safe=(integrity.get('ok') is True and o.get('cleanup_completed') is True and
                (no_bg or (o.get('restored') is True and o.get('workload_restored') is True)) and
                o.get('protected_misinterventions')==0 and o.get('readback_failures')==0)
            row=dict(**slot,status=status,exit_code=code,integrity=integrity,safe_to_continue=safe)
            if not safe or (code==0 and status!='completed'):stop=True
            consecutive=consecutive+1 if status!='completed' else 0
            if consecutive>=2:stop=True
        except Exception as error:
            row=dict(**slot,status='controller_failed',error=repr(error),safe_to_continue=False);stop=True
        results.append(row);save_new(root/(slot['session']+'.exit.json'),row)
        print(json.dumps(dict(event='session-end',index=index,status=row['status'],safe_to_continue=row['safe_to_continue'])),flush=True)
    return results

def collect(root,plan):
    ledger=[];metrics=[];overheads=[];timings=[]
    for slot in plan['schedule']:
        d=root/slot['session'];entry=dict(**slot,status='missing',eligible=False)
        if not (d/'outcome.json').exists():ledger.append(entry);continue
        try:
            check=check_run(d);o=read(d/'outcome.json');m=read(d/'run-manifest.json');source=read(d/'source-manifest.json')
            entry.update(status=o['status'],integrity=check,eligible=check['ok'] and o['status']=='completed',outcome=o,
                source_commit=source.get('Commit'),source_dirty=source.get('Dirty'),controller_sha256=m['controller_sha256'])
            if plan.get('provenance') and (source.get('Commit')!=plan['provenance']['commit'] or source.get('Dirty') is not False):entry.update(eligible=False,source_mismatch=True)
            events=[json.loads(l) for l in (d/'trial-events.jsonl').read_text().splitlines()]
            if entry['eligible']:
                for phase in o['phases']:
                    for metric in phase['metrics']:
                        metrics.append(dict(**slot,phase=phase['phase'],feature=metric['feature'],requests=metric['count'],p50_ms=metric['p50_ms'],p95_ms=metric['p95_ms'],
                            background_units_per_second=phase['background_units_per_second']))
            bounds=[]
            for start in (r for r in events if r['kind']=='phase-start'):
                end=next((r for r in events if r['kind']=='phase-end' and r['data']['phase']==start['data']['phase']),None)
                if end:bounds.append((utc(start['time_utc']),utc(end['time_utc']),start['data']['phase']))
            groups={}
            for p in (d/'independent').glob('*.raw.jsonl'):
                for line in p.read_text().splitlines():
                    sample=json.loads(line);phase=next((n for a,b,n in bounds if a<=utc(sample['time_utc'])<=b),'outside-measured-phase')
                    groups.setdefault((phase,sample['role']),[]).append(sample)
            for (phase,role),samples in groups.items():
                ok=[r for r in samples if r.get('status')=='ok'];values=lambda key:[r[key] for r in ok if r.get(key) is not None]
                cpu=values('cpu_percent');private=values('private_bytes');ws=values('working_set_bytes')
                overheads.append(dict(**slot,phase=phase,role=role,eligible=entry['eligible'],samples=len(samples),cpu_percent_mean=statistics.mean(cpu) if cpu else None,
                    private_bytes_max=max(private,default=None),working_set_bytes_max=max(ws,default=None)))
            t=read(d/'overhead-summary.json');times=t.get('policy_timing_ms') or [];audit=t.get('audit_write_ms') or []
            timings.append(dict(**slot,decision_count=len(times),decision_mean_ms=statistics.mean(times) if times else None,decision_max_ms=max(times,default=None),
                audit_count=len(audit),audit_mean_ms=statistics.mean(audit) if audit else None,journal_wall_ms=read(d/'completion.json')['log_write_ms']))
        except (OSError,ValueError,KeyError,TypeError,StopIteration) as error:entry.update(eligible=False,error=repr(error))
        ledger.append(entry)
    return dict(ledger=ledger,metrics=metrics,overheads=overheads,timings=timings)

def compare(rows,iterations=10000,seed=20261112):
    result=[]
    groups=sorted({(r['app'],r['phase'],r['feature']) for r in rows})
    for app,phase,feature in groups:
        group=[r for r in rows if (r['app'],r['phase'],r['feature'])==(app,phase,feature)]
        for baseline,candidate in itertools.combinations('ABC',2):
            for metric in ('p95_ms','background_units_per_second'):
                left={r['block']:r[metric] for r in group if r['condition']==baseline};right={r['block']:r[metric] for r in group if r['condition']==candidate}
                matched=sorted(left.keys()&right.keys());identity=(app,phase,feature,baseline,candidate,metric)
                derived_seed=int.from_bytes(hashlib.sha256(json.dumps([seed,identity]).encode()).digest()[:8],'big')
                summary=paired_block_bootstrap([(left[b],right[b]) for b in matched],iterations=iterations,seed=derived_seed)
                result.append(dict(app=app,phase=phase,feature=feature,baseline=baseline,candidate=candidate,metric=metric,blocks=matched,
                    missing_pair_blocks=sorted(left.keys()^right.keys()),**summary))
    return result

def analyze(root,plan):
    output=root/'analysis';output.mkdir();data=collect(root,plan);comparisons=compare(data['metrics'],plan['bootstrap_iterations'],plan['seed'])
    data.update(comparisons=comparisons,protocol=PROTOCOL,analysis_unit='Paired session block, never requests',
        inference='Exploratory unadjusted marginal block-bootstrap CI only. No generated p-values, equivalence, CARP superiority, or acceptance claim. Practical latency tolerance not established.',
        planned_sessions=len(plan['schedule']),eligible_sessions=sum(r['eligible'] for r in data['ledger']))
    save_new(output/'summary.json',data)
    for key in ('ledger','metrics','comparisons','overheads','timings'):_write_csv(output/(key+'.csv'),data[key])
    lines=['# RAM Flow 일괄 A/B/C 결과','',f"예정 {data['planned_sessions']}세션 / 적격 {data['eligible_sessions']}세션. 단일 PC, 명시적 정상 QoS 입력층, 기존 V1 비교.",'',
        '분석 단위는 대응 세션 블록이다. 요청 수는 독립 반복 수가 아니다. 95% CI는 탐색적·다중성 미보정이며 동등성/유의성/우수성 판정이 아니다. 실용적 지연 허용 범위는 확정하지 않았다. 모든 실패/결측은 ledger.csv에 남는다.','',
        '| 앱·기능 (Contention) | 조건 | 세션 수 | 블록 p95 평균 ms | 배경 처리율 평균 단위/초 |','|---|---|---:|---:|---:|']
    bars=[]
    for app,feature in sorted({(r['app'],r['feature']) for r in data['metrics']}):
        for condition in 'ABC':
            group=[r for r in data['metrics'] if r['app']==app and r['feature']==feature and r['phase']=='Contention' and r['condition']==condition]
            if not group:continue
            fg=statistics.mean(r['p95_ms'] for r in group);bg=statistics.mean(r['background_units_per_second'] for r in group)
            lines.append(f'| {app} {feature} | {condition} | {len(group)} | {fg:.3f} | {bg:.1f} |');bars.append((app+' '+feature+' '+condition,fg))
    lines+=['','| C − B (Contention) | 지표 | 대응 블록 | 평균 차이 | 95% CI |','|---|---|---:|---:|---|']
    for r in comparisons:
        if (r['baseline'],r['candidate'],r['phase'])==('B','C','Contention'):
            ci=r['difference_ci95'];lines.append(f"| {r['app']} {r['feature']} | {r['metric']} | {r['n_blocks']} | {_fmt(r['difference'])} | {ci} |")
    lines+=['','A 엔진/트레이 미실행, B 고정 V1 조합, C 기존 V1. 엔진 CPU는 실행기와 합산이다. CPU/RAM/혼합 전체나 CARP 결과로 확장하지 않는다. 연속 실행의 블록 독립성·시간 추세·carryover는 자동으로 보장되지 않는다. 반복 전환과 무효/회복 실패도 원시 기록을 확인한다.','']
    (output/'REPORT.ko.md').write_text('\n'.join(lines),encoding='utf-8')
    (output/'foreground-p95.svg').write_text(bars_svg('Contention foreground p95','Equal weight across session blocks; descriptive only',bars,'ms'),encoding='utf-8')
    return data

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--output',required=True,type=Path);p.add_argument('--blocks',type=int,default=6);p.add_argument('--seed',type=int,default=20261112)
    p.add_argument('--phase-seconds',type=int,default=10);p.add_argument('--contention-threads',type=int,default=14);p.add_argument('--bootstrap-iterations',type=int,default=10000);p.add_argument('--plan-only',action='store_true')
    args=p.parse_args();lab=REPO/'scripts/ResearchLab/bin/Release/net10.0-windows/ResearchLab.exe';root=args.output.resolve()
    if not 5<=args.phase_seconds<=120 or not 1<=args.contention_threads<=16 or not 100<=args.bootstrap_iterations<=100000:p.error('Invalid bounded phase/thread/bootstrap parameter')
    try:slots=schedule(args.blocks,args.seed);source=provenance(lab)
    except (ValueError,OSError,subprocess.SubprocessError) as error:p.error(str(error))
    root.mkdir(parents=True,exist_ok=False)
    plan=dict(protocol=PROTOCOL,batch_schema=1,created_utc=datetime.now(timezone.utc).isoformat(),schedule=slots,blocks=args.blocks,seed=args.seed,phase_seconds=args.phase_seconds,
        contention_threads=args.contention_threads,bootstrap_iterations=args.bootstrap_iterations,provenance=source,primary_contrast='C minus B, Contention; report foreground p95 and background throughput jointly',
        scope='Single-PC repeated feasibility comparison, not powered confirmatory study; no automatic tuning',stop_rule='No retries; stop on unsafe/unverifiable cleanup or two consecutive failed sessions',
        input_qos='explicit-normal-common-in-A-B-C',foreground_tolerance_ms=None)
    save_new(root/'batch-plan.json',plan);save_new(root/'batch-plan-sha256.json',dict(sha256=file_hash(root/'batch-plan.json')))
    if args.plan_only:print('Plan only; no sessions run');return 0
    command=[sys.executable,str(REPO/'scripts/research_cpu_preflight.py'),'--output',str(root/'admission'),'--threads',str(args.contention_threads),'--qos-mode','normal']
    admitted=subprocess.run(command,cwd=REPO).returncode==0
    if not admitted:
        save_new(root/'batch-result.json',dict(status='admission_failed',sessions_run=0));return 1
    proof=verify_preflight(root/'admission',lab,args.contention_threads);save_new(root/'admission-proof.json',proof)
    def execute(slot):
        verify_frozen(plan)
        if read(root/'batch-plan-sha256.json')['sha256']!=file_hash(root/'batch-plan.json'):raise ValueError('Plan changed after freeze')
        d=root/slot['session']
        argv=[str(lab),'app-session','--protocol',PROTOCOL,'--app',slot['app'],'--condition',slot['condition'],'--output',str(d),'--phase-seconds',str(args.phase_seconds),'--contention-threads',str(args.contention_threads)]
        with (root/(slot['session']+'.stdout.txt')).open('x') as out,(root/(slot['session']+'.stderr.txt')).open('x') as err:
            child=subprocess.Popen(argv,cwd=REPO,stdout=out,stderr=err)
            try:return child.wait(timeout=240+3*args.phase_seconds)
            except (KeyboardInterrupt,subprocess.TimeoutExpired):
                if d.exists():save_new(d/'cancel.request',dict(reason='Batch cancellation/timeout; owned engine restores before exit'))
                child.wait(timeout=90)
                raise RuntimeError('Batch interrupted; no later sessions launched')
    results=run_schedule(root,plan,execute);save_new(root/'execution-ledger.json',results)
    data=analyze(root,plan)
    save_new(root/'batch-result.json',dict(status='completed' if data['eligible_sessions']==len(slots) else 'completed_with_failures',eligible_sessions=data['eligible_sessions'],planned_sessions=len(slots),finished_utc=datetime.now(timezone.utc).isoformat()))
    save_new(root/'batch-artifact-index.json',dict(files=[dict(path=f.relative_to(root).as_posix(),bytes=f.stat().st_size,sha256=file_hash(f)) for f in sorted(root.rglob('*')) if f.is_file() and not any(p.startswith('runtime-') for p in f.relative_to(root).parts)]))
    print(json.dumps(dict(event='batch-finished',eligible=data['eligible_sessions'],planned=len(slots),report=str(root/'analysis/REPORT.ko.md'))),flush=True)
    return 0 if data['eligible_sessions']==len(slots) else 1

if __name__=='__main__':raise SystemExit(main())
