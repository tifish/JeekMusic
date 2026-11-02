"""Pairing is the authorization boundary. Real TLS/MCP, synthetic files and empty learning data only."""
import argparse, base64, hashlib, importlib.util, json, subprocess, tempfile, time, uuid
from pathlib import Path
from urllib.parse import urlsplit
from mcp_client import McpClient

spec = importlib.util.spec_from_file_location('security', Path(__file__).with_name('test-lan-security.py'))
security = importlib.util.module_from_spec(spec); spec.loader.exec_module(security)
Wire, call, wait = security.Wire, security.call, security.wait
ALL = dict.fromkeys(['readMusic','writeSync','writeFiles','deleteFiles','manageLearning','downloadLearning'], True)


def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--exe',type=Path,required=True); a=parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='jeek-paired-') as temp:
        root=Path(temp); data=root/'data'; data.mkdir(); music=root/'Music'; music.mkdir()
        (data/'settings.json').write_text(json.dumps(dict(autoCheckUpdate=False,analysisMode='Manual',closeAction='Exit')))
        old=dict(deviceId=uuid.uuid4().hex,token=uuid.uuid4().hex+uuid.uuid4().hex)
        hashed=hashlib.sha256(old['token'].encode()).hexdigest()
        config=dict(enabled=False,bindAddress='127.0.0.1',port=39843,roots={},devices={old['deviceId']:dict(name='Legacy false permissions',tokenHash=hashed,generation='unchanged',expiresAt=1,permissions=dict.fromkeys(ALL,False))})
        path=data/'lan-sync.json'; path.write_text(json.dumps(config))
        peers=[]
        def stop(pid):
            subprocess.run(['taskkill','/pid',str(pid),'/f'],capture_output=True)
            subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-Command',f'Get-Process -Id {int(pid)} -ErrorAction SilentlyContinue | Wait-Process -Timeout 10'],capture_output=True)
        with McpClient(a.exe,data=data,timeout=90) as c:
            pid=call(c,'get_diagnostics')['processId']
            try:
                status=call(c,'lan_sync_status'); assert not status['enabled'] and status['authorizationPolicy']=='paired_all'
                assert status['devices'][0]['permissions']==ALL
                persisted=json.loads(path.read_text()); assert persisted['devices'][old['deviceId']]['tokenHash']==hashed
                assert persisted['devices'][old['deviceId']]['generation']=='unchanged' and persisted['port']==39843 and persisted['bindAddress']=='127.0.0.1'
                call(c,'add_folder',path=str(music)); call(c,'lan_sync_control',action='start',port=0)
                status=wait(lambda:(lambda s:s if s['roots'] else None)(call(c,'lan_sync_status'))); port=status['port']; root_id=status['roots'][0]['rootId']
                def pair(device=None):
                    u=urlsplit(call(c,'lan_sync_control',action='invite',host='127.0.0.1')['invitation']); pin,secret=u.path.strip('/').split('/')
                    peer=dict(port=port,pin=pin,deviceId=device or uuid.uuid4().hex)
                    w=Wire(peer,auth=False)
                    try: w.send(dict(op='pair',deviceId=peer['deviceId'],name='New paired fixture',invitation=secret)); peer['token']=w.receive()['token']
                    finally: w.close()
                    return peer
                new=pair(); old.update(port=port,pin=new['pin']); peers=[old,new]
                for index,peer in enumerate(peers):
                    w=Wire(peer)
                    try:
                        assert w.request('capabilities')['permissions']==ALL
                        assert c.call('lan_sync_control',dict(action='authorize',deviceId=peer['deviceId'],permissions=ALL))['isError']
                        assert w.request('roots')['roots'] # Rejected old command did not invalidate a valid session.
                        state=dict(format=2,device=dict(id=peer['deviceId'],name='Fixture'),written_at=0,playlists=[],deleted_playlists=[])
                        assert 'state' in w.request('state',state=state,includeFeatures=False)
                        page=dict(protocol=3,generation=hashlib.sha256(b'personal-label-learning-v3\n').hexdigest(),resetBoundary=[],events=[],nextCursor=0,hasMore=False)
                        time.sleep(.55)
                        reply=w.request('preference_delta',protocol=3,sender=peer['deviceId'],page=page,after=0)
                        assert 'acceptedCursor' in reply,reply
                        assert 'learning' in w.request('preference_training',protocol=3,action='status')
                        assert 'state' in w.request('preference_training',protocol=3,action='learn',downloadConfirmed=True)
                        # Empty labels guarantee the production worker stops before component preparation/download.
                        wait(lambda:call(c,'preference_training',action='status')['learning']['state']=='insufficient_material',20)
                        assert 'state' in w.request('preference_training',protocol=3,action='cancel')
                        def request(op,**args):
                            result=w.request(op,fileProtocol=1,**args)
                            while result.get('pending'): time.sleep(.05); result=w.request('plan_status',job=result['job'])
                            assert 'error' not in result,result
                            return result
                        scope=request('file_open',rootId=root_id,mapping=str(uuid.uuid4()),selection=[''])['scope']
                        name=f'Fixture{index}.wav'; content=b'RIFF'+bytes(4)+b'WAVE'+bytes(51)+bytes([index]); digest=hashlib.sha256(content).hexdigest(); operation=uuid.uuid4().hex
                        request('file_begin',scope=scope,operation=operation,path=name,hash=digest,size=len(content),expectedHash=None,baselineVersion='')
                        request('file_chunk',scope=scope,operation=operation,offset=0,data=base64.b64encode(content).decode())
                        assert request('file_finish',scope=scope,operation=operation)['committed']
                        ack=request('file_ack',scope=scope,path=name,operation=uuid.uuid4().hex,mutationOperation=operation,hash=digest,localHash=digest,localKnown=True,baselineVersion='')
                        assert request('file_delete',scope=scope,path=name,operation=uuid.uuid4().hex,expectedHash=digest,baselineVersion=ack['version'])['committed']
                        assert not (music/name).exists()
                        assert request('file_probe',scope=scope,path='../Outside.wav')['deferred']
                    finally: w.close()
                assert not call(c,'lan_sync_status')['invitationActive']
                assert c.call('lan_sync_control',dict(action='invite',host='127.0.0.1',permissions=ALL))['isError']
                assert not call(c,'lan_sync_status')['invitationActive']
                live=Wire(old); call(c,'lan_sync_control',action='revoke',deviceId=old['deviceId'])
                try: live.request('roots'); raise AssertionError('Revoked session remained usable')
                except (OSError,EOFError): pass
                finally: live.close()
                def rejected(peer):
                    w=Wire(peer,auth=False)
                    try: w.send(dict(op='auth',protocol=4,deviceId=peer['deviceId'],token=peer['token'])); assert w.receive()['code']=='unauthorized'
                    finally: w.close()
                rejected(old); rejected(dict(new,deviceId=uuid.uuid4().hex))
                replacement=pair(old['deviceId']); assert replacement['token']!=old['token']; rejected(old)
                peers=[new,replacement]
                call(c,'lan_sync_control',action='stop')
                print('PASS real TLS/MCP new + legacy all capabilities, user/learning exchange, download authorization without download, conditional files/backups, revoke/session close, unknown device and ID reuse refusal',flush=True)
            finally: stop(pid)
        before=json.loads(path.read_text())
        before['devices'][new['deviceId']]['permissions']=dict.fromkeys(ALL,False)
        path.write_text(json.dumps(before))
        for iteration in range(2):
            with McpClient(a.exe,data=data,timeout=90) as c:
                pid=call(c,'get_diagnostics')['processId']
                try:
                    status=call(c,'lan_sync_status'); assert not status['enabled']
                    assert all(d['permissions']==ALL for d in status['devices'])
                    saved=json.loads(path.read_text())
                    if iteration==0:
                        expected=json.loads(json.dumps(before))
                        for record in expected['devices'].values(): record['permissions']=ALL; record['permissionMigration']='paired_all'
                        assert saved==expected,'Migration changed non-permission configuration'
                        call(c,'lan_sync_control',action='start',port=0); port=call(c,'lan_sync_status')['port']
                        for peer in peers:
                            peer['port']=port; w=Wire(peer)
                            try: assert w.request('capabilities')['permissions']==ALL
                            finally: w.close()
                        call(c,'lan_sync_control',action='stop'); stable=json.loads(path.read_text())
                    else: assert saved==stable,'Repeated migration changed configuration'
                finally: stop(pid)
        print('PASS persisted restart credentials, false grants normalized once, unchanged disabled service/bind/non-permission settings and idempotent next restart',flush=True)

if __name__=='__main__': main()
