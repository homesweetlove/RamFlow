"""Read-only integrity/completion gate, not a performance or scientific-validity test."""
import argparse
import csv
from datetime import datetime
import hashlib
import json
import math
from pathlib import Path, PurePosixPath

COMPLETED_REQUIRED=('started.json','pre-intervention.json','effective-settings.json','resource-original.json','resource-final.json','cost-observation.json')

def app_profile_errors(records,outcome):
    """Semantic gate in addition to the fd17548 byte/index/lifecycle gate."""
    errors=[]
    def payload(kind):return [r.get('data',{}) for r in records if r.get('kind')==kind]
    phases=['Normal','Contention','Recovery'];starts=payload('phase-start');ends=payload('phase-end')
    if [r.get('phase') for r in starts]!=phases or [r.get('phase') for r in ends]!=phases:
        errors.append('app_phases_missing_or_out_of_order');return errors
    boundaries=[(r['kind'],r['data']['phase']) for r in records if r.get('kind') in ('phase-start','phase-end')]
    if boundaries!=[(k,p) for p in phases for k in ('phase-start','phase-end')]:errors.append('app_phase_boundaries_overlap')
    observed=payload('app-observation');summaries=outcome.get('phases',[])
    if [r.get('phase') for r in summaries]!=phases:errors.append('app_summary_phases_mismatch');return errors
    if len(observed)!=outcome.get('observations'):errors.append('app_observation_count_mismatch')
    def near(a,b):return isinstance(a,(int,float)) and not isinstance(a,bool) and math.isfinite(a) and math.isclose(a,b,rel_tol=1e-10,abs_tol=1e-9)
    def quantile(values,p):
        values=sorted(values);x=(len(values)-1)*p;lo=int(x);hi=math.ceil(x)
        return values[lo]+(values[hi]-values[lo])*(x-lo)
    ready=payload('app-ready');features=ready[0].get('features',[]) if len(ready)==1 else []
    expected=['search','sort'] if outcome.get('application')=='chrome' else ['completion','save']
    if features!=expected:errors.append('app_features_missing_or_wrong')
    active=None;positions={p:[] for p in phases}
    for row in records:
        if row.get('kind')=='phase-start':active=row['data']['phase']
        if row.get('kind')=='app-observation':
            item=row['data'];p=item.get('phase')
            if p!=active or p not in phases:errors.append('app_observation_outside_phase')
            elif item.get('feature') not in features:errors.append('app_unknown_feature')
            else:positions[p].append(item)
            if item.get('correct') is not True:errors.append('app_incorrect_result')
            for boundary in ('foreground_before','foreground_after'):
                fg=item.get('reply',{}).get(boundary,{})
                if fg.get('owned') is not True or not isinstance(fg.get('pid'),int) or fg['pid']<=0 or not isinstance(fg.get('creation_filetime'),int):errors.append('app_foreground_unconfirmed')
        if row.get('kind')=='phase-end':active=None
    for start,end,summary in zip(starts,ends,summaries):
        p=start['phase'];duration=end['seconds']-start['seconds'];units=end['units']-start['units']
        if duration<=0 or units<0 or not near(summary.get('background_seconds'),duration) or summary.get('background_units')!=units or not near(summary.get('background_units_per_second'),units/duration):errors.append('app_throughput_summary_mismatch:'+p)
        metrics=summary.get('metrics',[])
        if [m.get('feature') for m in metrics]!=features:errors.append('app_metric_features_mismatch:'+p)
        for m in metrics:
            rows=[r for r in positions[p] if r['feature']==m['feature']];values=[r.get('latency_ms') for r in rows]
            if len(rows)<12 or not all(isinstance(v,(int,float)) and math.isfinite(v) and v>=0 for v in values):errors.append('app_invalid_or_insufficient_requests:'+p);continue
            if m.get('latencies_ms')!=values or m.get('count')!=len(rows) or m.get('roundtrip_ms')!=[r.get('roundtrip_ms') for r in rows] or not near(m.get('p50_ms'),quantile(values,.5)) or not near(m.get('p95_ms'),quantile(values,.95)):errors.append('app_latency_summary_mismatch:'+p)
    writes=payload('resource-write-readback')
    def equivalent(a,b):return isinstance(a,dict) and isinstance(b,dict) and all(a.get(k)==b.get(k) and k in a for k in ('MemoryPriority','CpuPriority','PowerControl','PowerState','CpuSets'))
    for item in writes:
        if item.get('phase') not in phases+['cleanup'] or item.get('api_result') is not True or item.get('matched') is not True or not equivalent(item.get('requested'),item.get('observed')):errors.append('app_readback_mismatch')
    if outcome.get('condition')=='A' and writes:errors.append('app_A_was_modified')
    if outcome.get('condition') in ('B','C'):
        active_writes=[r for r in writes if (r.get('requested') or {}).get('CpuPriority')==0x4000 and (r.get('requested') or {}).get('PowerState',0)&1]
        if not active_writes or any(r['requested'].get('MemoryPriority')!=3 or not r['requested'].get('PowerControl',0)&1 for r in active_writes):errors.append('app_v1_matched_bundle_not_demonstrated')
    if outcome.get('readback_failures')!=0 or outcome.get('protected_misinterventions')!=0 or payload('out-of-scope-write'):errors.append('app_safety_or_readback_failure')
    cleanup=payload('app-cleanup')
    if len(cleanup)!=1 or cleanup[0].get('cleanup',{}).get('completed') is not True or cleanup[0].get('cleanup',{}).get('remaining_pids')!=[]:errors.append('app_cleanup_unconfirmed')
    return errors

