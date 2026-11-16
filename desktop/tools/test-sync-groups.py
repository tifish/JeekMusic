"""Real production Windows clients, isolated data and loopback TLS. No daily library changes."""
import argparse
import base64
import contextlib
import importlib.util
import json
import os
import hashlib
import io
import tarfile
import subprocess
import tempfile
import time
from pathlib import Path

from mcp_client import McpClient

spec = importlib.util.spec_from_file_location("group_wire", Path(__file__).with_name("test-lan-security.py"))
wire = importlib.util.module_from_spec(spec)
spec.loader.exec_module(wire)
def call(client, tool, **arguments):
    result = client.call(tool, arguments)
    assert not result.get("isError"), (tool, result)
    return result["structuredContent"]


class Android:
    def __init__(self, serial, evidence):
        assert serial.startswith("emulator-")
        self.serial,self.evidence=serial,evidence
        self.adb=Path(os.environ.get("ANDROID_HOME","C:/Android/sdk"))/"platform-tools/adb.exe"
        self.package="com.tinyfish.jeekmusic.debug"
    def run(self,*arguments,**kwargs):
        kwargs.setdefault("timeout",60)
        try:return subprocess.run([str(self.adb),"-s",self.serial,*map(str,arguments)],capture_output=True,check=True,**kwargs)
        except subprocess.TimeoutExpired:raise TimeoutError(f"{self.serial}: emulator operation timed out ({arguments[0]})") from None
        except subprocess.CalledProcessError:raise RuntimeError(f"{self.serial}: emulator operation failed ({arguments[0]})") from None
    @contextlib.contextmanager
    def preserve(self):
        self.run("shell","am","force-stop",self.package)
        self.run("shell","run-as",self.package,"mkdir","-p","databases","shared_prefs","files")
        backup=self.run("exec-out","run-as",self.package,"tar","-cf","-","databases","shared_prefs","files").stdout
        assert backup and len(backup)%512==0
        def manifest(data):
            with tarfile.open(fileobj=io.BytesIO(data)) as archive:
                entries=archive.getmembers()
                assert all(entry.name.split('/')[0] in ("databases","shared_prefs","files") and ".." not in entry.name.split('/') and not entry.issym() and not entry.islnk() for entry in entries)
                return {entry.name:hashlib.sha256(archive.extractfile(entry).read()).digest() for entry in entries if entry.isfile()}
        expected=manifest(backup)
        recovery=self.evidence/f"private-recovery-{self.serial}.tar"
        recovery.write_bytes(backup)
        try:yield
        finally:
            try:
                self.run("shell","am","force-stop",self.package)
                # Fixed directories inside this emulator app, after its complete private DB/settings snapshot.
                self.run("shell","run-as",self.package,"rm","-rf","databases","shared_prefs","files")
                self.run("exec-in","run-as",self.package,"tar","-xf","-",input=backup)
                wire.wait(lambda:manifest(self.run("exec-out","run-as",self.package,"tar","-cf","-","databases","shared_prefs","files").stdout)==expected,seconds=5)
                recovery.unlink()
            except BaseException:
                (self.evidence/f"private-recovery-{self.serial}.tar").write_bytes(backup)
                raise
    def phase(self,**config):
        print("Android phase",self.serial,",".join(config.keys()) or "sync",flush=True)
        fixture=base64.b64encode(json.dumps(dict(isolated=True,**config)).encode()).decode("ascii")
        result=self.run("shell","am","instrument","-w","-e","class","com.tinyfish.jeekmusic.data.sync.LanGroupTest","-e","jeekGroupFixture",fixture,
                        self.package+".test/androidx.test.runner.AndroidJUnitRunner",timeout=180).stdout.decode("utf-8","replace")
        if "OK (1 test)" not in result or "FAILURES!!!" in result:
            with contextlib.suppress(Exception):self.screenshot("failure")
            raise AssertionError(result)
    def screenshot(self,name):
        data=self.run("exec-out","run-as",self.package,"cat",f"cache/group-ui/{name}.png").stdout
        assert data.startswith(b"\x89PNG\r\n\x1a\n")
        (self.evidence/f"{self.serial}-{name}.png").write_bytes(data)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--android",nargs=2,help="Two explicit emulator serials, with debug app/test APK installed")
    parser.add_argument("--windows-ui",action="store_true",help="Operate the real Windows group controls and save screenshots")
    args = parser.parse_args()
    args.evidence.mkdir(parents=True, exist_ok=True)
    processes = []
    with tempfile.TemporaryDirectory(prefix="jeek-groups-") as temp, contextlib.ExitStack() as stack:
        clients = []
        try:
            for index in range(2):
                data = Path(temp, str(index)); data.mkdir()
                (data / "settings.json").write_text(json.dumps(dict(closeAction="Exit", autoUpdateCheck=False, analysisMode="Manual", language="en")))
                (data / "lan-sync.json").write_text(json.dumps(dict(bindAddress="127.0.0.1", enabled=False, roots={}, devices={})))
                client = stack.enter_context(McpClient(args.exe, data=data, timeout=90))
                clients.append(client)
                processes.append(call(client, "get_diagnostics")["processId"])
            server, member = clients
            # Startup merge service is registered after the MCP endpoint.
            wire.wait(lambda: call(server,"lan_sync_status")["userDataSyncReady"], seconds=30)
            wire.wait(lambda: call(member,"lan_sync_status")["userDataSyncReady"], seconds=30)
            call(server,"edit_playlist",action="create",name="Coordinator initial")
            call(member,"edit_playlist",action="create",name="Member offline initial")
            call(server,"lan_sync_control",action="start",port=0)
            if args.windows_ui:
                call(server,"show_window")
                def ui(action,**arguments):
                    command=["powershell.exe","-NoProfile","-ExecutionPolicy","Bypass","-File",str(Path(__file__).with_name("preference-ui.ps1")),"-AppProcess",str(processes[0]),"-Action",action]
                    for key,value in arguments.items():command.extend(["-"+key,str(value)])
                    result=subprocess.run(command,capture_output=True,text=True,encoding="utf-8",errors="replace",timeout=30)
                    assert result.returncode==0,result.stdout+result.stderr
                    return result.stdout
                ui("Invoke",Name="Settings",Type="Button")
                ui("Click",Name="Sync",Type="ListItem")
                ui("Invoke",Name="Create group on this PC",Type="Button")
                assert call(server,"sync_groups",action="status")["joined"]
                ui("Invoke",Name="Pause group sync",Type="Button",Shot=args.evidence/"windows-paused.png")
                assert call(server,"sync_groups",action="status")["paused"]
                ui("Invoke",Name="Resume group sync",Type="Button",Shot=args.evidence/"windows-enabled.png")
                assert not call(server,"sync_groups",action="status")["paused"]
                group=call(server,"sync_groups",action="status")
            else:group = call(server,"sync_groups",action="create")
            invitation = call(server,"lan_sync_control",action="invite",host="127.0.0.1")["invitation"]
            peer = call(member,"sync_peers",action="pair",invitation=invitation)["peerId"]
            call(member,"sync_groups",action="join",peerId=peer)
            def sync():
                result=call(member,"sync_groups",action="sync")
                assert not result["lastError"],result
                assert result["pendingUploads"]==0,result
                return result
            def lists(client): return call(client,"list_playlists")["items"]
            sync()
            assert {p["name"] for p in lists(server)} == {"Coordinator initial","Member offline initial"}, lists(server)
            assert {p["name"] for p in lists(member)} == {"Coordinator initial","Member offline initial"}, lists(member)
            playlist=next(p for p in lists(member) if p["name"]=="Member offline initial")
            call(member,"edit_playlist",action="rename",playlistId=playlist["id"],name="Changed on member")
            sync()
            assert "Changed on member" in {p["name"] for p in lists(server)}
            deleted=next(p for p in lists(server) if p["name"]=="Coordinator initial")
            call(server,"edit_playlist",action="delete",playlistId=deleted["id"])
            sync()
            assert {p["name"] for p in lists(member)}=={"Changed on member"}
            before=call(server,"sync_groups",action="status")["head"]
            for _ in range(3): sync()
            after=call(server,"sync_groups",action="status")["head"]
            assert before==after,(before,after)
            call(server,"sync_groups",action="pause")
            paused=call(member,"sync_groups",action="sync")
            assert paused["lastError"]=="group_paused",paused
            call(server,"sync_groups",action="resume");sync()
            call(server,"lan_sync_control",action="stop")
            call(member,"edit_playlist",action="create",name="Offline durable")
            offline=call(member,"sync_groups",action="sync")
            assert offline["pendingUploads"]>0 and offline["lastError"],offline
            call(server,"lan_sync_control",action="start")
            sync()
            assert "Offline durable" in {p["name"] for p in lists(server)}
            if args.android:
                phones=[Android(serial,args.evidence) for serial in args.android]
                for phone in phones:stack.enter_context(phone.preserve())
                port=call(server,"lan_sync_status")["port"]
                for index,phone in enumerate(phones):
                    phone.run("reverse",f"tcp:{port}",f"tcp:{port}")
                    phone.run("shell","svc","wifi","enable")
                    phone.run("shell","cmd","wifi","connect-network","AndroidWifi","open")
                    phone.run("shell","input","keyevent","224")
                    phone.run("shell","wm","dismiss-keyguard")
                    invitation=call(server,"lan_sync_control",action="invite",host="127.0.0.1")["invitation"]
                    phone.phase(invitation=invitation,createName=f"Group phone {index+1}")
                    phone.screenshot("joined")
                expected=["Changed on member","Offline durable","Group phone 1","Group phone 2"]
                for phone in phones:phone.phase(expectedNames=expected)
                sync()
                if args.windows_ui:ui("Dump",Shot=args.evidence/"windows-members.png")
                assert set(expected)<={p["name"] for p in lists(member)}
                phones[0].phase(renameFrom="Group phone 1",renameTo="Edited on phone 1")
                sync();assert "Edited on phone 1" in {p["name"] for p in lists(member)}
                deleted=next(p for p in lists(server) if p["name"]=="Group phone 2")
                call(server,"edit_playlist",action="delete",playlistId=deleted["id"])
                phones[1].phase(absentNames=["Group phone 2"])
                sync()
                for phone in phones:phone.phase()
                before=call(server,"sync_groups",action="status")["head"]
                for phone in phones:phone.phase()
                sync()
                assert before==call(server,"sync_groups",action="status")["head"]
                call(server,"lan_sync_control",action="stop")
                phones[0].phase(createName="Phone offline durable",offline=True)
                call(server,"lan_sync_control",action="start")
                for phone in phones:phone.phase(expectedNames=["Phone offline durable"])
                sync();assert "Phone offline durable" in {p["name"] for p in lists(member)}
                before_clear=call(server,"preference_data",action="status")["sync"]["generation"]
                phones[0].phase(clearLearning=True)
                after_clear=call(server,"preference_data",action="status")["sync"]["generation"]
                assert before_clear!=after_clear
                phones[1].phase(expectedLearningGeneration=after_clear)
                sync()
                assert call(member,"preference_data",action="status")["sync"]["generation"]==after_clear
                for phone in phones:phone.run("reverse","--remove",f"tcp:{port}")
            actor=call(member,"sync_groups",action="status")["actorId"]
            call(server,"sync_groups",action="remove",actorId=actor)
            refused=call(member,"sync_groups",action="sync")
            assert refused["lastError"]=="member_not_active",refused
            call(member,"edit_playlist",action="create",name="Recovery pending")
            call(member,"sync_groups",action="sync")
            call(server,"sync_groups",action="allow_recovery",actorId=actor)
            call(member,"sync_groups",action="recover")
            sync();assert "Recovery pending" in {p["name"] for p in lists(server)}
            report=dict(realWindowsClients=2,loopbackTls=True,noFileMapping=True,bidirectionalPlaylists=True,
                        deletion=True,quietRounds=3,noEcho=True,offlineOutbox=True,removedMemberRejected=True,
                        explicitRecovery=True,learningClearRelay=bool(args.android),windowsUi=args.windows_ui,androidEmulators=args.android or [],crossHostVerified=False)
            (args.evidence/"result.json").write_text(json.dumps(report,indent=2)+"\n")
            print("PASS real Windows group create/pair/join, bidirectional edits, deletion, no echo, offline retry and removal")
        finally:
            for client in clients:
                with contextlib.suppress(Exception):call(client,"lan_sync_control",action="stop")
            for pid in processes:subprocess.run(["taskkill","/pid",str(pid),"/f"],capture_output=True)
            time.sleep(.5)


if __name__ == "__main__": main()
