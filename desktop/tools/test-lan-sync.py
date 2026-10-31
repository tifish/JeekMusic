"""Isolated real Windows service fixture + TLS authorization regressions. No user music.

--interactive keeps the production Windows service running for emulator UI tests.
Type invite to enter an invitation directly in the focused emulator pairing field;
the credential is never printed or written to evidence. Other commands: stop/start/status/quit.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import socket
import ssl
import struct
import subprocess
import sys
import time
import uuid
import wave

from mcp_client import McpClient

ROOT = Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "desktop/artifacts/lan-sync"

def call(client, name, **args):
    r=client.call(name,args)
    assert not r.get("isError"), (name,r)
    return r["structuredContent"]

def wire(connection, op, **args):
    context=ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT);context.check_hostname=False;context.verify_mode=ssl.CERT_NONE
    with socket.create_connection(("127.0.0.1",connection["port"]),timeout=20) as tcp, context.wrap_socket(tcp,server_hostname="JeekMusic") as s:
        assert hashlib.sha256(s.getpeercert(binary_form=True)).hexdigest()==connection["pin"]
        value={**connection,**args,"op":op};data=json.dumps(value).encode();s.sendall(struct.pack(">i",len(data))+data)
        def receive(n):
            data=b""
            while len(data)<n:
                part=s.recv(n-len(data));assert part;data+=part
            return data
        count=struct.unpack(">i",receive(4))[0];assert 2<=count<=2_000_000
        return json.loads(receive(count))

def audio(path, seconds=2, tone=0):
    path.parent.mkdir(parents=True,exist_ok=True)
    if path.exists():return
    with wave.open(str(path),"wb") as w:
        w.setparams((1,2,16000,0,"NONE","not compressed"))
        for _ in range(seconds):w.writeframesraw(bytes([tone,0])*16000)
    os.utime(path,ns=(1_600_000_000_123456700,)*2)

def main():
    p=argparse.ArgumentParser();p.add_argument("--interactive",action="store_true");p.add_argument("--exe",type=Path,default=ROOT/"output/JeekMusic.exe");a=p.parse_args()
    ARTIFACTS.mkdir(parents=True,exist_ok=True)
    library=ARTIFACTS/"source/Music"
    audio(library/"Karajan/Album/Artist - First.wav")
    audio(library/"Celtic/Artist - Celtic.wav")
    audio(library/"Unselected/Private.wav")
    audio(library/"Karajan/Album/Artist - Long.wav",1200)
    audio(library/"Celtic/Disc.wav",360)
    (library/"Celtic/Disc.cue").write_text('FILE "Disc.wav" WAVE\n TRACK 01 AUDIO\n TITLE "CUE One"\n INDEX 01 00:00:00\n TRACK 02 AUDIO\n TITLE "CUE Two"\n INDEX 01 03:00:00\n',encoding="utf-8")
    (library/"Karajan/Album/Artist - First.lrc").write_text('[00:00.00]Synthetic lyrics\n',encoding="utf-8")
    second=ARTIFACTS/"second/Music";audio(second/"Karajan/Album/Artist - First.wav",tone=1)
    data=ARTIFACTS/"windows-data";data.mkdir(exist_ok=True)
    (data/"settings.json").write_text(json.dumps({"closeAction":"Exit","autoCheckUpdate":False,"language":"en"}))
    with McpClient(a.exe,data=data) as client:
        diagnostics = call(client, "get_diagnostics")
        assert Path(diagnostics["processPath"]).resolve() == a.exe.resolve(), "A different client answered the fixture"
        call(client,"lan_sync_control",action="start",port=0)
        for folder in [library,second]:call(client,"add_folder",path=str(folder))
        deadline=time.monotonic()+30
        while time.monotonic()<deadline:
            folders=call(client,"list_folders")["items"] if "items" in call(client,"list_folders") else call(client,"list_folders")["folders"]
            if len(folders)>=2 and call(client,"get_status").get("scanning") is not True:break
            time.sleep(.2)
        roots=[call(client,"lan_sync_control",action="share",folderId=f["id"]) for f in folders]
        call(client,"lan_sync_control",action="start")
        status=call(client,"lan_sync_status")
        (ARTIFACTS/"service-public.json").write_text(json.dumps(status,indent=2))
        invite=call(client,"lan_sync_control",action="invite",host="10.0.2.2")["invitation"]
        from urllib.parse import urlsplit
        u=urlsplit(invite);pin,secret=u.path.strip("/").split("/")
        connection={"port":u.port,"pin":pin,"deviceId":uuid.uuid4().hex}
        assert "error" in wire(connection,"roots",token="unpaired")
        assert "error" in wire(connection,"pair",invitation="wrong",name="Test")
        paired=wire(connection,"pair",invitation=secret,name="Security fixture");connection["token"]=paired["token"]
        assert "error" in wire(connection,"pair",invitation=secret,name="Replay")
        root=roots[0]["rootId"]
        assert "error" in wire(connection,"tree",rootId=root,path="../")
        assert "error" in wire(connection,"tree",rootId="unshared",path="")
        assert "error" in wire(connection,"read",rootId=root,plan="fabricated",path="Unselected/Private.wav",offset=0)
        call(client,"lan_sync_control",action="revoke",deviceId=connection["deviceId"])
        assert "error" in wire(connection,"roots")
        print("PASS: real Windows TLS service rejects unpaired/wrong invitation/replay/path traversal/unknown root/unplanned reads/revoked device",flush=True)
        print(json.dumps(status),flush=True)
        if a.interactive:
            for line in sys.stdin:
                cmd=line.strip()
                if cmd=="quit":break
                if cmd=="invite":
                    assert subprocess.check_output(["adb","-s","emulator-5582","emu","avd","name"]).splitlines()[0].strip()==b"JeekLanSync35"
                    invitation=call(client,"lan_sync_control",action="invite",host="10.0.2.2")["invitation"]
                    subprocess.run(["adb","-s","emulator-5582","shell","input","text",invitation],check=True,capture_output=True)
                    print("Entered private one-use invitation into emulator UI",flush=True)
                elif cmd in ("stop","start"):print(call(client,"lan_sync_control",action=cmd),flush=True)
                elif cmd=="status":print(call(client,"lan_sync_status"),flush=True)
                else:print("Unknown fixture command",flush=True)

if __name__=="__main__":main()