def check_run(directory):
    root=Path(directory).resolve();errors=[];warnings=[];status='unfinalized';kind='trial';records=[]
    def read(name):return json.loads((root/name).read_text(encoding='utf-8-sig'))
    def fail(text):errors.append(text)
    try:
        for name in ('run-manifest.json','completion.json','artifact-index.json','trial-events.jsonl'):
            if not (root/name).is_file():fail('missing:'+name)
        if errors:return dict(ok=False,status=status,performance_ready=False,errors=errors,warnings=warnings,root=str(root))
        start,done,index=read('run-manifest.json'),read('completion.json'),read('artifact-index.json')
        run_id=start['run_id'];status=done['status'];kind='calibration-batch' if start.get('args',[None])[0:1]==['calibrate'] else 'trial'
        profile=start.get('evidence_profile','synthetic-trial-v1')
        if profile in ('real-app-abc-v1','real-app-abc-v2','real-app-abc-v3'):kind='real-app-smoke'
        elif profile!='synthetic-trial-v1':fail('unknown_evidence_profile')
        if done.get('run_id')!=run_id or index.get('run_id')!=run_id:fail('run_identity_mismatch')
        if any(x.get('evidence_schema_version')!=1 for x in (start,done,index)):fail('unsupported_evidence_schema')
        if done.get('logging_fault') is not False:fail('logging_fault_or_missing_flag')
        if status not in ('completed','failed','invalid','aborted'):fail('unknown_terminal_status')
        if status=='completed' and done.get('cleanup_completed') is not True:fail('cleanup_not_confirmed')
        indexed=set()
        for item in index['files']:
            name=item['path'];relative=PurePosixPath(name.replace('\\','/'))
            if relative.is_absolute() or '..' in relative.parts or ':' in name or name in indexed or not relative.parts:
                fail('unsafe_or_duplicate_index_path:'+name);continue
            indexed.add(name);path=root.joinpath(*relative.parts)
            if path.is_symlink() or not path.resolve().is_relative_to(root):fail('linked_index_path:'+name);continue
            if not path.is_file():
                if item.get('retention')=='hash-only-binary':warnings.append('binary_not_embedded:'+name)
                else:fail('missing_indexed_file:'+name)
                continue
            if path.stat().st_size!=item['bytes'] or hashlib.sha256(path.read_bytes()).hexdigest()!=item['sha256']:fail('hash_or_length_mismatch:'+name)
        required=['run-manifest.json','completion.json','trial-events.jsonl']
        if kind in ('trial','real-app-smoke'):required.append('outcome.json')
        else:required.extend(('manifest.json','run.result.json'))
        if status=='completed' and kind=='trial':required.extend(COMPLETED_REQUIRED)
        if status=='completed' and kind=='real-app-smoke':required.extend(('started.json','effective-settings.json','resource-original.json','resource-final.json','source-manifest.json','overhead-summary.json','app-driver.jsonl','app-driver.stderr.jsonl'))
        if status=='completed' and profile in ('real-app-abc-v2','real-app-abc-v3'):required.append('pre-intervention.json')
        for name in required:
            if name not in indexed or not (root/name).is_file():fail('required_not_indexed:'+name)
        # Unindexed additions are not silently counted as part of the sealed run.
        for p in root.rglob('*'):
            if p.is_symlink():fail('linked_evidence:'+p.name)
            if p.is_file() and p.relative_to(root).as_posix() not in indexed|{'artifact-index.json'}:fail('unindexed_file:'+p.relative_to(root).as_posix())
        stream=(root/'trial-events.jsonl').read_bytes()
        if not stream.endswith(b'\n'):fail('partial_event_tail')
        previous=-1;pending=set()
        for i,line in enumerate(stream.decode('utf-8').splitlines(),1):
            row=json.loads(line);records.append(row)
            if row.get('sequence')!=i or row.get('run_id')!=run_id:fail('event_identity_or_sequence:'+str(i))
            tick=row.get('monotonic_ticks')
            if type(tick) is not int or tick<previous:fail('event_clock:'+str(i))
            else:previous=tick
            stamp=datetime.fromisoformat(row['time_utc'].replace('Z','+00:00'))
            if stamp.tzinfo is None:fail('event_timezone_missing:'+str(i))
            payload=row.get('data') or {}
            if row.get('kind') in ('request-begin','request-end','request-error'):
                key=(payload.get('pid'),payload.get('creation_filetime'),payload.get('id'))
                if not all(type(v) is int and v>0 for v in key):fail('request_identity_invalid')
                if row['kind']=='request-begin':
                    if key in pending:fail('duplicate_request_begin')
                    pending.add(key)
                else:
                    if key not in pending:fail('request_end_without_begin')
                    pending.discard(key)
        if not records or records[0].get('kind')!='run-start' or records[-1].get('kind')!='terminal':fail('missing_lifecycle_boundary')
        elif records[-1].get('data',{}).get('status')!=status:fail('terminal_status_mismatch')
        if done.get('records')!=len(records):fail('event_count_mismatch')
        if status=='completed' and pending:fail('unfinished_requests')
        if kind in ('trial','real-app-smoke'):
            outcome=read('outcome.json')
            if outcome.get('status')!=status:fail('outcome_completion_mismatch')
            if status=='completed' and (outcome.get('restored') is not True):fail('restoration_not_confirmed')
            if outcome.get('evidence_run_id') is not None and outcome['evidence_run_id']!=run_id:fail('outcome_run_identity_mismatch')
            if status=='completed' and 'foreground_latency_ms' in outcome:
                observed=[r['data'] for r in records if r.get('kind')=='foreground-observation']
                if [r.get('index') for r in observed]!=list(range(1,len(observed)+1)) or [r.get('latency_ms') for r in observed]!=outcome['foreground_latency_ms'] or [r.get('roundtrip_ms') for r in observed]!=outcome['foreground_wall_ms']:
                    fail('raw_foreground_summary_mismatch')
                if not any(r.get('kind')=='measurement-end' for r in records):fail('missing_measurement_end')
            if status=='completed' and kind=='real-app-smoke':
                errors.extend(app_profile_errors(records,outcome))
                if profile in ('real-app-abc-v2','real-app-abc-v3'):
                    baseline=read('pre-intervention.json')
                    if baseline.get('Eligible') is not True or baseline.get('Stable') is not True or baseline.get('Attained') is not True or baseline.get('Regime')!=1:fail('app_normal_baseline_not_confirmed')
                original,final=read('resource-original.json'),read('resource-final.json')
                if final.get('values')!=original.get('values') or final.get('restored') is not True or final.get('cleanup_completed') is not True:fail('app_original_final_readback_mismatch')
                for item in (r['data'] for r in records if r['kind']=='resource-write-readback'):
                    if item.get('pid')!=original.get('pid') or item.get('creation_filetime')!=original.get('creation_filetime'):fail('app_write_target_identity_mismatch')
                driver_rows=[json.loads(line) for line in (root/'app-driver.jsonl').read_text(encoding='utf-8-sig').splitlines()]
                replies={r['id']:r for r in driver_rows if r.get('kind')=='observation'}
                for item in (r['data'] for r in records if r['kind']=='app-observation'):
                    if replies.get(item.get('id'))!=item.get('reply'):fail('app_driver_raw_observation_mismatch')
                bg_rows=[json.loads(line) for line in (root/'background-raw.jsonl').read_text(encoding='utf-8-sig').splitlines()]
                for boundary in (r['data'] for r in records if r['kind'] in ('phase-start','phase-end')):
                    if not any(r.get('Id',0)>0 and r.get('Units')==boundary.get('units') and r.get('Seconds')==boundary.get('seconds') and r.get('Phase')==boundary.get('phase') for r in bg_rows):fail('app_background_raw_boundary_mismatch')
                    if profile=='real-app-abc-v3':
                        expected=read('started.json').get('expected_background_checksum')
                        if not expected or boundary.get('work_checksum')!=expected or not any(r.get('Id',0)>0 and r.get('Units')==boundary.get('units') and r.get('Seconds')==boundary.get('seconds') and r.get('Checksum')==expected for r in bg_rows):fail('app_background_work_checksum_mismatch')
        audit=root/'audit/events.jsonl'
        if audit.is_file():
            events=[json.loads(line) for line in audit.read_text(encoding='utf-8-sig').splitlines()]
            csv_path=root/'audit/events.csv'
            if not csv_path.is_file():fail('missing_audit_csv')
            else:
                with csv_path.open(encoding='utf-8-sig',newline='') as source:rows=list(csv.DictReader(source))
                seq=[e.get('Sequence') for e in events]
                if seq!=list(range(1,len(events)+1)) or [r.get('sequence') for r in rows]!=[str(n) for n in seq]:fail('audit_csv_json_sequence_mismatch')
                mapping={'kind':'Kind','policy':'Policy','version':'Version','settings_sha256':'SettingsSha256','pid':'Pid','creation_filetime':'Identity','result':'Result'}
                if len(rows)==len(events):
                    for number,(row,event) in enumerate(zip(rows,events),1):
                        if any(row.get(column)!=('' if event.get(field) is None else str(event[field])) for column,field in mapping.items()):
                            fail('audit_csv_json_value_mismatch:'+str(number))
                        try:
                            if datetime.fromisoformat(row['time_utc'].replace('Z','+00:00'))!=datetime.fromisoformat(event['Time'].replace('Z','+00:00')):fail('audit_timestamp_mismatch:'+str(number))
                        except (KeyError,TypeError,ValueError):fail('audit_timestamp_missing:'+str(number))
    except (OSError,ValueError,KeyError,TypeError,AttributeError,UnicodeError) as e:fail(type(e).__name__+':'+str(e))
    return dict(ok=not errors,status=status,kind=kind,performance_ready=not errors and kind=='trial' and status=='completed',
                errors=errors,warnings=warnings,root=str(root),records=len(records))

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--input',type=Path,required=True);parser.add_argument('--recursive',action='store_true')
    args=parser.parse_args();roots=sorted(p.parent for p in args.input.rglob('run-manifest.json')) if args.recursive else [args.input]
    results=[check_run(p) for p in roots]
    print(json.dumps(dict(scope='Evidence integrity only; sealed failures are not performance successes',runs=results),ensure_ascii=False,indent=2))
    return 0 if results and all(r['ok'] for r in results) else 1

if __name__=='__main__':raise SystemExit(main())
