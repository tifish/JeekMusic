import sys,subprocess,tempfile,json,uuid,time,re,argparse
from pathlib import Path
from urllib.parse import urlsplit
sys.path.insert(0,str(Path('desktop/tools').resolve()))
from mcp_client import McpClient
import importlib.util
spec=importlib.util.spec_from_file_location('security',Path('desktop/tools/test-lan-security.py'));mod=importlib.util.module_from_spec(spec);spec.loader.exec_module(mod)
parser=argparse.ArgumentParser(description='Real Android WorkManager handoff with isolated synthetic public-SAF files only.')
parser.add_argument('--exe',type=Path,required=True);parser.add_argument('--android',required=True);parser.add_argument('--out',type=Path,required=True)
parser.add_argument('--manual',action='store_true',help='Actually tap Sync now, then background the foreground task; capture preparation and return UI')
args=parser.parse_args();root=args.out.resolve();artifacts=Path('desktop/artifacts').resolve()
assert artifacts in root.parents,'Private output must stay beneath desktop/artifacts'
root.mkdir(parents=True,exist_ok=True)
adb=['adb','-s',args.android];pkg='com.tinyfish.jeekmusic.debug' 
def device(*args):return subprocess.check_output(adb+list(args))
def call(c,name,**args):
    r=c.call(name,args);assert not r.get('isError'),(name,r);return r['structuredContent']
def wait(fn,seconds=90):
    end=time.monotonic()+seconds
    while time.monotonic()<end:
        value=fn()
        if value:return value
        time.sleep(.5)
    raise AssertionError('Background fixture timed out')
def write_config(raw):
    subprocess.run(adb+['shell','-T','run-as',pkg,'tee','files/lan-sync.json'],input=raw,stdout=subprocess.DEVNULL,check=True,timeout=15)
