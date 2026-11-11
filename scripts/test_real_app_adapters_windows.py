"""Explicit installed-app connectivity diagnostic, not a timed comparison trial."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import uuid

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--run',action='store_true');p.add_argument('--app',choices=['chrome','code','both'],default='both');args=p.parse_args()
    if sys.platform!='win32' or not args.run:
        print('SKIP: explicit --run on an interactive Windows desktop required');return 0
    repo=Path(__file__).resolve().parents[1];root=repo/'work'/('app-adapter-preflight-'+uuid.uuid4().hex);root.mkdir()
    print('DIAGNOSTICS',root,flush=True)
    for app,features in [('chrome',['search','sort']),('code',['completion','save'])]:
        if args.app not in ('both',app):continue
        with (root/(app+'.jsonl')).open('w',encoding='utf-8') as out,(root/(app+'.stderr.txt')).open('w',encoding='utf-8') as err:
            child=subprocess.Popen([sys.executable,'-u',str(repo/'scripts/real_apps/driver.py'),'--app',app,'--runtime',str(root/(app+'-runtime'))],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=err,text=True,encoding='utf-8')
            try:
                def reply():
                    line=child.stdout.readline();out.write(line);out.flush()
                    if not line:raise RuntimeError('Driver closed output; inspect '+str(root))
                    value=json.loads(line);print(app,line.strip(),flush=True);return value
                assert reply()['kind']=='ready'
                for i,feature in enumerate(features*3,1):
                    child.stdin.write(json.dumps(dict(id=i,verb='request',feature=feature))+'\n');child.stdin.flush()
                    result=reply();assert result['id']==i and result['correct'] and result['foreground_before']['owned'] and result['foreground_after']['owned']
                child.stdin.write(json.dumps(dict(id=10,verb='stop'))+'\n');child.stdin.flush()
                assert reply()['cleanup']['completed'];child.wait(timeout=20);assert child.returncode==0
            finally:
                if child.poll() is None:child.kill();child.wait()
    return 0

if __name__=='__main__':raise SystemExit(main())
