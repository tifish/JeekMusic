"""Real production TLS regression: removed roots never inherit reusable SQLite row IDs.
Only synthetic directories in artifacts/lan-sync/root-reuse are touched.
"""
import argparse, json, time, uuid
from pathlib import Path
from urllib.parse import urlsplit
from mcp_client import McpClient
import importlib.util
_spec=importlib.util.spec_from_file_location("lan_fixture",Path(__file__).with_name("test-lan-sync.py"))
_fixture=importlib.util.module_from_spec(_spec);_spec.loader.exec_module(_fixture)
call,wire,audio=_fixture.call,_fixture.wire,_fixture.audio


def main():
    p=argparse.ArgumentParser();p.add_argument("--exe",required=True,type=Path);args=p.parse_args()
    base=Path(__file__).resolve().parents[1]/"artifacts/lan-sync/root-reuse"/uuid.uuid4().hex
    a=base/"A";b=base/"B";data=base/"data";data.mkdir(parents=True)
    audio(a/"Same.wav");audio(b/"Same.wav",tone=7)
    (data/"settings.json").write_text(json.dumps({"autoCheckUpdate":False,"language":"en","closeAction":"Exit"}))
    def wait(c,predicate):
        until=time.monotonic()+45
        while time.monotonic()<until:
            folders=call(c,"list_folders")["folders"]
            if predicate(folders): return folders
            time.sleep(.1)
        raise AssertionError("library operation timed out")
    with McpClient(args.exe,data=data) as c:
        diagnostics=call(c,"get_diagnostics");assert Path(diagnostics["processPath"]).resolve()==args.exe.resolve()
        call(c,"add_folder",path=str(a));folder=wait(c,lambda fs:len(fs)==1)[0];old_id=folder["id"]
        old_root=call(c,"lan_sync_control",action="share",folderId=old_id)["rootId"]
        invitation=urlsplit(call(c,"lan_sync_control",action="invite",host="127.0.0.1")["invitation"])
        pin,secret=invitation.path.strip("/").split("/");connection={"port":invitation.port,"pin":pin,"deviceId":uuid.uuid4().hex}
        connection["token"]=wire(connection,"pair",invitation=secret,name="root identity regression")["token"]
        plan=wire(connection,"plan",rootId=old_root,selection=[""])["plan"]
        assert "data" in wire(connection,"read",rootId=old_root,plan=plan,path="Same.wav",offset=0)
        call(c,"remove_folder",folderId=old_id);wait(c,lambda fs:not fs)
        call(c,"add_folder",path=str(b));new_folder=wait(c,lambda fs:len(fs)==1)[0];assert new_folder["id"]==old_id
        assert "error" in wire(connection,"tree",rootId=old_root,path="")
        assert "error" in wire(connection,"read",rootId=old_root,plan=plan,path="Same.wav",offset=0)
        assert wire(connection,"roots")["roots"]==[]
        status=call(c,"lan_sync_status");assert status["roots"][0]["revoked"] is True
        new_root=call(c,"lan_sync_control",action="share",folderId=old_id)["rootId"];assert new_root!=old_root
        assert "error" in wire(connection,"manifest",rootId=new_root,plan=plan,offset=0)
        new_plan=wire(connection,"plan",rootId=new_root,selection=[""])["plan"]
        assert "data" in wire(connection,"read",rootId=new_root,plan=new_plan,path="Same.wav",offset=0)
        assert "error" in wire(connection,"read",rootId=old_root,plan=plan,path="Same.wav",offset=0)
        proof={"oldFolderId":old_id,"newFolderId":new_folder["id"],"oldRoot":old_root,"newRoot":new_root,"oldRootAndPlanRejected":True,"newRootUnsharedUntilExplicitGrant":True,"sameRelativePath":"Same.wav","diagnostics":status}
        (base/"proof.json").write_text(json.dumps(proof,indent=2));print(json.dumps(proof))
        call(c,"lan_sync_control",action="stop")
        pid=diagnostics["processId"]
    import subprocess
    subprocess.run(["powershell","-NoProfile","-Command",f"Stop-Process -Id {pid}"],check=True,capture_output=True)

if __name__=="__main__":main()

