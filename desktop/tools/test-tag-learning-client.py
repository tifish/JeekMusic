"""Real MCP/worker orchestration in an isolated, generated fixture. Never user truth.

The positive deployment scenario SIMULATES trusted independent human input using
generated vectors/labels in this fixture only. It proves software contracts, not
classification validity. The separate real CLAP extraction has no accuracy claim.
"""
import argparse
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import shutil
import sqlite3
import struct
import time
import uuid
import wave
from urllib.parse import urlsplit
from mcp_client import McpClient

ROOT=Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('lan_fixture',Path(__file__).with_name('test-lan-sync.py'))
lan=importlib.util.module_from_spec(spec);spec.loader.exec_module(lan)
H=lambda s:hashlib.sha256(s.encode()).hexdigest()

def call(c,name,**args):
    r=c.call(name,args); assert not r.get('isError'),(name,r); return r['structuredContent']

def wait(c,workflow=False,timeout=300):
    deadline=time.monotonic()+timeout
    while time.monotonic()<deadline:
        s=call(c,'preference_training',action='status',dimension='VOICE')
        if workflow:
            if not s['learning']['running']: return s
        elif s['runningDimension'] is None: return s
        time.sleep(.15)
    raise TimeoutError('Bounded fixture failed to terminate')

def main():
    p=argparse.ArgumentParser(description=__doc__); p.add_argument('--exe',type=Path,required=True);p.add_argument('--data',type=Path,required=True);p.add_argument('--components',type=Path,required=True);p.add_argument('--real-extract',action='store_true');a=p.parse_args()
    data=a.data.resolve(); assert data.is_relative_to(ROOT/'desktop/artifacts') and data.name.endswith('fixture') and not data.exists(),'Fresh isolated fixture required'
    data.mkdir(parents=True); music=data/'music';music.mkdir()
    (data/'settings.json').write_text(json.dumps(dict(language='en',analysisMode=2,closeAction='Exit',autoCheckUpdate=False)))
    for i in range(44):
        pcm=b''.join(struct.pack('<h',int(8000*math.sin(2*math.pi*(180+i*7)*n/8000))) for n in range(8000))
        with wave.open(str(music/f'Fixture {i:02}.wav'),'wb') as out:
            out.setparams((1,2,8000,0,'NONE','not compressed'))
            for _ in range(180):out.writeframesraw(pcm)
    report=dict(isolatedGeneratedFixture=True,userAccuracyEvidence=False,simulatedIndependentTruth=True,productionUserDataTouched=False)
    pid=None
    try:
        with McpClient(a.exe,data,timeout=60) as c:
            pid=call(c,'get_diagnostics')['processId'];catalog=c.request('tools/list');(data/'catalog.json').write_text(json.dumps(catalog))
            assert len(catalog['tools'])==38
            initial=call(c,'get_status'); assert not initial['playing'] and initial['queueCount']==0
            call(c,'add_folder',path=str(music))
            deadline=time.monotonic()+45
            while True:
                tracks=call(c,'search_tracks',limit=200)['items']
                if len(tracks)==44 and not call(c,'get_status')['scanning']:break
                assert time.monotonic()<deadline;time.sleep(.2)
            call(c,'preference_training',action='learn');assert wait(c,True)['learning']['state']=='insufficient_material'
            call(c,'lan_sync_control',action='start',port=0)
            invitation=urlsplit(call(c,'lan_sync_control',action='invite',host='127.0.0.1')['invitation']);pin,secret=invitation.path.strip('/').split('/')
            connection=dict(port=invitation.port,pin=pin,deviceId=uuid.uuid4().hex)
            paired=lan.wire(connection,'pair',invitation=secret,name='Generated isolated learning fixture');connection['token']=paired['token']
            generation=call(c,'preference_data',action='status')['sync']['generation'];counter=0
            def event(entity,kind,payload):
                nonlocal counter
                counter+=1
                return dict(protocol=3,generation=generation,profileId='personal-label-learning-v3',eventId=uuid.uuid4().hex,entityId=entity,kind=kind,deviceId='isolated_simulated_truth',revision=1,parents=[],at=int(time.time()*1000)+counter,payload=payload)
            def send(events):
                reply=lan.wire(connection,'preference_delta',protocol=3,sender='isolated_simulated_truth',after=0,page=dict(protocol=3,generation=generation,resetBoundary=[],events=events,nextCursor=counter,hasMore=False))
                assert 'error' not in reply,reply
            records=[];bindings={}
            for i,t in enumerate(tracks):
                detail=call(c,'get_track',trackId=t['id']);path=Path(detail['path']);assert path.resolve().is_relative_to(music)
                content=hashlib.sha256(path.read_bytes()).hexdigest();rid=H('sha256:'+content+':whole');bindings[rid]=(detail,content)
                records.append(event(rid,'recording',dict(recordingId=rid,contentSignature=content,title=f'Fixture {i}',artists=[f'FIXTURE ARTIST {i}'],workGroup=f'FIXTURE WORK {i}',albumGroup=f'FIXTURE RELEASE {i}',identityReviewed=True,exposed=False,supported=True,durationMs=180000,fixture=True)))
            send(records);call(c,'preference_data',action='dimension',dimension='VOICE');assert call(c,'preference_inventory',action='freeze')['state']=='frozen'
            with sqlite3.connect(data/'preferences.db') as db:
                study=next(json.loads(r[0])['payload'] for r in db.execute("SELECT json FROM events WHERE kind='manifest'") if json.loads(r[0])['entityId']=='study-v1')
            answers=[];vectors={}
            for role in ('development','sealed_test','training'):
                ids=study[role];assert len(ids)==dict(training=12,development=8,sealed_test=24)[role]
                for chunk in [ids[j:j+6] for j in range(0,len(ids),6)]:
                    mid=uuid.uuid4().hex
                    manifest=dict(dimension='VOICE',role=role,items=chunk,exposure='blind_ui_no_predictions',state='frozen_'+role,fixture=True);manifest['hash']=H(json.dumps(manifest));answers.append(event(mid,'manifest',manifest))
                    for rid in chunk:
                        index=ids.index(rid);positive=index%2==0;x=[(1 if positive else -1),.04*(index+1)]+[0.0]*510;norm=math.sqrt(sum(v*v for v in x));x=[v/norm for v in x]
                        vectors[rid]=dict(vector=x,perturbations={k:x for k in ('base','minus6','plus6','minus2','plus2')},contentSignature=bindings[rid][1])
                        answers.append(event(H('fixture-answer:'+rid),'answer',dict(dimension='VOICE',contextId='voice_personal_v1',recordingId=rid,contentSignature=bindings[rid][1],answer='VOCAL' if positive else 'INSTRUMENTAL',basis='whole_recording_familiar',source='explicit_human_blind_batch',exposure='blind_ui_no_predictions',role=role,manifestId=mid,manifestHash=manifest['hash'],fixture=True,reason='ISOLATED GENERATED ENGINEERING FIXTURE; NOT HUMAN VALIDITY EVIDENCE')))
            for chunk in [answers[j:j+100] for j in range(0,len(answers),100)]:send(chunk)
            worker=a.exe.parent/'Preferences/Runtime/preference_worker.py';worker_hash=hashlib.sha256(worker.read_bytes()).hexdigest();extractor='clap-htsat-unfused@8fa0f1c6d0433df6e97c127f64b2a1d6c0dcda8a/center10-rms01-v1'
            with sqlite3.connect(data/'preferences.db') as db:
                for rid,(t,content) in bindings.items():
                    db.execute('INSERT OR REPLACE INTO local VALUES(?,?)',('binding:'+rid,t['trackKey']))
                    db.execute('INSERT OR REPLACE INTO local VALUES(?,?)',('signature:'+rid,f"{t['mtimeUtcTicks']}:{Path(t['path']).stat().st_size}"))
                for rid,v in vectors.items():
                    v.update(workerHash=worker_hash,extractor=extractor);db.execute('INSERT OR REPLACE INTO local VALUES(?,?)',('vector:'+rid,json.dumps(v)))
            remote=lan.wire(connection,'preference_training',protocol=3,action='learn');assert 'error' not in remote,remote
            assert wait(c,True)['learning']['state']=='download_required'
            assert not (data/'preference-components/model').exists();report['downloadConsentBeforeNetwork']=True
            component_root=data/'preference-components';component_root.mkdir(exist_ok=True)
            for name in ('model','venv'):shutil.copytree(a.components/name,component_root/name,copy_function=os.link)
            shutil.copy2(a.components/'ready.json',component_root/'ready.json')
            call(c,'preference_training',action='prepare');time.sleep(.15);remote=lan.wire(connection,'preference_training',protocol=3,action='cancel');assert 'error' not in remote;assert wait(c)['state']=='cancelled';report['realPreparationCancellation']=True
            if a.real_extract:
                rid=study['training'][0]
                with sqlite3.connect(data/'preferences.db') as db:db.execute('DELETE FROM local WHERE key=?',('vector:'+rid,))
                call(c,'preference_training',action='extract',dimension='VOICE');assert wait(c)['state']=='extraction_complete'
                with sqlite3.connect(data/'preferences.db') as db:
                    real=json.loads(db.execute('SELECT value FROM local WHERE key=?',('vector:'+rid,)).fetchone()[0]);assert len(real['vector'])==512 and set(real['perturbations'])=={'base','minus6','plus6','minus2','plus2'}
                    db.execute('UPDATE local SET value=? WHERE key=?',(json.dumps(vectors[rid]),'vector:'+rid))
                report['realClapBoundedExtraction']=True
            python=component_root/'venv/Scripts/python.exe';saved=python.with_suffix('.saved');python.rename(saved)
            try:
                call(c,'preference_training',action='resume');failed=wait(c,True);assert failed['state']=='failed',failed
                assert not call(c,'preference_training',action='diagnostics')['heads'][1]['enabled']
            finally:saved.rename(python)
            report['realWorkerFailureAndRetainedTruth']=True
            call(c,'preference_training',action='resume');state=wait(c,True)
            assert state['learning']['state']=='updated',state
            active=call(c,'preference_training',action='diagnostics');assert next(h for h in active['heads'] if h['dimension']=='VOICE')['enabled']
            with sqlite3.connect(data/'preferences.db') as db:
                assert db.execute("SELECT COUNT(*) FROM local WHERE key LIKE 'personal-result:%'").fetchone()[0]==44
                receipts=db.execute("SELECT key FROM local WHERE key LIKE 'passed:VOICE:%'").fetchall();assert len(receipts)==2
            for t in tracks:
                e=call(c,'explain_analysis',trackId=t['id']);assert e['sources']['VOICE']=='PERSONAL' and e['personal']['VOICE']['version']
            assert all(call(c,'get_track',trackId=t['id'])['tagSources'].get('voice')=='PERSONAL' for t in tracks)
            page=lan.wire(connection,'preference_results',protocol=3,offset=0);assert len(page['rows'])==44 and 'vector' not in json.dumps(page) and 'model' not in json.dumps(page)
            final=call(c,'get_status');assert final['queueCount']==0 and not final['playing']
            with sqlite3.connect(data/'library.db') as db:assert db.execute('SELECT COUNT(*) FROM tag_override').fetchone()[0]==0 and db.execute('SELECT COUNT(*) FROM tag_review').fetchone()[0]==0
            report.update(automaticTrainCheckActivationAndFullLibrary=True,traceableResults=44,ordinaryManualDataUntouched=True,queuePreserved=True)
            # Explicit production-style invalidation, confined to the fixture.
            rid=study['training'][0];old=next(e for e in answers if e['kind']=='answer' and e['payload']['recordingId']==rid)
            withdrawn={**old,'eventId':uuid.uuid4().hex,'revision':2,'parents':[old['eventId']],'at':int(time.time()*1000)+10000,'payload':{**old['payload'],'answer':'WITHDRAWN'}}
            send([withdrawn]);assert not next(h for h in call(c,'preference_training',action='diagnostics')['heads'] if h['dimension']=='VOICE')['enabled']
            deadline=time.monotonic()+15
            while any(call(c,'get_track',trackId=t['id'])['tagSources'].get('voice')=='PERSONAL' for t in tracks):
                assert time.monotonic()<deadline; time.sleep(.1)
            report['withdrawalInvalidatesAndFallsBack']=True
    finally:
        if pid is not None:
            import subprocess
            subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
    (data/'report.json').write_text(json.dumps(report,indent=2));print(json.dumps(report),flush=True)

if __name__=='__main__':main()
