"""SYNTHETIC test inputs only; never real performance observations."""
import collections
import json
from pathlib import Path
import tempfile
import unittest

import research_app_batch as batch


class BatchTests(unittest.TestCase):
    def test_each_app_uses_all_orders_and_balances_each_position(self):
        rows=batch.schedule(6,20261112)
        self.assertEqual(len(rows),36)
        for app in ('chrome','code'):
            orders=[]
            for block in range(1,7):
                group=[r for r in rows if r['app']==app and r['block']==block]
                self.assertEqual(len(group),3)
                orders.append(''.join(r['condition'] for r in group))
            self.assertEqual(set(orders),{'ABC','ACB','BAC','BCA','CAB','CBA'})
            for i in range(3):self.assertEqual(collections.Counter(o[i] for o in orders),{'A':2,'B':2,'C':2})
        self.assertEqual(len({r['session'] for r in rows}),36)

    def test_invalid_schedule_is_rejected_before_any_run(self):
        for blocks in (0,1,7,True,36):
            with self.subTest(blocks=blocks),self.assertRaises(ValueError):batch.schedule(blocks,1)

    def test_safety_failure_stops_later_slots_and_never_retries(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);plan={'schedule':[{'session':x,'app':'chrome','condition':'A','block':i} for i,x in enumerate(['one','two','three'],1)]}
            def execute(slot):
                d=root/slot['session'];d.mkdir()
                (d/'outcome.json').write_text(json.dumps(dict(status='failed' if slot['session']=='two' else 'completed',cleanup_completed=slot['session']!='two',restored=True,workload_restored=True,protected_misinterventions=0,readback_failures=0)))
                return 0
            result=batch.run_schedule(root,plan,execute,lambda d:dict(ok=True,status='completed'))
            self.assertEqual([r['status'] for r in result],['completed','failed','not_run_safety_stop'])
            self.assertFalse((root/'three').exists())
            before=(root/'one.attempt.json').read_bytes()
            with self.assertRaises(FileExistsError):batch.run_schedule(root,plan,execute,lambda d:dict(ok=True))
            self.assertEqual((root/'one.attempt.json').read_bytes(),before)

    def test_complete_block_pairing_does_not_pool_requests_or_missing_blocks(self):
        rows=[]
        for block,b,c,n in [(1,10,12,1000),(2,20,18,1)]:
            for condition,value in [('B',b),('C',c)]:rows.append(dict(app='code',block=block,condition=condition,phase='Contention',feature='completion',requests=n,p95_ms=value,background_units_per_second=100 if condition=='B' else 150))
        rows.append(dict(app='code',block=3,condition='B',phase='Contention',feature='completion',requests=500,p95_ms=999,background_units_per_second=1))
        result=batch.compare(rows,iterations=100,seed=1)
        row=next(r for r in result if r['baseline']=='B' and r['candidate']=='C' and r['metric']=='p95_ms')
        self.assertEqual(row['n_blocks'],2)
        self.assertEqual(row['baseline_mean'],15)
        self.assertEqual(row['candidate_mean'],15)
        self.assertEqual(row['difference'],0)
        self.assertEqual(row['missing_pair_blocks'],[3])

    def test_missing_outcomes_and_broken_logs_are_ledgered(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);plan={'schedule':[dict(session='missing',app='chrome',condition='A',block=1)]}
            result=batch.collect(root,plan)
            self.assertEqual(result['ledger'][0]['status'],'missing')
            self.assertEqual(result['metrics'],[])

    def test_nonoverlapping_blocks_have_no_invented_pair(self):
        rows=[dict(app='chrome',phase='Contention',feature='search',condition=c,block=b,p95_ms=10,background_units_per_second=100) for c,b in [('B',1),('C',2)]]
        row=next(r for r in batch.compare(rows,iterations=100) if r['baseline']=='B' and r['candidate']=='C' and r['metric']=='p95_ms')
        self.assertEqual(row['n_blocks'],0)
        self.assertIsNone(row['difference'])
        self.assertEqual(row['missing_pair_blocks'],[1,2])


if __name__=='__main__':unittest.main()
