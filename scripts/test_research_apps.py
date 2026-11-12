"""SYNTHETIC contract fixtures only; never experimental observations."""
import copy
import unittest
import json
from pathlib import Path
import tempfile
import research_logcheck as gate
import test_research_logcheck as integrity_fixtures


class AppContract(unittest.TestCase):
    def fixture(self,condition='C'):
        records=[]
        def add(kind,data):records.append(dict(kind=kind,data=data))
        add('app-ready',dict(features=['search','sort']))
        summaries=[]
        for i,phase in enumerate(['Normal','Contention','Recovery']):
            add('phase-start',dict(phase=phase,units=i*100,seconds=i*10))
            metrics=[]
            for feature in ['search','sort']:
                values=list(range(12))
                for value in values:
                    fg=dict(owned=True,pid=123,creation_filetime=456)
                    add('app-observation',dict(phase=phase,feature=feature,latency_ms=value,roundtrip_ms=value+1,correct=True,reply=dict(foreground_before=fg,foreground_after=fg)))
                metrics.append(dict(feature=feature,count=12,latencies_ms=values,p50_ms=5.5,p95_ms=10.45,roundtrip_ms=[v+1 for v in values]))
            add('phase-end',dict(phase=phase,units=i*100+40,seconds=i*10+8))
            summaries.append(dict(phase=phase,metrics=metrics,background_units=40,background_seconds=8,background_units_per_second=5))
        values=dict(CpuPriority=0x4000,MemoryPriority=3,PowerControl=1,PowerState=1,CpuSets=[])
        if condition!='A':add('resource-write-readback',dict(phase='Contention',requested=values,observed=copy.deepcopy(values),api_result=True,matched=True))
        add('app-cleanup',dict(cleanup=dict(completed=True,remaining_pids=[])))
        outcome=dict(application='chrome',condition=condition,phases=summaries,observations=72,protected_misinterventions=0,readback_failures=0)
        return records,outcome

    def test_unknown_profile_fails_closed(self):
        self.assertIn('app_phases_missing_or_out_of_order', gate.app_profile_errors([], {}))

    def test_valid_synthetic_contract(self):
        for condition in 'ABC':
            rows,outcome=self.fixture(condition)
            self.assertEqual([],gate.app_profile_errors(rows,outcome))

    def test_matching_bundle_includes_memory(self):
        rows,outcome=self.fixture('B')
        row=next(r['data'] for r in rows if r['kind']=='resource-write-readback')
        row['requested']['MemoryPriority']=5;row['observed']['MemoryPriority']=5
        self.assertIn('app_v1_matched_bundle_not_demonstrated',gate.app_profile_errors(rows,outcome))

    def test_summary_tampering_with_rehashed_index_still_fails(self):
        rows,outcome=self.fixture();outcome['phases'][0]['metrics'][0]['p95_ms']=0
        self.assertIn('app_latency_summary_mismatch:Normal',gate.app_profile_errors(rows,outcome))

    def test_useful_progress_uses_boundary_differences(self):
        rows,outcome=self.fixture();outcome['phases'][1]['background_units_per_second']=500
        self.assertIn('app_throughput_summary_mismatch:Contention',gate.app_profile_errors(rows,outcome))

    def test_forged_readback_matched_flag_cannot_pass(self):
        rows,outcome=self.fixture()
        next(r['data'] for r in rows if r['kind']=='resource-write-readback')['observed']['CpuPriority']=0x20
        self.assertIn('app_readback_mismatch',gate.app_profile_errors(rows,outcome))

    def test_observation_must_be_inside_its_phase(self):
        rows,outcome=self.fixture();rows[2]['data']['phase']='Recovery'
        self.assertIn('app_observation_outside_phase',gate.app_profile_errors(rows,outcome))

    def test_lost_os_foreground_invalidates_trial(self):
        rows,outcome=self.fixture();rows[2]['data']['reply']['foreground_after']['owned']=False
        self.assertIn('app_foreground_unconfirmed',gate.app_profile_errors(rows,outcome))

    def test_no_engine_condition_has_no_resource_writes(self):
        rows,outcome=self.fixture();outcome['condition']='A'
        self.assertIn('app_A_was_modified',gate.app_profile_errors(rows,outcome))

    def test_missing_feature_is_not_success(self):
        rows,outcome=self.fixture();outcome['phases'][0]['metrics'].pop()
        self.assertIn('app_metric_features_mismatch:Normal',gate.app_profile_errors(rows,outcome))

    def test_cleanup_and_scope_must_be_confirmed(self):
        rows,outcome=self.fixture();outcome['protected_misinterventions']=1;rows.pop()
        errors=gate.app_profile_errors(rows,outcome)
        self.assertIn('app_safety_or_readback_failure',errors);self.assertIn('app_cleanup_unconfirmed',errors)


class AppVersionGate(unittest.TestCase):
    def test_workload_qos_setup_is_separate_and_must_restore_initial_values(self):
        value=dict(MemoryPriority=5,CpuPriority=32,PowerControl=0,PowerState=0,CpuSets=[])
        original=dict(pid=12,creation_filetime=34,values=value)
        effective=dict(pid=12,creation_filetime=34,values={**value,'PowerControl':1})
        final=dict(pid=12,creation_filetime=34,values=value.copy(),restored=True)
        check=getattr(gate,'workload_qos_errors',lambda *args:['missing_workload_qos_validation'])
        self.assertEqual([],check(original,effective,final))
        final['values']={**value,'PowerControl':1}
        self.assertIn('workload_initial_settings_not_restored',check(original,effective,final))

    def test_v2_cannot_be_completed_without_frozen_healthy_baseline(self):
        helper=integrity_fixtures.EvidenceChecks('runTest');helper.setUp();self.addCleanup(helper.doCleanups);helper.fixture('completed')
        manifest=__import__('json').loads((helper.root/'run-manifest.json').read_text())
        manifest['evidence_profile']='real-app-abc-v2';helper.save('run-manifest.json',manifest)
        (helper.root/'pre-intervention.json').unlink();helper.index()
        result=gate.check_run(helper.root)
        self.assertIn('required_not_indexed:pre-intervention.json',result['errors'])

    def test_unstarted_slots_never_become_zero_latency_successes(self):
        import research_app_analyze as analysis
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[1]/'work') as folder:
            root=Path(folder);plan=dict(protocol='real-app-abc-v2',schedule=[dict(app=a,condition=c,session=a+'-'+c) for a in ['chrome','code'] for c in 'ABC'])
            (root/'pilot-plan.json').write_text(json.dumps(plan))
            result=analysis.summarize(root)
            self.assertFalse(result['smoke_passed']);self.assertEqual([],result['metrics'])
            self.assertTrue(all(s['status']=='unstarted' for s in result['sessions']))
            self.assertFalse(any(result['matched_B_C_readbacks'].values()))


if __name__ == '__main__': unittest.main()
