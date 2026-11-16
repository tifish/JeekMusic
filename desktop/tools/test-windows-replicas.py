"""Two real Windows clients, production TLS and isolated music/data directories."""
import argparse
import contextlib
import json
import os
import sqlite3
import subprocess
import tempfile
import time
import wave
import shutil
import importlib.util
import uuid
import signal
import concurrent.futures
import atexit
from pathlib import Path
from mcp_client import McpClient

group_spec=importlib.util.spec_from_file_location("replica_group_fixture",Path(__file__).with_name("test-sync-groups.py"))
group_fixture=importlib.util.module_from_spec(group_spec);group_spec.loader.exec_module(group_fixture)

def call(client,tool,**arguments):
    result=client.call(tool,arguments)
    assert not result.get("isError"),(tool,result)
    return result["structuredContent"]

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe",type=Path,required=True)
    parser.add_argument("--evidence",type=Path,required=True)
    parser.add_argument("--windows-ui",action="store_true")
    parser.add_argument("--android",nargs=2)
    parser.add_argument("--recovery",action="store_true")
    args=parser.parse_args();args.evidence.mkdir(parents=True,exist_ok=True)
    pids=[];androids=[];external=[]
    def restart_restored_emulators():
        for android in androids:
            if not (args.evidence/f"private-recovery-{android.serial}.tar").exists():
                with contextlib.suppress(Exception):android.run("shell","am","start","-n",android.package+"/com.tinyfish.jeekmusic.MainActivity")
    atexit.register(restart_restored_emulators)
    with tempfile.TemporaryDirectory(prefix="jeek-replicas-") as temporary,contextlib.ExitStack() as stack:
        base=Path(temporary);clients=[];roots=[];data=[]
        try:
            for index in range(2):
                private=base/str(index);private.mkdir();data.append(private)
                music=base/f"music-{index}";music.mkdir();roots.append(music)
                with wave.open(str(music/"same.wav"),"wb") as audio:
                    audio.setparams((1,2,16000,0,"NONE","none"));audio.writeframes(bytes(32000))
                os.utime(music/"same.wav",ns=(1_790_000_000_000_000_000,)*2)
                (private/"settings.json").write_text(json.dumps(dict(closeAction="Exit",autoUpdateCheck=False,analysisMode="Manual",language="en")))
                (private/"lan-sync.json").write_text(json.dumps(dict(bindAddress="127.0.0.1",enabled=False,roots={},devices={})))
                client=stack.enter_context(McpClient(args.exe,data=private,timeout=120));clients.append(client)
                pids.append(call(client,"get_diagnostics")["processId"])
                for attempt in range(100):
                    if call(client,"lan_sync_status")["userDataSyncReady"]:break
                    time.sleep(.1)
                call(client,"add_folder",path=str(music))
                for attempt in range(100):
                    with contextlib.closing(sqlite3.connect(private/"library.db")) as db:
                        track=db.execute("select id,track_key,mtime,size from track where file_name='same.wav'").fetchone()
                    if track:break
                    time.sleep(.1)
                assert track,"Actual scanner did not index synthetic WAV"
                with contextlib.closing(sqlite3.connect(private/"library.db")) as db:
                    db.execute("insert into tag_override values(?,?,?,?)",(track[1],"MOOD","NON_SPORT" if index==0 else "SPORT",30 if index==0 else 10))
                    db.execute("insert into lyrics_offset values(?,?,?)",(track[1],150 if index==0 else 50,30 if index==0 else 10));db.commit()
                if index==0:
                    call(client,"singing_language",action="set",trackIds=[track[0]],value="CHINESE")
                    scores=dict(vocal=1,classical=1,calm=1,energy=1,orchestra=1)
                    evidence=dict(contract=6,segments=[dict(start_ms=0,end_ms=1000,additional=False,sample_count=16000,decode_attempts=1,model_attempts=1,mean=scores,frames=[scores])],additional_reasons=[],feature_complete=True,elapsed_ms=1,rhythm_contract=1,rhythm_state="not_needed")
                    features=dict(bpm=100,bpm_confidence=1,loudness=-20,loudness_range=1,onset_rate=1,centroid_hz=1000,low_ratio=.5,vocal_score=1,classical_score=1,calm_level=1,energy_level=1,orchestra_level=1,analysis_evidence=json.dumps(evidence),analysis_attempts=1)
                    with contextlib.closing(sqlite3.connect(private/"library.db")) as db:
                        db.execute("insert or replace into track_features values(?,?,?,?,?,?)",(track[1],f"{track[2]}:{track[3]}",6,json.dumps(features),0,30));db.commit()
            server,member=clients
            def ui(index,action,optional=False,**arguments):
                command=["powershell.exe","-NoProfile","-ExecutionPolicy","Bypass","-File",str(Path(__file__).with_name("preference-ui.ps1")),"-AppProcess",str(pids[index]),"-Action",action]
                for key,value in arguments.items():command.extend(["-"+key,str(value)])
                result=subprocess.run(command,capture_output=True,text=True,encoding="utf-8",errors="replace",timeout=30)
                if not optional:assert result.returncode==0,result.stdout+result.stderr
                return result
            def reveal(index,action,**arguments):
                result=ui(index,action,optional=True,**arguments)
                if result.returncode==0:return result
                for scroll in range(4):ui(index,"Scroll",optional=True,Type="ScrollBar",Index=scroll,Value="100")
                return ui(index,action,**arguments)
            def settings(index):
                call(clients[index],"show_window");ui(index,"Invoke",Name="Settings",Type="Button");ui(index,"Click",Name="Sync",Type="ListItem")
            call(server,"lan_sync_control",action="start",port=0)
            call(server,"sync_groups",action="create")
            with contextlib.closing(sqlite3.connect(data[0]/"library.db")) as db:folder=db.execute("select id from library_folder").fetchone()[0]
            if args.windows_ui:
                settings(0);reveal(0,"Set",Id="collection-name",Value="Shared acceptance")
                reveal(0,"Invoke",Id="collection-create",Shot=args.evidence/"windows-collection-created.png")
                collection=call(server,"sync_collections",action="list")["collections"][0]["collectionId"]
            else:collection=call(server,"sync_collections",action="create",folderId=folder,name="Shared acceptance")["collectionId"]
            invitation=call(server,"lan_sync_control",action="invite",host="127.0.0.1")["invitation"]
            peer=call(member,"sync_peers",action="pair",invitation=invitation)["peerId"]
            call(member,"sync_groups",action="join",peerId=peer)
            call(member,"sync_groups",action="sync")
            with contextlib.closing(sqlite3.connect(data[1]/"library.db")) as db:folder=db.execute("select id from library_folder").fetchone()[0]
            if args.windows_ui:
                settings(1);reveal(1,"Invoke",Id="replica-bind",Shot=args.evidence/"windows-replica-bound.png")
                mapping=call(member,"sync_status")["mappings"][0]["mappingId"]
                reveal(1,"Focus",Name="Start this library sync",Type="Button",Shot=args.evidence/"windows-replica-bound.png")
            else:mapping=call(member,"sync_collections",action="bind",collectionId=collection,folderId=folder)["mappings"][0]["mappingId"]
            call(member,"sync_groups",action="sync")
            for index in range(2):
                with contextlib.closing(sqlite3.connect(data[index]/"library.db")) as db:
                    diagnostic=dict(tags=db.execute("select * from tag_override").fetchall(),registers=db.execute("select key,value from sync_group_row where kind like 'register:%' and key like 'tag:%'").fetchall())
                (args.evidence/f"metadata-{index}.json").write_text(json.dumps(diagnostic,indent=2)+"\n")
            with contextlib.closing(sqlite3.connect(data[1]/"library.db")) as db:
                key=db.execute("select track_key from track where file_name='same.wav'").fetchone()[0]
                assert db.execute("select value from tag_override where track_key=? and dimension='MOOD'",(key,)).fetchone()[0]=="NON_SPORT","Historical member tag overrode newer main tag"
                assert db.execute("select offset_ms from lyrics_offset where track_key=?",(key,)).fetchone()[0]==150
                assert db.execute("select count(*) from track_features where track_key=? and analyzer_version=6 and failed=0",(key,)).fetchone()[0]==1,"Shared analysis cache did not match actual track"
                assert db.execute("select count(*) from track_language where track_key=? and source='MANUAL'",(key,)).fetchone()[0]==1,"Shared vocal language did not match actual track"
            print("PASS same collection historical tags, lyrics offsets, language and analysis cache matching",flush=True)
            def sync(expected=None):
                deadline=time.monotonic()+180
                while True:
                    status=call(member,"sync_status",mappingId=mapping)["mappings"][0]
                    while status.get("state") in ("scanning","reconciling"):
                        assert time.monotonic()<deadline,"File worker did not finish"
                        time.sleep(.1)
                        status=call(member,"sync_status",mappingId=mapping)["mappings"][0]
                    status=call(member,"sync_control",action="sync",mappingId=mapping)["mappings"][0]
                    if status.get("state") not in ("scanning","reconciling"):break
                    # The automatic timer may own the serialized worker. Wait for it,
                    # then run a fresh round that includes the edits under test.
                    assert time.monotonic()<deadline,"File worker remained busy"
                print("round",status.get("state"),status.get("lastError"),"actions",status.get("lastActions"),flush=True)
                if status.get("lastError")=="unconfirmed_files":
                    with contextlib.closing(sqlite3.connect(data[1]/"windows-replicas/replicas.db")) as db:
                        rows=[dict(kind=kind,key=key,value=json.loads(value)) for kind,key,value in db.execute("select kind,key,value from rows where kind like 'pending:%' or kind like 'landing:%' or kind like 'conflict:%'")]
                    with contextlib.closing(sqlite3.connect(data[0]/"lan-file-sync/file-sync.db")) as db:
                        operations=[dict(state=state,value=json.loads(value)) for state,value in db.execute("select state,json from operations where state not in ('acknowledged','aborted')")]
                    (args.evidence/"unconfirmed.json").write_text(json.dumps(dict(status=status,client=rows,server=operations),indent=2)+"\n")
                if expected is not None:assert status.get("lastError")==expected,status
                return status
            def write(index,name,text,stamp):
                path=roots[index]/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_text(text,encoding="utf-8");os.utime(path,ns=(stamp,stamp))
            initial=1_790_000_000_000_000_000
            write(0,"from-main.lrc","[00:01] Main PC",initial)
            write(1,"from-member.lrc","[00:01] Member PC",initial)
            call(member,"sync_control",action="resume",mappingId=mapping)
            sync("")
            assert (roots[1]/"from-main.lrc").read_text()=="[00:01] Main PC"
            assert (roots[0]/"from-member.lrc").read_text()=="[00:01] Member PC"
            for _ in range(2):
                before=call(server,"lan_sync_status")["fileSync"]
                assert sync("")["lastActions"]==0
                after=call(server,"lan_sync_status")["fileSync"]
                assert after["hashBytes"]==before["hashBytes"] and after["transferBytes"]==before["transferBytes"],(before,after)
            write(1,"from-main.lrc","[00:02] Changed on member",initial+2_000_000_000);sync("")
            assert (roots[0]/"from-main.lrc").read_text()=="[00:02] Changed on member"
            (roots[0]/"from-member.lrc").unlink();sync("");assert not (roots[1]/"from-member.lrc").exists()
            call(member,"sync_control",action="pause",mappingId=mapping)
            backups=call(member,"sync_backups",action="list",mappingId=mapping)["backups"]
            backup=next(b for b in backups if b["path"]=="from-member.lrc")
            call(member,"sync_backups",action="restore",mappingId=mapping,receipt=backup["receipt"])
            assert (roots[1]/"from-member.lrc").read_text()=="[00:01] Member PC"
            call(member,"sync_backups",action="purge",mappingId=mapping,receipt=backup["receipt"])
            call(member,"sync_control",action="resume",mappingId=mapping);sync("")
            assert (roots[0]/"from-member.lrc").read_text()=="[00:01] Member PC"
            write(0,"from-main.lrc","AAAA",initial+4_000_000_000)
            write(1,"from-main.lrc","BBBBBBBB",initial+4_000_000_000)
            sync("unconfirmed_files")
            issues=call(member,"sync_conflicts",action="list",mappingId=mapping)["rows"];assert len(issues)==1,issues
            issue=issues[0]
            if args.windows_ui:
                reveal(1,"Invoke",Name="Sync this library now",Type="Button");time.sleep(1)
                reveal(1,"Focus",Name="Keep this version: this PC",Type="Button",Shot=args.evidence/"windows-replica-conflict.png")
                reveal(1,"Focus",Name="Keep this version: main PC",Type="Button",Shot=args.evidence/"windows-replica-conflict-choices.png")
                reveal(1,"Invoke",Name="Keep this version: this PC",Type="Button");time.sleep(1)
            else:call(member,"sync_conflicts",action="choose",mappingId=mapping,id=issue["id"],replicaId=issue["localReplicaId"],version=issue["localVersion"])
            sync("");assert (roots[0]/"from-main.lrc").read_text()=="BBBBBBBB"
            if args.windows_ui:reveal(1,"Invoke",Name="Pause this library sync",Type="Button",Shot=args.evidence/"windows-replica-paused.png")
            else:call(member,"sync_control",action="pause",mappingId=mapping)
            write(0,"paused.lrc","paused",initial);sync();assert not (roots[1]/"paused.lrc").exists()
            call(member,"sync_control",action="resume",mappingId=mapping);sync("");assert (roots[1]/"paused.lrc").read_text()=="paused"
            call(server,"sync_groups",action="pause")
            write(0,"group-paused.lrc","Group pause",initial);sync("group_paused");assert not (roots[1]/"group-paused.lrc").exists()
            call(server,"sync_groups",action="resume");sync("");assert (roots[1]/"group-paused.lrc").read_text()=="Group pause"
            if args.recovery:
                write(1,"receipt-loss.lrc","A"*1_000_000,initial+10_000_000_000)
                with concurrent.futures.ThreadPoolExecutor(max_workers=1) as pool:
                    future=pool.submit(sync)
                    deadline=time.monotonic()+30
                    while not (roots[0]/"receipt-loss.lrc").exists():
                        assert not future.done(),future.result() if future.done() else ""
                        assert time.monotonic()<deadline,"Upload never landed"
                        time.sleep(.001)
                    # Only the exact isolated coordinator is terminated, after its atomic rename
                    # and before its async result/ACK exchange. The user's daily client is separate.
                    os.kill(pids[0],signal.SIGTERM)
                    failed=future.result(timeout=60)
                assert failed["pendingTransactions"]>0,failed
                write(1,"receipt-loss.lrc","B"*1_000_001,initial+12_000_000_000)
                server=stack.enter_context(McpClient(args.exe,data=data[0],timeout=120));clients[0]=server
                pids[0]=call(server,"get_diagnostics")["processId"]
                for _ in range(100):
                    if call(server,"lan_sync_status")["userDataSyncReady"]:break
                    time.sleep(.1)
                call(server,"lan_sync_control",action="start")
                sync("");assert (roots[0]/"receipt-loss.lrc").read_bytes()==(roots[1]/"receipt-loss.lrc").read_bytes()
                assert call(member,"sync_status",mappingId=mapping)["mappings"][0]["pendingTransactions"]==0
                print("PASS coordinator termination after landing, lost receipt, later local edit and recovery",flush=True)
                write(1,"remote-after-loss.lrc","A"*1_000_000,initial+16_000_000_000)
                with concurrent.futures.ThreadPoolExecutor(max_workers=1) as pool:
                    future=pool.submit(sync);deadline=time.monotonic()+30
                    while not (roots[0]/"remote-after-loss.lrc").exists():
                        assert not future.done(),future.result() if future.done() else ""
                        assert time.monotonic()<deadline;time.sleep(.001)
                    os.kill(pids[0],signal.SIGTERM);failed=future.result(timeout=60)
                assert failed["pendingTransactions"]>0,failed
                write(0,"remote-after-loss.lrc","C"*1_000_002,initial+18_000_000_000)
                server=stack.enter_context(McpClient(args.exe,data=data[0],timeout=120));clients[0]=server;pids[0]=call(server,"get_diagnostics")["processId"]
                for _ in range(100):
                    if call(server,"lan_sync_status")["userDataSyncReady"]:break
                    time.sleep(.1)
                call(server,"lan_sync_control",action="start")
                for _ in range(3):
                    if not sync()["lastError"]:break
                assert (roots[0]/"remote-after-loss.lrc").read_bytes()==(roots[1]/"remote-after-loss.lrc").read_bytes()
                assert (roots[1]/"remote-after-loss.lrc").read_text()=="C"*1_000_002
                print("PASS lost receipt with later upstream edit preserves landing history",flush=True)
                write(0,"download-loss.lrc","A"*1_000_000,initial+20_000_000_000)
                with concurrent.futures.ThreadPoolExecutor(max_workers=1) as pool:
                    future=pool.submit(sync);deadline=time.monotonic()+30
                    while not (roots[1]/"download-loss.lrc").exists():
                        assert not future.done(),future.result() if future.done() else ""
                        assert time.monotonic()<deadline;time.sleep(.001)
                    os.kill(pids[1],signal.SIGTERM)
                    try:future.result(timeout=60)
                    except (ConnectionError,OSError,RuntimeError):pass
                write(1,"download-loss.lrc","D"*1_000_003,initial+22_000_000_000)
                member=stack.enter_context(McpClient(args.exe,data=data[1],timeout=120));clients[1]=member;pids[1]=call(member,"get_diagnostics")["processId"]
                for _ in range(100):
                    if call(member,"lan_sync_status")["userDataSyncReady"]:break
                    time.sleep(.1)
                for _ in range(4):
                    if not sync()["lastError"]:break
                assert (roots[0]/"download-loss.lrc").read_text()=="D"*1_000_003
                assert call(member,"sync_status",mappingId=mapping)["mappings"][0]["pendingTransactions"]==0
                print("PASS member termination after download landing and later local edit",flush=True)
            if args.android:
                port=call(server,"lan_sync_status")["port"]
                for index,serial in enumerate(args.android):
                    android=group_fixture.Android(serial,args.evidence);androids.append(android)
                    android.run("install","-r","mobile/app/build/outputs/apk/debug/app-debug.apk")
                    android.run("install","-r","mobile/app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk")
                    # Android 15 x86_64 ART can abort in its interpreter while resolving a
                    # catch handler with a pending SocketException. Use normal platform
                    # AOT compilation; production networking/cancellation stays unchanged.
                    android.run("shell","cmd","package","compile","-m","speed","-f",android.package)
                    android.run("shell","cmd","package","compile","-m","speed","-f",android.package+".test")
                    stack.enter_context(android.preserve())
                    android.run("shell","svc","wifi","enable")
                    android.run("shell","cmd","wifi","connect-network","AndroidWifi","open")
                    android.run("shell","input","keyevent","KEYCODE_WAKEUP");android.run("shell","wm","dismiss-keyguard")
                    android.run("shell","pm","grant",android.package,"android.permission.READ_MEDIA_AUDIO")
                    android.run("shell","cmd","appops","set",android.package,"MANAGE_EXTERNAL_STORAGE","allow")
                    android.run("reverse",f"tcp:{port}",f"tcp:{port}")
                    directory="JeekGroupAcceptance_"+uuid.uuid4().hex[:8];path="/sdcard/Documents/"+directory
                    android.run("shell","test","!","-e",path);android.run("shell","mkdir","-p",path);external.append((android,path))
                    invitation=call(server,"lan_sync_control",action="invite",host="127.0.0.1")["invitation"]
                    files={f"phone{index+1}.lrc":f"Phone {index+1}"}
                    if index==0:files["phone-delete.lrc"]="Delete from phone"
                    android.phase(invitation=invitation,bindDirectory=directory,collectionId=collection,collectionName="Shared acceptance",fileSync=True,writeFiles=files,expectedFiles={"from-main.lrc":"BBBBBBBB",**files},expectTrackMetadata=True)
                    android.screenshot("collection-bound");android.screenshot("collection-synced")
                    assert (roots[0]/f"phone{index+1}.lrc").read_text()==f"Phone {index+1}"
                sync("")
                assert (roots[1]/"phone2.lrc").read_text()=="Phone 2"
                androids[0].phase(fileSync=True,writeFiles={"phone1.lrc":"Changed on phone 1"},deleteFiles=["phone-delete.lrc"],absentFiles=["phone-delete.lrc"])
                assert (roots[0]/"phone1.lrc").read_text()=="Changed on phone 1","Phone 1 edit was not uploaded to the coordinator"
                assert not (roots[0]/"phone-delete.lrc").exists(),"Phone 1 deletion was not uploaded to the coordinator"
                sync("");assert (roots[1]/"phone1.lrc").read_text()=="Changed on phone 1","Coordinator edit was not forwarded to member PC";assert not (roots[1]/"phone-delete.lrc").exists(),"Coordinator deletion was not forwarded to member PC"
                androids[1].phase(fileSync=True,expectedFiles={"phone1.lrc":"Changed on phone 1"},absentFiles=["phone-delete.lrc"])
                write(0,"pc-fanout.lrc","Main PC fanout",initial+6_000_000_000);sync("")
                for android in androids:android.phase(fileSync=True,expectedFiles={"pc-fanout.lrc":"Main PC fanout"})
                def metadata_round():
                    call(member,"sync_groups",action="sync")
                    for android in androids:android.phase()
                metadata_round()
                head=call(server,"sync_groups",action="status")["head"]
                for _ in range(2):metadata_round()
                assert call(server,"sync_groups",action="status")["head"]==head,"Same-collection metadata generated an echo after convergence"
                print("PASS four-device files and two quiet metadata rounds without echo",flush=True)
            # A directory substitution must be refused even if its path is unchanged.
            call(member,"sync_control",action="pause",mappingId=mapping)
            old=roots[1].with_name("saved-member");roots[1].rename(old);roots[1].mkdir()
            call(member,"sync_control",action="resume",mappingId=mapping)
            sync("local_directory_identity_changed")
            assert not list(roots[1].iterdir())
            roots[1].rmdir();old.rename(roots[1])
            call(member,"sync_control",action="pause",mappingId=mapping)
            oldMain=roots[0].with_name("saved-main");roots[0].rename(oldMain);roots[0].mkdir()
            call(member,"sync_control",action="resume",mappingId=mapping)
            status=sync();assert status["lastError"],status;assert not list(roots[0].iterdir())
            assert (roots[1]/"from-main.lrc").read_text()=="BBBBBBBB"
            roots[0].rmdir();oldMain.rename(roots[0])
            if not args.android:
                write(0,"album.cue",'FILE "same.wav" WAVE\nTRACK 01 AUDIO\nINDEX 01 00:00:00\n',initial+14_000_000_000)
                sync("");assert (roots[1]/"album.cue").read_text()==(roots[0]/"album.cue").read_text()
                (roots[0]/"album.cue").unlink();(roots[0]/"same.wav").unlink()
                # A descriptor is removed before its referenced audio; a safe retry is allowed.
                status=sync()
                if status["lastError"]:sync("")
                assert not (roots[1]/"album.cue").exists() and not (roots[1]/"same.wav").exists()
            call(member,"sync_control",action="pause",mappingId=mapping)
            call(member,"sync_collections",action="unbind",mappingId=mapping)
            assert (roots[1]/"from-main.lrc").read_text()=="BBBBBBBB"
            (args.evidence/"result.json").write_text(json.dumps(dict(realWindowsClients=2,loopbackTls=True,bidirectionalFiles=True,deletion=True,quietRounds=2,noChangeTransferOrHash=True,immutableConflictChoice=True,pauseResume=True,nativeRootSubstitutionRejected=True,unbindKeepsMusic=True,backupRestoreAndPurge=True,cueDependencyDeletion=not bool(args.android),sameCollectionMetadata=True,metadataQuietRounds=2 if args.android else 0,lostReceiptWithLaterEdit=args.recovery,androidEmulators=args.android or [],windowsUi=args.windows_ui,crossHostVerified=False),indent=2)+"\n")
            print("PASS Windows outgoing replica file acceptance",flush=True)
        finally:
            if clients:
                with contextlib.suppress(Exception):
                    status=call(clients[0],"lan_sync_status")
                    (args.evidence/"admission.json").write_text(json.dumps({k:status.get(k) for k in ("activeConnections","unauthenticatedConnections","connectionRejected","authenticationFailures","rateLimited")},indent=2)+"\n")
            for android,path in external:
                with contextlib.suppress(Exception):
                    assert path.startswith("/sdcard/Documents/JeekGroupAcceptance_") and len(path.rsplit("_",1)[1])==8
                    android.run("shell","am","force-stop",android.package);android.run("shell","rm","-rf",path)
            for client in clients:
                with contextlib.suppress(Exception):call(client,"lan_sync_control",action="stop")
            for pid in pids:subprocess.run(["taskkill","/pid",str(pid),"/f"],capture_output=True)
            time.sleep(.5)

if __name__=="__main__":main()
