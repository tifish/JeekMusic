"""Real Sync now interactions using an isolated PC and the synthetic public-SAF AVD only."""
import argparse,contextlib,importlib.util,json,re,sqlite3,struct,subprocess,sys,tempfile,time,uuid
from pathlib import Path
from urllib.parse import urlsplit
from mcp_client import McpClient

p=argparse.ArgumentParser();p.add_argument('--exe',type=Path,required=True);p.add_argument('--android',required=True);p.add_argument('--out',type=Path,required=True);p.add_argument('--check-local-index',action='store_true');args=p.parse_args()
out=args.out.resolve();assert Path('desktop/artifacts').resolve() in out.parents;out.mkdir(parents=True,exist_ok=True)
adb=['C:/Android/sdk/platform-tools/adb.exe','-s',args.android];pkg='com.tinyfish.jeekmusic.debug'
def device(*a):return subprocess.check_output(adb+list(a))
assert device('emu','avd','name').splitlines()[0].strip()==b'JeekLanEmpty35'
assert b'InstrumentationRecord{' not in device('shell','dumpsys','activity','processes')
spec=importlib.util.spec_from_file_location('security',Path(__file__).with_name('test-lan-security.py'));security=importlib.util.module_from_spec(spec);spec.loader.exec_module(security)
sys.path.insert(0,str(Path('mobile/tools').resolve()));import lan_ui as ui
ui.ADB=adb # Guarded above against the dedicated synthetic AVD; never a physical serial.
original_dump=ui.dump
def dump():
    end=time.monotonic()+20
    while True:
        try:return original_dump()
        except AssertionError:
            if time.monotonic()>end:raise
            time.sleep(.5)
ui.dump=dump
def call(c,name,**a):
    r=c.call(name,a);assert not r.get('isError'),name;return r['structuredContent']
def wait(fn,seconds=120):
    end=time.monotonic()+seconds
    while time.monotonic()<end:
        value=fn()
        if value is not None and value is not False:return value
        time.sleep(.2)
    raise AssertionError('Isolated UI fixture timed out')
def wave(padding):
    data=bytes([17])*(1024*1024);fmt=struct.pack('<HHIIHH',1,1,16000,32000,2,16)
    def chunk(name,b):return name+struct.pack('<I',len(b))+b
    body=b'WAVE'+chunk(b'fmt ',fmt)+chunk(b'JUNK',bytes(padding))+chunk(b'data',data)
    return b'RIFF'+struct.pack('<I',len(body))+body
def config(raw):subprocess.run(adb+['shell','-T','run-as',pkg,'tee','files/lan-sync.json'],input=raw,stdout=subprocess.DEVNULL,check=True)
original=device('exec-out','run-as',pkg,'cat','files/lan-sync.json');(out/'original-private.json').write_bytes(original)
device('shell','am','force-stop',pkg)
# Pair as the production user-data identity, rather than a random file-only peer.
# Copy a stopped synthetic debug database read-only; never edit its journal or print it.
with tempfile.TemporaryDirectory(prefix='jeek-ui-device-',ignore_cleanup_errors=True) as identity_tmp:
    for name in ('jeek.db','jeek.db-wal'):
        result=subprocess.run(adb+['exec-out','run-as',pkg,'cat','databases/'+name],capture_output=True)
        if result.returncode==0:Path(identity_tmp,name).write_bytes(result.stdout)
        elif name=='jeek.db':raise AssertionError('Initialized synthetic debug database required')
    with contextlib.closing(sqlite3.connect(Path(identity_tmp,'jeek.db').as_uri()+'?mode=ro',uri=True)) as db:
        device_id=json.loads(db.execute('SELECT content FROM sync_journal WHERE id=1').fetchone()[0])['id']
    assert str(uuid.UUID(device_id))==device_id
