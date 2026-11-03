"""Real production TLS and optional Android reconciler, synthetic loopback fixture only."""
import argparse, base64, hashlib, json, os, subprocess, tempfile, time, uuid
from pathlib import Path
from urllib.parse import urlsplit
import importlib.util
_spec=importlib.util.spec_from_file_location("lan_security",Path(__file__).with_name("test-lan-security.py"));_module=importlib.util.module_from_spec(_spec);_spec.loader.exec_module(_module)
Wire,call,wait=_module.Wire,_module.call,_module.wait
from mcp_client import McpClient

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--exe',type=Path,required=True);parser.add_argument('--android');parser.add_argument('--public-saf',action='store_true');parser.add_argument('--auto',action='store_true');parser.add_argument('--benchmark',action='store_true');parser.add_argument('--case');a=parser.parse_args()
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
                u=urlsplit(call(c,'lan_sync_control',action='invite',host='127.0.0.1')['invitation']);pin,secret=u.path.strip('/').split('/')
                peer=dict(host='127.0.0.1',port=port,pin=pin,deviceId=uuid.uuid4().hex)
                w=Wire(peer,auth=False);w.send(dict(op='pair',deviceId=peer['deviceId'],name='Synthetic file fixture',invitation=secret));peer['token']=w.receive()['token'];w.close()
                with_peer=lambda:Wire(peer)
                def request(op,**args):
                    w=with_peer()
                    try:
                        if op in ('file_begin','file_delete','file_ack'):
                            observed=w.request('file_probe',fileProtocol=2,scope=args['scope'],path=args['path'])
                            if op=='file_ack':args.update(remoteVersion=observed.get('metadataVersion'),localVersion=observed.get('metadataVersion'),decision='ack')
                            else:args['expectedVersion']=observed.get('metadataVersion')
                        result=w.request(op,fileProtocol=2,**args)
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
                if a.benchmark:
                    sample=music/'MetadataSample';sample.mkdir()
                    for index in range(128):
                        with (sample/f'{index:03}.wav').open('wb') as f:f.write(b'RIFF'+bytes(4)+b'WAVE');f.truncate(256*1024)
                    (sample/'album.cue').write_text('FILE "000.wav" WAVE\nTRACK 01 AUDIO\n INDEX 01 00:00:00\n')
                    samples=[]
                    for round_ in range(2):
                        request('file_probe',scope=scope,path='MetadataSample/000.wav')
                        before=call(c,'lan_sync_status')['fileSync'];start=time.perf_counter()
                        plan=request('plan',rootId=root,selection=['MetadataSample'])
                        rows=request('manifest',rootId=root,plan=plan['plan'],offset=0)['files']
                        request('file_probe',scope=scope,path='MetadataSample/000.wav')
                        after=call(c,'lan_sync_status')['fileSync']
                        result=dict(round=round_+1,files=len(rows),sampleBytes=128*256*1024,elapsedMs=round((time.perf_counter()-start)*1000,2),metadataObservations=after['metadataObservations']-before['metadataObservations'],hashBytes=after['hashBytes']-before['hashBytes'],transferBytes=after['transferBytes']-before['transferBytes'],sidecarParseBytes=after['sidecarParseBytes']-before['sidecarParseBytes'])
                        assert result['hashBytes']==0 and result['transferBytes']==0 and all('hash' not in r for r in rows)
                        if round_:assert result['sidecarParseBytes']==0
                        samples.append(result)
                    print('METADATA_SAMPLE '+json.dumps(samples),flush=True)
                # Android cases share one independent writer scope per credential, retaining the production eight-scope cap.
                if a.android:
                    adb=str(Path(os.environ.get('ANDROID_HOME','C:/Android/sdk'))/'platform-tools/adb.exe')
                    def device(*args,**kwargs):return subprocess.run([adb,'-s',a.android,*args],check=True,**kwargs)
                    device('install','-r','mobile/app/build/outputs/apk/debug/app-debug.apk',capture_output=True)
                    device('install','-r','mobile/app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk',capture_output=True)
                    device('reverse',f'tcp:{port}',f'tcp:{port}',capture_output=True)
                    if a.auto:
                        invite=urlsplit(call(c,'lan_sync_control',action='invite',host='127.0.0.1')['invitation']);writer_pin,writer_secret=invite.path.strip('/').split('/')
                        writer=dict(host='127.0.0.1',port=port,pin=writer_pin,deviceId=uuid.uuid4().hex)
                        wire=Wire(writer,auth=False);wire.send(dict(op='pair',deviceId=writer['deviceId'],name='Synthetic independent writer',invitation=writer_secret));writer['token']=wire.receive()['token'];wire.close();peer['writer']=writer
                    fixture=base64.b64encode(json.dumps(dict(peer,rootId=root,isolated=True,prefix='Matrix',publicSaf=a.public_saf)).encode()).decode()
                    try:
                        test_class=('LanAutoSyncTest' if a.auto else 'LanFileSynchronizerTest')+('#'+a.case if a.case else '')
                        result=device('shell','am','instrument','-w','-e','class','com.tinyfish.jeekmusic.data.sync.'+test_class,'-e','jeekFileFixture',fixture,'com.tinyfish.jeekmusic.debug.test/androidx.test.runner.AndroidJUnitRunner',capture_output=True,text=True,timeout=900)
                        print(result.stdout,flush=True);assert ('OK (1 test)' if a.auto or a.case else 'OK (5 tests)') in result.stdout
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