assert device('emu','avd','name').splitlines()[0].strip()==b'JeekLanEmpty35'
assert b'InstrumentationRecord{' not in device('shell','dumpsys','activity','processes') # no overlapping instrumentation
original=device('exec-out','run-as',pkg,'cat','files/lan-sync.json')
(root/'background-original-private.json').write_bytes(original)
prefix='Background-'+uuid.uuid4().hex
target='/sdcard/Documents/JeekLanFinalDirect/'+prefix
assert re.fullmatch(r'/sdcard/Documents/JeekLanFinalDirect/Background-[0-9a-f]{32}',target)
with tempfile.TemporaryDirectory(prefix='jeek-background-') as tmp:
    work=Path(tmp);assert work.resolve().parent==Path(tempfile.gettempdir()).resolve() and work.name.startswith('jeek-background-')
    data=work/'data';music=work/'music';data.mkdir();music.mkdir()
    (music/prefix).mkdir()
    (data/'settings.json').write_text(json.dumps(dict(autoCheckUpdate=False,language='en',closeAction='Exit')))
    (data/'lan-sync.json').write_text(json.dumps(dict(enabled=False,bindAddress='127.0.0.1',roots={},devices={})))
    with McpClient(args.exe,data=data,timeout=60) as c:
        diagnostics=call(c,'get_diagnostics');assert Path(diagnostics['dataDirectory']).resolve()==data
        pid=diagnostics['processId'];port=None
        try:
            call(c,'add_folder',path=str(music));call(c,'lan_sync_control',action='start',port=0)
            status=wait(lambda:(lambda s:s if s['roots'] else None)(call(c,'lan_sync_status')));port=status['port'];root_id=status['roots'][0]['rootId']
            invitation=urlsplit(call(c,'lan_sync_control',action='invite',host='127.0.0.1')['invitation']);pin,secret=invitation.path.strip('/').split('/')
            peer=dict(host='127.0.0.1',port=port,pin=pin,deviceId=uuid.uuid4().hex)
            wire=mod.Wire(peer,auth=False);wire.send(dict(op='pair',deviceId=peer['deviceId'],name='Isolated background worker',invitation=secret));peer['token']=wire.receive()['token'];wire.close()
            device('reverse',f'tcp:{port}',f'tcp:{port}')
            mapping=dict(layoutVersion=2,key=str(uuid.uuid4()),rootId=root_id,name='Synthetic background',selection=[prefix],treeUri='content://com.android.externalstorage.documents/tree/primary%3ADocuments%2FJeekLanFinalDirect',documentId='primary:Documents/JeekLanFinalDirect',volume='external_primary',relative='Documents/JeekLanFinalDirect/',enabled=args.manual)
            device('shell','am','force-stop',pkg)
            phone_peer=dict(peer,host='10.0.2.2') if args.manual else peer
            write_config(json.dumps(dict(connection=phone_peer,mappings=[mapping],autoCancelled=args.manual)).encode())
            device('shell','input','keyevent','224');device('shell','wm','dismiss-keyguard');device('shell','am','start','-n',pkg+'/com.tinyfish.jeekmusic.MainActivity')
            def schedule():
                rows=call(c,'lan_sync_status').get('fileSync',{}).get('schedules',[])
                return rows[-1]['schedule'] if rows else {}
            wait(lambda:(lambda s:s if s.get('paused')==(not args.manual) and not s.get('running') else None)(schedule()))
            wave=b'RIFF'+bytes(4)+b'WAVE'+bytes(32*1024*1024-12);(music/prefix/'A.wav').write_bytes(wave)
            if args.manual:
                for index in range(20):(music/prefix/f'{index:04}.wav').write_bytes(b'RIFF'+bytes(4)+b'WAVE'+bytes(52))
            sys.path.insert(0,str(Path('mobile/tools').resolve()))
            import lan_ui as ui
            ui.ADB=adb
            original_dump=ui.dump
            def fresh_dump():
                end=time.monotonic()+20
                while True:
                    try:return original_dump()
                    except AssertionError:
                        if time.monotonic()>=end:raise
                        time.sleep(1)
            ui.dump=fresh_dump
            nodes=ui.dump()
            if any(n.get('resource-id')=='open_settings' for n in nodes.iter('node')):ui.tap('open_settings')
            nodes=ui.dump()
            if any(n.get('resource-id')=='settings_group_devices' for n in nodes.iter('node')):ui.tap('settings_group_devices')
            scrolls=0
            action='lan_start' if args.manual else 'lan_mapping_enabled_'+root_id
            while not any(n.get('resource-id')==action for n in ui.dump().iter('node')):
                scrolls+=1;assert scrolls<=12,'Enable mapping switch was not visible'
                device('shell','input','swipe','550','1900','550','700','350')
            (root/'android-background-paused.png').write_bytes(device('exec-out','screencap','-p'))
            time.sleep(.5) # A tap during the preceding scroll can otherwise only stop the fling.
            ui.tap(action)
            if args.manual:
                wait(lambda:any(n.get('resource-id')=='lan_cancel' and n.get('enabled')=='true' for n in ui.dump().iter('node')),seconds=20)
                (root/'android-preparation.png').write_bytes(device('exec-out','screencap','-p'))
            device('shell','input','keyevent','3')
            observed=wait(lambda:(lambda s:s if s.get('trigger')=='workmanager_fallback' and s.get('foreground')==False and not s.get('running') and (s.get('lastTransferBytes',0)>0 if args.manual else s.get('lastTransferBytes')==len(wave)) or s.get('state')=='retry_backoff' else None)(schedule()),seconds=150)
            if observed.get('state')=='retry_backoff':
                device('shell','input','keyevent','224');device('shell','wm','dismiss-keyguard');device('shell','am','start','-n',pkg+'/com.tinyfish.jeekmusic.MainActivity')
                seen=[]
                for _ in range(6):
                    for n in ui.dump().iter('node'):
                        if n.get('text') and n.get('text') not in seen:seen.append(n.get('text'))
                    device('shell','input','swipe','550','1900','550','700','300')
                print(json.dumps(dict(workerFailure=observed,visibleText=seen)),flush=True)
                raise AssertionError('Background worker reported an actual deferred/error result')
            actual=device('exec-out','cat',target+'/A.wav');assert actual==wave
            if args.manual:
                device('shell','am','start','-W','-n',pkg+'/com.tinyfish.jeekmusic.MainActivity')
                shown=ui.dump()
                assert not any(n.get('text','').startswith(('已取消同步','Sync cancelled')) for n in shown.iter('node'))
                (root/'android-returned.png').write_bytes(device('exec-out','screencap','-p'))
                assert len(device('shell','ls',target).splitlines())==21
            result=dict(trigger='Real UI Sync now followed by Home' if args.manual else 'Real UI re-enable registered a fresh due periodic WorkManager job; app was backgrounded during transfer',natural15MinuteTimingTested=False,appStartMethodCalled=False,foreground=False,bytes=len(actual),contentVerified=True,schedule=observed)
            (root/'background-result.json').write_text(json.dumps(result,indent=2),encoding='utf-8');print(json.dumps(result))
        finally:
            device('shell','am','force-stop',pkg);write_config(original)
            assert device('exec-out','run-as',pkg,'cat','files/lan-sync.json')==original
            device('shell','rm','-rf','--',target)
            if port:device('reverse','--remove',f'tcp:{port}')
            call(c,'lan_sync_control',action='stop');subprocess.run(['taskkill','/pid',str(pid),'/f'],capture_output=True);time.sleep(.5)
            assert work.resolve().parent==Path(tempfile.gettempdir()).resolve() and work.name.startswith('jeek-background-')
