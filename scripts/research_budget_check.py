"""Read-only semantic checks for the separate budget-placement protocol."""
import math

PROTOCOL='real-app-budget-v1'
MASK={0,1,2,5,6,10,11,15,16}


def exposure(start,end,writes,original):
    total=0.;cursor=start;active=False
    for row in sorted(writes,key=lambda r:r['readback_clock_seconds']):
        stamp=row['readback_clock_seconds']
        if not start<=stamp<=end:continue
        if active:total+=stamp-cursor
        active=row['observed']!=original;cursor=stamp
    if active:total+=end-cursor
    return total,active


def pair_blocks(ledger):
    groups={}
    for r in ledger:
        if r['eligible']:groups.setdefault((r['app'],r['block']),{})[r['condition']]=r['dose']
    return {k for k,g in groups.items() if set(g)=={'B','C'} and abs(g['B']-g['C'])<=.15}


def budget_errors(records,outcome,baseline,result,original):
    errors=[]
    def near(a,b):return isinstance(a,(int,float)) and not isinstance(a,bool) and math.isfinite(a) and math.isclose(a,b,abs_tol=1e-8,rel_tol=1e-9)
    def rows(kind):return [r['data'] for r in records if r['kind']==kind]
    starts,ends,epochs,stops=rows('budget-start'),rows('budget-end'),rows('budget-epoch'),rows('budget-stop-deadline')
    if len(starts)!=1 or len(ends)!=1 or len(epochs)!=20 or len(stops)!=1:return ['budget_lifecycle_missing']
    start,end=starts[0],ends[0]
    if (start.get('epochs'),start.get('quota'),start.get('epoch_seconds'))!=(20,9,.5):errors.append('budget_parameters_changed')
    if [r.get('decision',{}).get('epoch') for r in epochs]!=list(range(20)):errors.append('budget_epoch_sequence')
    if result.get('protocol')!=PROTOCOL or outcome.get('protocol')!=PROTOCOL:errors.append('budget_protocol_mismatch')
    if rows('budget-baseline-frozen')!=[baseline]:errors.append('budget_baseline_sidecar_mismatch')
    if baseline.get('phase')!='Normal' or len(baseline.get('p95_ms',{}))!=2:errors.append('budget_baseline_features')
    normal=next((p for p in outcome['phases'] if p['phase']=='Normal'),{})
    for metric in normal.get('metrics',[]):
        k=metric['feature']
        if baseline.get('counts',{}).get(k)!=metric['count'] or metric['count']<20 or not near(baseline.get('p95_ms',{}).get(k),metric['p95_ms']):errors.append('budget_baseline_not_raw_normal')
    sources={r['id']:r for r in rows('app-observation')};observed=rows('budget-observation')
    by_id={r['id']:r for r in observed}
    for r in observed:
        source=sources.get(r['id'],{})
        if any(r.get(k)!=source.get(k) for k in ('feature','phase','latency_ms')):errors.append('budget_feedback_not_raw_response')
    mode='fixed' if outcome['condition']=='B' else 'feedback'
    used=0;latched=False;prior=False;run_length=0;rotation=None;lag=0.
    for i,r in enumerate(epochs):
        d=r['decision'];actual=r['actual_seconds'];due=start['clock_seconds']+i*.5
        if not near(r['due_seconds'],due) or actual<due:errors.append('budget_deadline_clock')
        lag=max(lag,actual-due)
        if d.get('mode')!=mode or type(d.get('rotation')) is not int or not 0<=d['rotation']<20:errors.append('budget_mode_rotation')
        if rotation is None:rotation=d.get('rotation')
        elif d.get('rotation')!=rotation:errors.append('budget_rotation_changed')
        risks=[];fresh=True
        for feature,base in baseline['p95_ms'].items():
            o=d['observations'][feature];source_id=o['source_request_id'];ema=1.
            history=sorted((x for x in observed if x['feature']==feature and x['id']<=source_id and x['phase']!='Normal'),key=lambda x:x['id'])
            for h in history:ema=.3*(h['latency_ms']/base)+.7*ema
            seen=baseline['clock_seconds'] if source_id==0 else by_id[source_id]['clock_seconds']
            if not near(o['baseline_p95_ms'],base) or not near(o['normalized_ema'],ema) or not near(o['seen_seconds'],seen):errors.append('budget_feedback_replay_mismatch')
            fresh &= 0<=actual-seen<=1.5;risks.append(ema)
        risk=max(risks)
        if not near(d.get('risk'),risk) or d.get('fresh')!=fresh:errors.append('budget_risk_or_freshness_mismatch')
        selected=False
        if not fresh:reason='missing-or-stale-response'
        elif used>=9:reason='quota-exhausted'
        else:
            if risk>=1.25:latched=True
            elif risk<=1.10:latched=False
            if mode=='fixed':selected=(i+rotation)%20 in MASK;reason='fixed-periodic'
            else:
                selected=latched or (prior and run_length<2)
                reason=('response-risk' if latched else 'minimum-hold') if selected else 'response-low'
                if 9-used>=20-i:selected=True;reason='common-quota-tail'
            used+=int(selected)
        if d.get('selected')!=selected or d.get('reason')!=reason or d.get('used')!=used or d.get('quota')!=9:errors.append('budget_selection_replay_mismatch')
        run_length=(run_length+1 if prior else 1) if selected else 0;prior=selected
    stop=stops[0]
    if not near(stop.get('due_seconds'),start['clock_seconds']+10) or stop['actual_seconds']<stop['due_seconds']:errors.append('budget_stop_deadline_clock')
    lag=max(lag,stop['actual_seconds']-stop['due_seconds'])
    on,active=exposure(start['clock_seconds'],end['clock_seconds'],rows('resource-write-readback'),original['values'])
    if not near(result.get('actual_readback_on_seconds'),on) or not near(result.get('max_deadline_lag_seconds'),lag) or not near(end.get('max_deadline_lag_seconds'),lag) or not near(result.get('start_seconds'),start['clock_seconds']) or not near(result.get('end_seconds'),end['clock_seconds']) or not near(result.get('requested_on_seconds'),4.5):errors.append('budget_actual_dose_summary_mismatch')
    if any(r.get('phase')!='Contention' for r in starts+ends+epochs+stops):errors.append('budget_control_outside_contention')
    if any(r.get('phase')!='Contention' and r['observed']!=original['values'] for r in rows('resource-write-readback')):errors.append('budget_intervention_outside_contention')
    if result.get('used')!=used or end.get('used')!=used or used!=9 or result.get('quota_complete') is not True or end.get('quota_complete') is not True:errors.append('budget_quota_incomplete')
    if result.get('matched_session') is not True or abs(on-4.5)>.1 or result.get('max_deadline_lag_seconds',1)>.1 or active:errors.append('budget_exposure_or_deadline_invalid')
    return sorted(set(errors))
