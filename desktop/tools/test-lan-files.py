"""Real production TLS and optional Android reconciler, synthetic loopback fixture only."""
import argparse, base64, hashlib, json, os, subprocess, tempfile, time, uuid
from pathlib import Path
from urllib.parse import urlsplit
import importlib.util
_spec=importlib.util.spec_from_file_location("lan_security",Path(__file__).with_name("test-lan-security.py"));_module=importlib.util.module_from_spec(_spec);_spec.loader.exec_module(_module)
Wire,call,wait=_module.Wire,_module.call,_module.wait
from mcp_client import McpClient

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--exe',type=Path,required=True);parser.add_argument('--android');a=parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='jeek-files-') as temp:
        work=Path(temp);data=work/'data';data.mkdir();music=work/'Music';music.mkdir()
        (data/'settings.json').write_text(json.dumps(dict(closeAction='Exit',autoCheckUpdate=False,language='en')))
        (data/'lan-sync.json').write_text(json.dumps(dict(bindAddress='127.0.0.1',enabled=False,roots={},devices={})))
        with McpClient(a.exe,data=data,timeout=60) as c:
            pid=call(c,'get_diagnostics')['processId']
            try:
                call(c,'add_folder',path=str(music));call(c,'lan_sync_control',action='start',port=0)
                status=wait(lambda:(lambda s:s if s['roots'] else None)(call(c,'lan_sync_status')))
                port=status['port'];root=status['roots'][0]['rootId']
                permissions=dict(readMusic=True,writeSync=False,manageLearning=False,downloadLearning=False,writeFiles=True,deleteFiles=True)
                u=urlsplit(call(c,'lan_sync_control',action='invite',host='127.0.0.1',permissions=permissions)['invitation']);pin,secret=u.path.strip('/').split('/')
                peer=dict(host='127.0.0.1',port=port,pin=pin,deviceId=uuid.uuid4().hex)
                w=Wire(peer,auth=False);w.send(dict(op='pair',deviceId=peer['deviceId'],name='Synthetic file fixture',invitation=secret));peer['token']=w.receive()['token'];w.close()
                with_peer=lambda:Wire(peer)
                def request(op,**args):
                    w=with_peer()
                    try:
                        result=w.request(op,fileProtocol=1,**args)
                        while result.get('pending'):
                            time.sleep(.05);result=w.request('plan_status',job=result['job'])
                        return result
                    finally:w.close()
                scope=request('file_open',rootId=root,mapping=str(uuid.uuid4()),selection=[''])['scope']
                content=b'RIFF'+bytes(4)+b'WAVE'+bytes(51)+b'1';digest=hashlib.sha256(content).hexdigest();operation=uuid.uuid4().hex
                assert request('file_begin',scope=scope,operation=operation,path='Fresh/A.wav',hash=digest,size=len(content),expectedHash=None,baselineVersion='')['offset']==0
                chunk=base64.b64encode(content).decode()
                assert request('file_chunk',scope=scope,operation=operation,offset=0,data=chunk)['offset']==len(content)
                assert request('file_chunk',scope=scope,operation=operation,offset=0,data=chunk)['offset']==len(content)
                assert request('file_finish',scope=scope,operation=operation)['committed'];assert (music/'Fresh/A.wav').read_bytes()==content
                assert request('file_baseline',scope=scope,path='Fresh/A.wav')['version']==''
                acknowledgement=dict(scope=scope,path='Fresh/A.wav',operation=uuid.uuid4().hex,mutationOperation=operation,hash=digest,localHash=digest,localKnown=True,baselineVersion='')
                ack=request('file_ack',**acknowledgement);assert ack['version'];assert request('file_ack',**acknowledgement)['version']==ack['version']
                assert request('file_upload_status',scope=scope,operation=operation)['state']=='acknowledged'
                assert request('file_delete',scope=scope,path='Fresh/A.wav',operation=uuid.uuid4().hex,expectedHash=digest,baselineVersion='')['deferred']
                deletion=uuid.uuid4().hex
                assert request('file_delete',scope=scope,path='Fresh/A.wav',operation=deletion,expectedHash=digest,baselineVersion=ack['version'])['committed']
                assert not (music/'Fresh/A.wav').exists();assert len(list((music/'Fresh').glob('.jeek-sync-*.bak')))==1
                assert request('file_ack',scope=scope,path='Fresh/A.wav',operation=uuid.uuid4().hex,mutationOperation=deletion,hash=None,localHash=None,localKnown=True,baselineVersion=ack['version'])['version']
                assert request('file_probe',scope=scope,path='../outside.wav').get('deferred')
                print('PASS real TLS upload, exact chunk replay, conditional delete, explicit receipt/ack and traversal rejection',flush=True)
                if a.android:
                    adb=str(Path(os.environ.get('ANDROID_HOME','C:/Android/sdk'))/'platform-tools/adb.exe')
                    def device(*args,**kwargs):return subprocess.run([adb,'-s',a.android,*args],check=True,**kwargs)
                    device('install','-r','mobile/app/build/outputs/apk/debug/app-debug.apk',capture_output=True)
                    device('install','-r','mobile/app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk',capture_output=True)
                    device('reverse',f'tcp:{port}',f'tcp:{port}',capture_output=True)
                    fixture=base64.b64encode(json.dumps(dict(peer,rootId=root,isolated=True,prefix='Matrix')).encode()).decode()
                    try:
                        result=device('shell','am','instrument','-w','-e','class','com.tinyfish.jeekmusic.data.sync.LanFileSynchronizerTest','-e','jeekFileFixture',fixture,'com.tinyfish.jeekmusic.debug.test/androidx.test.runner.AndroidJUnitRunner',capture_output=True,text=True,timeout=900)
                        print(result.stdout,flush=True);assert 'OK (3 tests)' in result.stdout
                    finally:device('reverse','--remove',f'tcp:{port}',capture_output=True)
                # Administrative recovery uses the same private store and never a remote endpoint.
                backups=call(c,'lan_file_backups',action='list')['backups'];assert backups
                call(c,'lan_sync_control',action='stop')
                row=next(r for r in backups if r['path']=='Fresh/A.wav')
                assert call(c,'lan_file_backups',action='restore',receipt=row['receipt'])['restored'];assert (music/'Fresh/A.wav').read_bytes()==content
                assert call(c,'lan_file_backups',action='purge',receipt=row['receipt'])['purged']
                print('PASS local administrator verified backup restore/cleanup; baselines unchanged',flush=True)
            finally:
                call(c,'lan_sync_control',action='stop');subprocess.run(['taskkill','/pid',str(pid),'/f'],capture_output=True)
                time.sleep(.5)

if __name__=='__main__':main()
