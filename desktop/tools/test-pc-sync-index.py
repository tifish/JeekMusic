"""Real isolated Windows library scanner + TLS plan integration; synthetic loopback only."""
import argparse,importlib.util,json,os,struct,subprocess,tempfile,time,uuid
from pathlib import Path
from urllib.parse import urlsplit
from mcp_client import McpClient

p=argparse.ArgumentParser();p.add_argument('--exe',type=Path,required=True);p.add_argument('--out',type=Path,required=True);a=p.parse_args()
out=a.out.resolve();assert Path('desktop/artifacts').resolve() in out.parents;out.mkdir(parents=True,exist_ok=True)
spec=importlib.util.spec_from_file_location('security',Path(__file__).with_name('test-lan-security.py'));security=importlib.util.module_from_spec(spec);spec.loader.exec_module(security)
call,wait=security.call,security.wait

def wave(marker):
    body=b'WAVEfmt '+struct.pack('<IHHIIHH',16,1,1,16000,32000,2,16)+b'data'+struct.pack('<I',3200)+bytes([marker])*3200
    return b'RIFF'+struct.pack('<I',len(body))+body

with tempfile.TemporaryDirectory(prefix='jeek-pc-index-',ignore_cleanup_errors=True) as tmp:
    home=Path(tmp);data=home/'data';music=home/'music';data.mkdir();music.mkdir()
    for i in range(64):(music/f'{i:03}.wav').write_bytes(wave(i))
    (data/'settings.json').write_text(json.dumps(dict(autoCheckUpdate=False,language='en',closeAction='Exit',onlineCovers=False,onlineLyrics=False)))
    (data/'lan-sync.json').write_text(json.dumps(dict(enabled=False,bindAddress='127.0.0.1',roots={},devices={})))
    with McpClient(a.exe,data=data,timeout=60) as c:
        diagnostics=call(c,'get_diagnostics');assert Path(diagnostics['dataDirectory']).resolve()==data.resolve();pid=diagnostics['processId'];wire=None
        try:
            call(c,'add_folder',path=str(music));call(c,'lan_sync_control',action='start',port=0)
            status=wait(lambda:(lambda s:s if s['roots'] and call(c,'get_status')['trackCount']==64 and not call(c,'get_status')['scanning'] else None)(call(c,'lan_sync_status')),30)
            root=status['roots'][0]['rootId'];invite=urlsplit(call(c,'lan_sync_control',action='invite',host='127.0.0.1')['invitation']);pin,secret=invite.path.strip('/').split('/')
            peer=dict(host='127.0.0.1',port=status['port'],pin=pin,deviceId=uuid.uuid4().hex)
            pairing=security.Wire(peer,auth=False);pairing.send(dict(op='pair',deviceId=peer['deviceId'],name='Synthetic PC index',invitation=secret));peer['token']=pairing.receive()['token'];pairing.close()
            wire=security.Wire(peer)
            def request(op,**args):
                deadline=time.monotonic()+40
                while True:
                    r=wire.request(op,fileProtocol=3,**args)
                    while r.get('pending'):
                        time.sleep(.05);r=wire.request('plan_status',job=r['job'])
                    if r.get('code')!='busy':break
                    assert time.monotonic()<deadline,'Isolated busy retry deadline';time.sleep(.1)
                assert 'error' not in r and not r.get('deferred') and not r.get('conflict'),r
                return r
            scope=request('file_open',rootId=root,mapping=str(uuid.uuid4()),selection=[''])['scope']
            def revision():return request('file_revision',scope=scope)['revision']
            def stats():
                s=call(c,'lan_sync_status');return dict(cache=s['manifestCache'],observations=s['fileSync']['metadataObservations'],hashBytes=s['fileSync']['hashBytes'])
            def plan():return request('plan',rootId=root,selection=[''])
            def rows(plan):return request('manifest',rootId=root,plan=plan['plan'],offset=0)['files']
            initial=plan();assert initial['total']==64
            before=stats();warm=plan();after=stats()
            assert warm['cacheHit'] and after['observations']==before['observations']
            samples=[]
            for i in range(6):
                old=revision();target=music/'000.wav';target.write_bytes(wave(i+70));stamp=target.stat().st_mtime_ns+1_000_000_000;os.utime(target,ns=(stamp,stamp))
                wait(lambda:revision()!=old)
                before=stats();current=plan();current_rows=rows(current);after=stats()
                assert current['cacheHit'] and current['incremental'],current
                assert after['cache']['fullScans']==before['cache']['fullScans']
                assert after['observations']-before['observations']<=1
                assert next(r for r in current_rows if r['path']=='000.wav')['modifiedNs']==target.stat().st_mtime_ns
                samples.append(dict(refreshed=current['refreshedPaths'],shared=current['libraryMetadataReused'],observations=after['observations']-before['observations']))
            print('PASS six watched file changes use incremental plans without consuming full-scan budget',flush=True)
            # The normal debounce may scan too; explicitly queue the same production scanner.
            old=revision();target.write_bytes(wave(99));stamp=target.stat().st_mtime_ns+1_000_000_000;os.utime(target,ns=(stamp,stamp));wait(lambda:revision()!=old)
            call(c,'rescan_library')
            wait(lambda:stats()['cache']['sharedEstimatedBytes']>0,15)
            wait(lambda:not call(c,'get_status')['scanning'],15)
            def shared_plan():
                before=stats();p=plan();after=stats()
                if p.get('libraryMetadataReused',0)>0:
                    assert after['observations']==before['observations'];return p
            shared=wait(shared_plan,15)
            print('PASS production library scanner metadata reused with zero additional size/mtime reads',flush=True)
            old=revision();(music/'new.wav').write_bytes(wave(88));wait(lambda:revision()!=old)
            added=plan();assert added['cacheHit'] and added['total']==65
            old=revision();(music/'new.wav').rename(music/'renamed.wav');wait(lambda:revision()!=old)
            renamed=plan();assert renamed['cacheHit'] and any(r['path']=='renamed.wav' for r in rows(renamed))
            old=revision();(music/'renamed.wav').unlink();wait(lambda:revision()!=old)
            deleted=plan();assert deleted['cacheHit'] and deleted['total']==64
            # A directory event requires full discovery, including its pre-existing children.
            outside=home/'incoming';outside.mkdir();(outside/'A.wav').write_bytes(wave(90));old=revision();outside.rename(music/'incoming');wait(lambda:revision()!=old)
            topology=plan();assert not topology['cacheHit'] and topology['total']==65
            final=stats();assert final['hashBytes']==0
            (out/'result.json').write_text(json.dumps(dict(initial=initial,warm=warm,changes=samples,shared=shared,added=added,renamed=renamed,deleted=deleted,topology=topology,diagnostics=final),indent=2))
            print('PASS leaf create/rename/delete, directory move fallback, portable metadata and zero audio hashing',flush=True)
        finally:
            if wire:wire.close()
            call(c,'lan_sync_control',action='stop');subprocess.run(['taskkill','/pid',str(pid),'/f'],capture_output=True);time.sleep(.5)
