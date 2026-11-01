"""Exercise the real MCP and learning-only clear on an explicitly isolated fixture.

Never accepts the production data directory. Does not analyze or play audio.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sqlite3
import time
from mcp_client import McpClient

ROOT=Path(__file__).resolve().parents[2]

def call(client,tool,**arguments):
    result=client.call(tool,arguments)
    assert not result.get('isError'),(tool,result)
    return result['structuredContent']

def ordinary(data):
    with sqlite3.connect((data/'library.db').as_uri()+'?mode=ro',uri=True) as db:
        tables=[r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")]
        return {table:hashlib.sha256(json.dumps(sorted(db.execute('SELECT * FROM ['+table+']').fetchall(),key=repr),default=repr).encode()).hexdigest() for table in tables}

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe',type=Path,required=True);parser.add_argument('--data',type=Path,required=True);parser.add_argument('--report',type=Path,required=True)
    args=parser.parse_args();data=args.data.resolve()
    assert data.is_relative_to(ROOT/'desktop/artifacts') and data.name.endswith('fixture'),'Isolated artifact fixture only'
    report={}
    with McpClient(args.exe,data) as client:
        initial=call(client,'get_status');assert not initial['playing']
        catalog=client.request('tools/list');assert len(catalog['tools'])==38
        args.report.with_suffix('.catalog.json').write_text(json.dumps(catalog,ensure_ascii=False,indent=2),encoding='utf-8')
        status=call(client,'preference_data',action='status');identity=status['profileId'];assert identity=='personal-label-learning-v3'
        for name in ('ignored first name','ignored second name'):
            assert call(client,'preference_profile',action='create',name=name)['profileId']==identity
        for tool,parameters in [('preference_profile',dict(action='select',profileId='retired-multiple-datasets')),('preference_profile',dict(action='create',name='fixture',purpose='synthetic_validation')),('preference_data',dict(action='create')),('diagnose_analysis',dict(action='status',sensitivity='STRICT'))]:
            assert client.call(tool,parameters).get('isError'),(tool,parameters)
        report['firstUseAndDeprecatedCalls']=True
        for dimension in ('MOOD','VOICE','GENRE','CALMNESS'):
            call(client,'preference_data',action='dimension',dimension=dimension)
            call(client,'preference_training',action='train',dimension=dimension)
            deadline=time.monotonic()+10
            while True:
                training=call(client,'preference_training',action='status',dimension=dimension)
                if training['state']!='checking_material':break
                assert time.monotonic()<deadline;time.sleep(.1)
            assert training['state']=='insufficient_material',training
        report['fourIndependentInsufficientStates']=True
        rows=call(client,'search_tracks',limit=1)['items'];assert rows
        explained=call(client,'explain_analysis',trackId=rows[0]['id'])
        assert explained['sensitivity']=='LOOSE' and explained['rulesVersion']==9 and explained['thresholds']['MOOD']==2.75
        assert client.call('explain_analysis',dict(trackId=rows[0]['id'],sensitivity='NORMAL')).get('isError')
        report['fixedLooseDiagnosticsAndOldParameterRejection']=True
        before=ordinary(data)
        status=call(client,'preference_data',action='clear');assert status['answered']==0 and status['manifestCount']==0 and status['sync']['clearBoundaryCount']>=1
        assert before==ordinary(data),'Learning clear modified an ordinary domain table'
        for d in ('MOOD','VOICE','GENRE','CALMNESS'):
            assert call(client,'preference_batch',action='status',dimension=d)['items']==[]
        report['learningOnlyClearAndImmediateReuse']=True
        # Preparing an unanswered fixture batch is not inference or human truth.
        batch=call(client,'preference_batch',action='prepare',dimension='VOICE');assert 1<=len(batch['items'])<=6
        assert before==ordinary(data)
        report['batchReadyWithoutCreate']=len(batch['items'])
        track=rows[0]["id"]
        detail=call(client,"get_track",trackId=track)
        assert Path(detail["path"]).resolve().is_relative_to(ROOT/"desktop/artifacts")
        call(client,"set_smart_tags",trackIds=[track],voice="VOCAL")
        def captured(expected):
            deadline=time.monotonic()+15
            while True:
                call(client,"preference_data",action="capture")
                status=call(client,"preference_data",action="status")
                head=next(h for h in status["heads"] if h["dimension"]=="VOICE")
                if head[expected]==1:return
                assert time.monotonic()<deadline,status
                time.sleep(.1)
        captured("positiveCount")
        call(client,"set_smart_tags",trackIds=[track],voice="INSTRUMENTAL",genre="CLASSICAL")
        captured("negativeCount")
        assert call(client,"search_tracks",onlyManual=True,limit=1)["total"]>=1
        call(client,"set_smart_tags",trackIds=[track],voice="AUTO")
        deadline=time.monotonic()+15
        while True:
            call(client,"preference_data",action="capture")
            status=call(client,"preference_data",action="status")
            if next(h for h in status["heads"] if h["dimension"]=="VOICE")["trainingLabels"]==0:break
            assert time.monotonic()<deadline
            time.sleep(.1)
        call(client,"set_smart_tags",trackIds=[track],genre="AUTO")
        call(client,"preference_data",action="capture")
        report["manualBusinessToLearningRevisionWithdrawalAndManualFilter"]=True
        columns=call(client,"song_columns",action="status")
        assert columns["summaryColumnRemoved"] and not any(c["tag"]=="Tags" for c in columns["columns"])
        original={c["tag"]:c for c in columns["columns"]}
        for tag in ("Mood","Voice","Genre","Calmness"):
            changed=call(client,"song_columns",action="set",tag=tag,visible=True,width=105)
            assert next(c for c in changed["columns"] if c["tag"]==tag)["visible"]
        assert client.call("song_columns",dict(action="set",tag="Title",visible=False)).get("isError")
        for tag in ("Mood","Voice","Genre","Calmness"):
            c=original[tag];call(client,"song_columns",action="set",tag=tag,visible=c["visible"],width=max(64,c["width"]))
        report["columnControlsAndTitleInvariant"]=True
        final=call(client,'get_status');assert final['playing']==initial['playing'] and final['queueCount']==initial['queueCount']
        report['playbackPreserved']=True
        call(client,'checkpoint_client')
    args.report.write_text(json.dumps(report,indent=2),encoding='utf-8')
    print(json.dumps(report))

if __name__=='__main__':main()
