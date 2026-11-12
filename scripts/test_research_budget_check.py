"""Synthetic duration/gate tests, never live performance evidence."""
import unittest
import copy
from research_budget_check import exposure, pair_blocks, budget_errors,MASK
from research_budget_batch import schedule


class BudgetGateTests(unittest.TestCase):
    def sample(self):
        records=[]
        def add(kind,data):records.append(dict(kind=kind,data=data))
        baseline=dict(phase='Normal',clock_seconds=0,p95_ms={'completion':50,'save':20},counts={'completion':20,'save':20})
        original={'values':{'CpuPriority':32}}
        outcome=dict(protocol='real-app-budget-v1',condition='B',phases=[dict(phase='Normal',metrics=[dict(feature=k,count=20,p95_ms=v) for k,v in baseline['p95_ms'].items()])])
        add('budget-baseline-frozen',baseline);add('budget-start',dict(phase='Contention',clock_seconds=0,epochs=20,quota=9,epoch_seconds=.5));used=0;prior=False
        for i in range(20):
            inputs={}
            for n,(feature,value) in enumerate(baseline['p95_ms'].items(),1):
                id=i*2+n;r=dict(feature=feature,latency_ms=value,id=id,phase='Contention')
                add('app-observation',r);add('budget-observation',dict(**r,clock_seconds=i*.5))
                inputs[feature]=dict(baseline_p95_ms=value,normalized_ema=1,seen_seconds=i*.5,source_request_id=id)
            selected=i in MASK;used+=int(selected)
            reason='quota-exhausted' if used==9 and not selected else 'fixed-periodic'
            add('budget-epoch',dict(phase='Contention',due_seconds=i*.5,actual_seconds=i*.5,decision=dict(epoch=i,selected=selected,used=used,quota=9,reason=reason,risk=1,fresh=True,mode='fixed',rotation=0,observations=inputs)))
            if selected!=prior:add('resource-write-readback',dict(phase='Contention',readback_clock_seconds=i*.5,observed={'CpuPriority':16384} if selected else original['values']))
            prior=selected
        add('budget-stop-deadline',dict(phase='Contention',due_seconds=10,actual_seconds=10))
        add('budget-end',dict(phase='Contention',clock_seconds=10,used=9,quota_complete=True,max_deadline_lag_seconds=0))
        result=dict(protocol='real-app-budget-v1',requested_on_seconds=4.5,actual_readback_on_seconds=4.5,used=9,quota_complete=True,start_seconds=0,end_seconds=10,max_deadline_lag_seconds=0,matched_session=True)
        return records,outcome,baseline,result,original

    def test_semantic_replay_accepts_synthetic_exact_schedule(self):
        self.assertEqual(budget_errors(*self.sample()),[])

    def v2_sample(self):
        args=self.sample();records,outcome,baseline,result,original=args
        outcome['protocol']=result['protocol']='real-app-budget-v2-readback-clock'
        writes=[r['data'] for r in records if r['kind']=='resource-write-readback']
        for i in range(20):
            on,_=exposure(0,i*.5,writes,original['values'])
            records.append(dict(kind='budget-after-tick',data=dict(phase='Contention',epoch=i,tick_end_seconds=i*.5,on_seconds=on,next_due_seconds=(i+1)*.5)))
        return args

    def test_readback_clock_semantic_replay(self):self.assertEqual(budget_errors(*self.v2_sample()),[])

    def test_readback_clock_deadline_tampering_rejected(self):
        args=self.v2_sample();next(r['data'] for r in args[0] if r['kind']=='budget-after-tick')['next_due_seconds']+=.2
        self.assertIn('budget_readback_clock_replay_mismatch',budget_errors(*args))

    def test_replay_rejects_response_policy_and_dose_tampering(self):
        for kind in ('raw','decision','dose'):
            args=copy.deepcopy(self.sample());records,outcome,baseline,result,original=args
            if kind=='raw':next(r['data'] for r in records if r['kind']=='budget-observation')['latency_ms']=999
            elif kind=='decision':next(r['data'] for r in records if r['kind']=='budget-epoch')['decision']['selected']=False
            else:result['actual_readback_on_seconds']=4.4
            with self.subTest(kind=kind):self.assertTrue(budget_errors(*args))

    def test_missing_stop_marker_is_rejected(self):
        args=list(self.sample());args[0]=[r for r in args[0] if r['kind']!='budget-stop-deadline']
        self.assertIn('budget_lifecycle_missing',budget_errors(*args))

    def test_schedule_is_balanced_and_deterministic(self):
        plan=schedule(20261113);self.assertEqual(plan,schedule(20261113));self.assertEqual(len(plan),24)
        for app in ('chrome','code'):
            self.assertEqual(sum(r['condition']=='B' and r['position']==1 for r in plan if r['app']==app),3)
        self.assertEqual(len({r['session'] for r in plan}),24)
    def test_readback_state_not_requested_quota(self):
        original={'CpuPriority':32}
        writes=[{'readback_clock_seconds':1.1,'observed':{'CpuPriority':16384}},
                {'readback_clock_seconds':3.1,'observed':original}]
        self.assertAlmostEqual(exposure(1,5,writes,original)[0],2)

    def test_unrestored_tail_is_counted(self):
        self.assertEqual(exposure(1,5,[{'readback_clock_seconds':2,'observed':{}}],{'x':1}),(3,True))

    def test_pair_rejects_dose_difference_and_missing(self):
        rows=[dict(app='code',block=1,condition=c,eligible=True,dose=d) for c,d in [('B',4.4),('C',4.6)]]
        rows.append(dict(app='code',block=2,condition='B',eligible=True,dose=4.5))
        self.assertEqual(pair_blocks(rows),set())

    def test_pair_accepts_close_actual_dose(self):
        rows=[dict(app='code',block=1,condition=c,eligible=True,dose=d) for c,d in [('B',4.46),('C',4.53)]]
        self.assertEqual(pair_blocks(rows),{('code',1)})

if __name__=='__main__':unittest.main()
