"""SYNTHETIC contract fixtures only; never experimental observations."""
import copy
import unittest
import research_logcheck as gate


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


if __name__ == '__main__': unittest.main()