last_schedule={}
prefix='AudioReuse-'+uuid.uuid4().hex;target='/sdcard/Documents/JeekLanFinalDirect/'+prefix
assert re.fullmatch(r'/sdcard/Documents/JeekLanFinalDirect/AudioReuse-[0-9a-f]{32}',target)
with tempfile.TemporaryDirectory(prefix='jeek-audio-ui-',ignore_cleanup_errors=True) as tmp:
    work=Path(tmp).resolve();assert work.parent==Path(tempfile.gettempdir()).resolve() and work.name.startswith('jeek-audio-ui-')
    data=work/'data';music=work/'music';data.mkdir();(music/prefix).mkdir(parents=True)
    source=music/prefix/'A.wav';first=wave(4);source.write_bytes(first)
    (data/'settings.json').write_text(json.dumps(dict(autoCheckUpdate=False,language='en',closeAction='Exit')))
    (data/'lan-sync.json').write_text(json.dumps(dict(enabled=False,bindAddress='127.0.0.1',roots={},devices={})))
    with McpClient(args.exe,data=data,timeout=60) as c:
        d=call(c,'get_diagnostics');assert Path(d['dataDirectory']).resolve()==data;pid=d['processId'];port=None
        try:
            call(c,'add_folder',path=str(music));call(c,'lan_sync_control',action='start',port=0)
            status=wait(lambda:(lambda s:s if s['roots'] else None)(call(c,'lan_sync_status')));port=status['port'];root_id=status['roots'][0]['rootId']
            invite=urlsplit(call(c,'lan_sync_control',action='invite',host='127.0.0.1')['invitation']);pin,secret=invite.path.strip('/').split('/')
            peer=dict(host='127.0.0.1',port=port,pin=pin,deviceId=device_id)
            wire=security.Wire(peer,auth=False);wire.send(dict(op='pair',deviceId=peer['deviceId'],name='Synthetic audio reuse UI',invitation=secret));peer['token']=wire.receive()['token'];wire.close()
            mapping=dict(layoutVersion=2,key=str(uuid.uuid4()),rootId=root_id,name='Synthetic tags',selection=[prefix],treeUri='content://com.android.externalstorage.documents/tree/primary%3ADocuments%2FJeekLanFinalDirect',documentId='primary:Documents/JeekLanFinalDirect',volume='external_primary',relative='Documents/JeekLanFinalDirect/',enabled=True)
            device('shell','am','force-stop',pkg);config(json.dumps(dict(connection=dict(peer,host='10.0.2.2'),mappings=[mapping],autoCancelled=True)).encode())
            device('shell','input','keyevent','224');device('shell','wm','dismiss-keyguard');device('shell','am','start','-W','-n',pkg+'/com.tinyfish.jeekmusic.MainActivity')
            def schedule():
                global last_schedule
                rows=call(c,'lan_sync_status')['fileSync'].get('schedules',[]);last_schedule=rows[-1]['schedule'] if rows else {};return last_schedule
            def pause_auto():
                if not schedule().get('cancelled'):ui.tap('lan_cancel')
                wait(lambda:(lambda s:s.get('cancelled') and not s.get('running'))(schedule()))
            def start_manual():
                # Hold automatic hints while observing the button. A fast automatic
                # follow-up can otherwise disable it between a status poll and tap.
                pause_auto()
                wait(lambda:any(n.get('resource-id')=='lan_start' and n.get('enabled')=='true' for n in dump().iter('node')))
                ui.tap('lan_start')
            wait(lambda:schedule().get('cancelled'))
            if any(n.get('resource-id')=='open_settings' for n in dump().iter('node')):ui.tap('open_settings')
            if any(n.get('resource-id')=='settings_group_devices' for n in dump().iter('node')):ui.tap('settings_group_devices')
            for _ in range(12):
                if any(n.get('resource-id')=='lan_start' for n in dump().iter('node')):break
                device('shell','input','swipe','550','1900','550','700','350')
            time.sleep(.5);start_manual()
            initial=wait(lambda:(lambda s:s if s.get('trigger')=='manual' and s.get('state')=='idle' and not s.get('running') and s.get('lastTransferBytes')==len(first) else None)(schedule()))
            print('PASS initial complete-file transfer through the real UI',flush=True)
            assert device('exec-out','cat',target+'/A.wav')==first
            wait(lambda:(lambda s:s if s.get('state')=='idle' and not s.get('running') and not s.get('dirty') and s.get('lastSuccess',0)>=initial['lastSuccess'] else None)(schedule()))
            pause_auto()
            second=wave(1024);source.write_bytes(second);stamp=source.stat().st_mtime_ns+2_000_000_000
            import os;os.utime(source,ns=(stamp,stamp))
            time.sleep(.5);start_manual()
            device('shell','input','swipe','550','1900','550','1100','300')
            # Capture the production UI while this round still owns its reuse summary.
            def rendered():
                tree=dump();nodes=[n for n in tree.iter('node') if n.get('resource-id')=='lan_audio_reused']
                if not nodes:return False
                return tree
            final=wait(lambda:(lambda s:s if s.get('lastSuccess',0)>initial['lastSuccess'] and s.get('trigger')=='manual' and not s.get('running') and s.get('lastAudioReusedFiles')==1 else None)(schedule()))
            assert final['lastTransferBytes']==1076 and final['lastAudioReusedBytes']==1024*1024 and final['backoffUntil']==0
            wait(rendered)
            (out/'android-reused-audio.png').write_bytes(device('exec-out','screencap','-p'))
            assert device('exec-out','cat',target+'/A.wav')==second
            result=dict(initial=initial,final=final,exactSourceBytes=True)
            if args.check_local_index:
                for _ in range(2):
                    stable=wait(lambda:(lambda s:s if s.get('state')=='idle' and not s.get('running') and not s.get('dirty') else None)(schedule()))
                    start_manual()
                    completed=wait(lambda:(lambda s:s if s.get('trigger')=='manual' and s.get('lastSuccess',0)>stable['lastSuccess'] and s.get('state')=='idle' and not s.get('running') else None)(schedule()))
                assert completed['lastLocalIndexHits']==1 and completed['lastLocalIndexMisses']==0
                assert completed['lastLocalIndexRefreshedFiles']==0 and completed['lastMetadataObservations']==0
                assert completed['lastMetadataRows']==0 and completed['lastTransferBytes']==0 and completed['backoffUntil']==0
                wait(rendered)
                (out/'android-local-index-complete.png').write_bytes(device('exec-out','screencap','-p'))
                result['localIndex']=completed
                print('PASS actual no-change Sync now UI: localIndexHit=1 metadataObservations=0 metadataRows=0 transferBytes=0',flush=True)
            (out/'result.json').write_text(json.dumps(result,indent=2))
            print('PASS actual Sync now UI: exact source reconstructed, 1,048,576 audio bytes reused, 1,076 metadata bytes transferred',flush=True)
        finally:
            (out/'last-schedule.json').write_text(json.dumps(last_schedule,indent=2))
            device('shell','am','force-stop',pkg);config(original);assert device('exec-out','run-as',pkg,'cat','files/lan-sync.json')==original
            device('shell','rm','-rf','--',target)
            call(c,'lan_sync_control',action='stop');subprocess.run(['taskkill','/pid',str(pid),'/f'],capture_output=True);time.sleep(.5)
