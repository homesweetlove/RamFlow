"""SYNTHETIC evidence-format fixtures; no policy performance data."""
import csv
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import research_logcheck as checker

class EvidenceChecks(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[1]/'work')
        self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)
    def save(self,name,value):
        path=self.root/name;path.parent.mkdir(parents=True,exist_ok=True)
        path.write_text(json.dumps(value),encoding='utf-8')
    def fixture(self,status='failed'):
        self.save('run-manifest.json',dict(run_id='SYNTHETIC',args=['trial'],evidence_schema_version=1))
        self.save('outcome.json',dict(status=status,restored=True))
        rows=[dict(run_id='SYNTHETIC',sequence=1,monotonic_ticks=0,time_utc='2026-11-11T00:00:00Z',kind='run-start',data={}),
              dict(run_id='SYNTHETIC',sequence=2,monotonic_ticks=1,time_utc='2026-11-11T00:00:01Z',kind='terminal',data=dict(status=status))]
        (self.root/'trial-events.jsonl').write_text(''.join(json.dumps(r)+'\n' for r in rows),encoding='utf-8')
        self.save('completion.json',dict(evidence_schema_version=1,run_id='SYNTHETIC',status=status,cleanup_completed=True,logging_fault=False,records=2))
        if status=='completed':
            for name in checker.COMPLETED_REQUIRED:
                if not (self.root/name).exists():self.save(name,{})
        self.index()
    def index(self):
        entries=[dict(path=p.relative_to(self.root).as_posix(),bytes=p.stat().st_size,sha256=hashlib.sha256(p.read_bytes()).hexdigest(),retention='required')
                 for p in sorted(self.root.rglob('*')) if p.is_file() and p.name!='artifact-index.json']
        self.save('artifact-index.json',dict(evidence_schema_version=1,run_id='SYNTHETIC',files=entries))
    def test_sealed_failure_is_preserved_but_never_performance_ready(self):
        self.fixture();r=checker.check_run(self.root)
        self.assertTrue(r['ok'],r);self.assertEqual(r['status'],'failed');self.assertFalse(r['performance_ready'])
    def test_complete_fixture_is_ready(self):
        self.fixture('completed');self.assertTrue(checker.check_run(self.root)['performance_ready'])
    def test_missing_index_is_unfinalized(self):
        self.fixture();(self.root/'artifact-index.json').unlink()
        self.assertFalse(checker.check_run(self.root)['ok'])
    def test_tampering_and_missing_required_file_fail(self):
        self.fixture('completed');self.save('outcome.json',dict(status='completed',restored=False))
        self.assertFalse(checker.check_run(self.root)['ok'])
        (self.root/'resource-original.json').unlink();self.assertFalse(checker.check_run(self.root)['ok'])
    def test_partial_jsonl_tail_is_detected_even_with_matching_hash(self):
        self.fixture();p=self.root/'trial-events.jsonl';p.write_bytes(p.read_bytes()+b'{"cut":')
        self.index();self.assertFalse(checker.check_run(self.root)['ok'])
    def test_sequence_gap_is_detected(self):
        self.fixture();p=self.root/'trial-events.jsonl';p.write_text(p.read_text().replace('"sequence": 2','"sequence": 4'),encoding='utf-8')
        self.index();self.assertFalse(checker.check_run(self.root)['ok'])
    def test_index_traversal_is_rejected(self):
        self.fixture();r=json.loads((self.root/'artifact-index.json').read_text());r['files'][0]['path']='../outside'
        self.save('artifact-index.json',r);self.assertFalse(checker.check_run(self.root)['ok'])
    def test_audit_csv_json_mismatch_is_rejected(self):
        self.fixture();p=self.root/'audit';p.mkdir();(p/'events.jsonl').write_text('{"Sequence":1}\n',encoding='utf-8')
        (p/'events.csv').write_text('sequence\n2\n',encoding='utf-8');self.index()
        self.assertFalse(checker.check_run(self.root)['ok'])
    def test_audit_same_sequence_but_wrong_target_is_rejected(self):
        self.fixture();p=self.root/'audit';p.mkdir()
        event=dict(Sequence=1,Kind='apply',Policy='SYNTHETIC',Version='test',SettingsSha256='hash',Pid=123,Identity=456,Result='applied',Time='2026-11-11T00:00:00Z')
        (p/'events.jsonl').write_text(json.dumps(event)+'\n',encoding='utf-8')
        fields=['sequence','time_utc','kind','policy','version','settings_sha256','pid','creation_filetime','result']
        with (p/'events.csv').open('w',newline='',encoding='utf-8') as stream:
            writer=csv.writer(stream);writer.writerow(fields);writer.writerow([1,event['Time'],'apply','SYNTHETIC','test','hash',123,456,'applied'])
        self.index();self.assertTrue(checker.check_run(self.root)['ok'])
        path=p/'events.csv';path.write_text(path.read_text().replace(',123,',',999,'),encoding='utf-8')
        self.index();self.assertFalse(checker.check_run(self.root)['ok'])
    def test_missing_cleanup_and_completed_outcome_mismatch_fail(self):
        self.fixture('completed');self.save('completion.json',dict(run_id='SYNTHETIC',status='failed',cleanup_completed=False,logging_fault=False,records=2));self.index()
        self.assertFalse(checker.check_run(self.root)['ok'])

if __name__=='__main__':unittest.main()
