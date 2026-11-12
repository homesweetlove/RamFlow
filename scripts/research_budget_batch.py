"""Bounded equal-exposure placement study. No retries, tuning or efficacy verdict."""
import argparse
from datetime import datetime,timezone
import json
from pathlib import Path
import random
import statistics
import subprocess
import sys

from research_app_batch import REPO,collect,compare,file_hash,provenance,run_schedule,verify_frozen
from research_app_pilot import save_new,verify_preflight
from research_app_analyze import read
from research_analyze import _write_csv,bars_svg
from research_budget_check import PROTOCOL,pair_blocks


def schedule(seed):
    rng=random.Random(seed);orders={}
    for app in ('chrome','code'):
        orders[app]=['BC']*3+['CB']*3;rng.shuffle(orders[app])
    result=[]
    for block in range(1,7):
        apps=['chrome','code'];rng.shuffle(apps)
        for app in apps:
            rotation=rng.randrange(20)
            for position,c in enumerate(orders[app][block-1],1):
                result.append(dict(block=block,app=app,condition=c,position=position,rotation=rotation,session=f'b{block:02d}/{app}-{c}'))
    return result


def analyze(root,plan):
    output=root/'analysis';output.mkdir();data=collect(root,plan)
    for r in data['ledger']:
        path=root/r['session']/'budget-result.json'
        r['dose']=read(path)['actual_readback_on_seconds'] if path.exists() else None
        o=r.get('outcome',{})
        if o and (o.get('condition')!=r['condition'] or o.get('application')!=r['app'] or o.get('protocol')!=PROTOCOL):r.update(eligible=False,slot_mismatch=True)
    pairs=pair_blocks(data['ledger'])
    matched=[r for r in data['metrics'] if (r['app'],r['block']) in pairs]
    comparisons=[r for r in compare(matched,plan['bootstrap_iterations'],plan['seed']) if r['baseline']=='B' and r['candidate']=='C']
    data.update(protocol=PROTOCOL,comparisons=comparisons,matched_blocks=[dict(app=a,block=b) for a,b in sorted(pairs)],
        analysis_unit='Paired session block; requests are subsamples',planned_sessions=len(plan['schedule']),eligible_sessions=sum(r['eligible'] for r in data['ledger']),
        inference='Exploratory marginal unadjusted 95% block-bootstrap intervals. No equivalence/SLO claim, full CARP validation or acceptance guarantee. Equal wall-time exposure does not imply equal throughput loss.')
    save_new(output/'summary.json',data)
    for k in ('ledger','metrics','comparisons','overheads','timings'):_write_csv(output/(k+'.csv'),data[k])
    lines=['# 동일 개입 시간 예산 배치 비교','',f"예정 {len(plan['schedule'])}세션 / 세션 적격 {data['eligible_sessions']} / 시간 예산 대응 블록 {len(pairs)}개(앱별 합계).",'',
        'B=고정 주기, C=응답 피드백. 둘 다 9/20개의 0.5초 epoch를 요청한다. 실제 readback 상태 시간 차이 ≤0.15초인 대응 블록만 비교한다. 동일 배경 손실·동등 응답성을 보장하지 않는다. 95% CI는 탐색적·다중성 미보정. 이전 V1 A/B/C 자료와 합산하지 않는다.','',
        '| 앱·기능 (Contention) | B p95 ms | C p95 ms | B 배경 단위/초 | C 배경 단위/초 | 대응 블록 |','|---|---:|---:|---:|---:|---:|']
    bars=[]
    for app,feature in sorted({(r['app'],r['feature']) for r in matched}):
        group=[r for r in matched if r['app']==app and r['feature']==feature and r['phase']=='Contention']
        b=[r for r in group if r['condition']=='B'];c=[r for r in group if r['condition']=='C']
        if not b or not c:continue
        mean=lambda rs,k:statistics.mean(r[k] for r in rs)
        lines.append(f"| {app} {feature} | {mean(b,'p95_ms'):.3f} | {mean(c,'p95_ms'):.3f} | {mean(b,'background_units_per_second'):.1f} | {mean(c,'background_units_per_second'):.1f} | {len(b)} |")
        bars.extend([(app+' '+feature+' B',mean(b,'p95_ms')),(app+' '+feature+' C',mean(c,'p95_ms'))])
    lines+=['','| C − B | 지표 | 대응 블록 | 평균 차이 | 95% CI |','|---|---|---:|---:|---|']
    for r in comparisons:
        if r['phase']=='Contention':lines.append(f"| {r['app']} {r['feature']} | {r['metric']} | {r['n_blocks']} | {r['difference']} | {r['difference_ci95']} |")
    lines+=['','실패·무효는 ledger.csv와 원시 outcome에 보존한다. 0.1초 deadline/세션 dose 허용치 및 대응 0.15초 허용치는 측정 전 고정했다. 꼬리 강제 할당은 동일 요청 예산을 소비하기 위한 설계이므로 무개입 최적화·제품 정책으로 해석하지 않는다.','']
    (output/'REPORT.ko.md').write_text('\n'.join(lines),encoding='utf-8')
    (output/'foreground-p95.svg').write_text(bars_svg('Budget placement: foreground p95','Matched session blocks; descriptive only',bars,'ms'),encoding='utf-8')
    return data


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--output',required=True,type=Path);p.add_argument('--seed',type=int,default=20261113);p.add_argument('--plan-only',action='store_true')
    args=p.parse_args();lab=REPO/'scripts/ResearchLab/bin/Release/net10.0-windows/ResearchLab.exe';root=args.output.resolve()
    source=provenance(lab)
    for name in ('research_budget_batch.py','research_budget_check.py'):source['files']['scripts/'+name]=file_hash(REPO/'scripts'/name)
    root.mkdir(parents=True,exist_ok=False)
    plan=dict(protocol=PROTOCOL,created_utc=datetime.now(timezone.utc).isoformat(),schedule=schedule(args.seed),seed=args.seed,phase_seconds=10,contention_threads=14,
        bootstrap_iterations=10000,provenance=source,blocks_per_app=6,primary_contrast='C minus B, Contention: p95 and background throughput jointly',
        response_baseline='Own Normal p95, >=20 per feature',ema_alpha=.3,entry_ratio=1.25,exit_ratio=1.10,min_hold_epochs=2,max_response_age_seconds=1.5,
        epochs=20,epoch_seconds=.5,quota=9,fixed_mask=[0,1,2,5,6,10,11,15,16],max_deadline_lag_seconds=.1,dose_tolerance_seconds=.1,pair_dose_difference_seconds=.15,
        foreground_tolerance_ms=None,scope='Single-PC CPU-only placement feasibility; not full CARP or powered confirmation',stop_rule='No retries; stop on unsafe/unverifiable run or two consecutive failures')
    save_new(root/'batch-plan.json',plan);save_new(root/'batch-plan-sha256.json',dict(sha256=file_hash(root/'batch-plan.json')))
    if args.plan_only:print('Plan only; no live sessions');return 0
    command=[sys.executable,str(REPO/'scripts/research_cpu_preflight.py'),'--output',str(root/'admission'),'--threads','14','--qos-mode','normal']
    if subprocess.run(command,cwd=REPO).returncode:
        save_new(root/'batch-result.json',dict(status='admission_failed',sessions_run=0));return 1
    save_new(root/'admission-proof.json',verify_preflight(root/'admission',lab,14))
    def execute(slot):
        verify_frozen(plan)
        if read(root/'batch-plan-sha256.json')['sha256']!=file_hash(root/'batch-plan.json'):raise ValueError('Plan changed')
        d=root/slot['session']
        argv=[str(lab),'app-session','--protocol',PROTOCOL,'--app',slot['app'],'--condition',slot['condition'],'--output',str(d),'--phase-seconds','10','--contention-threads','14','--budget-rotation',str(slot['rotation'])]
        with (root/(slot['session']+'.stdout.txt')).open('x') as out,(root/(slot['session']+'.stderr.txt')).open('x') as err:
            child=subprocess.Popen(argv,cwd=REPO,stdout=out,stderr=err)
            try:return child.wait(timeout=270)
            except (KeyboardInterrupt,subprocess.TimeoutExpired):
                if d.exists():save_new(d/'cancel.request',dict(reason='Restore and seal; no retry'))
                child.wait(timeout=90);raise RuntimeError('Batch cancelled; no next session')
    results=run_schedule(root,plan,execute);save_new(root/'execution-ledger.json',results);data=analyze(root,plan)
    save_new(root/'batch-result.json',dict(status='completed' if data['eligible_sessions']==24 else 'completed_with_failures',eligible_sessions=data['eligible_sessions'],planned_sessions=24,matched_blocks=len(data['matched_blocks'])))
    save_new(root/'batch-artifact-index.json',dict(files=[dict(path=f.relative_to(root).as_posix(),bytes=f.stat().st_size,sha256=file_hash(f)) for f in sorted(root.rglob('*')) if f.is_file() and not any(p.startswith('runtime-') for p in f.relative_to(root).parts)]))
    print(json.dumps(dict(event='batch-finished',eligible=data['eligible_sessions'],planned=24,matched_blocks=len(data['matched_blocks']))),flush=True)
    return 0 if data['eligible_sessions']==24 else 1

if __name__=='__main__':raise SystemExit(main())
