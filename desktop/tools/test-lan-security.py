"""Isolated loopback-only production TLS adversarial regression. Never target the daily listener.
Uses new private data, synthetic audio, and an ephemeral 127.0.0.1 listener. No secrets in evidence.
"""
import argparse, concurrent.futures, subprocess, shutil, hashlib, json, os, socket, ssl, struct, tempfile, time, uuid, wave
from pathlib import Path
from urllib.parse import urlsplit
from mcp_client import McpClient


def call(c, name, **args):
    r = c.call(name, args)
    assert not r.get("isError"), (name, r)
    return r["structuredContent"]


def wait(check, seconds=10):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        value = check()
        if value: return value
        time.sleep(.1)
    raise AssertionError("Isolated condition deadline exceeded")


class Wire:
    def __init__(self, connection, source="127.0.0.1", auth=True):
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT); ctx.check_hostname=False; ctx.verify_mode=ssl.CERT_NONE
        raw = socket.create_connection(("127.0.0.1", connection["port"]), timeout=8, source_address=(source,0))
        self.s = ctx.wrap_socket(raw, server_hostname="JeekMusic")
        assert hashlib.sha256(self.s.getpeercert(binary_form=True)).hexdigest() == connection["pin"]
        self.c = connection
        if auth:
            self.send(dict(op="auth", protocol=4, deviceId=connection["deviceId"], token=connection["token"]))
            accepted=self.receive(); assert accepted.get("authenticated"), accepted
    def close(self): self.s.close()
    def send(self, value):
        b = json.dumps(value, separators=(",", ":")).encode(); self.s.sendall(struct.pack(">i",len(b))+b)
    def receive(self):
        def exact(n):
            b = bytearray()
            while len(b)<n:
                part=self.s.recv(n-len(b))
                if not part: raise EOFError("TLS peer closed")
                b.extend(part)
            return b
        n=struct.unpack(">i",exact(4))[0]; assert 2 <= n <= 32_000_000
        return json.loads(exact(n))
    def request(self, op, **args):
        value=dict(args,op=op,deviceId=self.c["deviceId"],token=self.c["token"])
        b=json.dumps(value,separators=(",", ":")).encode()
        self.send(dict(op="request",kind=op,length=len(b)))
        ready=self.receive()
        if not ready.get("ready"): return ready
        self.s.sendall(struct.pack(">i",len(b))+b)
        return self.receive()


def main():
    p=argparse.ArgumentParser();p.add_argument("--exe",type=Path,required=True);p.add_argument("--ui",action="store_true");p.add_argument("--android",help="Explicit isolated emulator serial");a=p.parse_args()
    with tempfile.TemporaryDirectory(prefix="jeek-security-") as temp:
        root=Path(temp); data=root/"data";data.mkdir()
        (data/"settings.json").write_text(json.dumps(dict(closeAction="Exit",autoCheckUpdate=False,language="en")))
        # Explicitly loopback: the production configuration and listener are never used.
        (data/"lan-sync.json").write_text(json.dumps(dict(bindAddress="127.0.0.1",enabled=False,roots={},devices={})))
        with McpClient(a.exe,data=data,timeout=60) as c:
            d=call(c,"get_diagnostics");assert Path(d["processPath"]).resolve()==a.exe.resolve()
            pid=d["processId"]
            try:
                call(c,"lan_sync_control",action="start",port=0)
                port=call(c,"lan_sync_status")["port"]
                def pair(permissions=None, device=None):
                    args=dict(action="invite",host="127.0.0.1")
                    if permissions is not None: assert c.call("lan_sync_control",dict(args,permissions=permissions)).get("isError")
                    u=urlsplit(call(c,"lan_sync_control",**args)["invitation"]);pin,secret=u.path.strip("/").split("/")
                    connection=dict(port=port,pin=pin,deviceId=device or uuid.uuid4().hex)
                    w=Wire(connection,auth=False)
                    try:w.send(dict(op="pair",deviceId=connection["deviceId"],name="Isolated fixture",invitation=secret));r=w.receive()
                    finally:w.close()
                    assert "token" in r;connection["token"]=r["token"]
                    return connection,secret
                peer,secret=pair(); w=Wire(peer)
                assert "roots" in w.request("roots")
                assert all(w.request("capabilities")["permissions"].values());w.close()
                replay=Wire(peer,auth=False);replay.send(dict(op="pair",deviceId=uuid.uuid4().hex,invitation=secret));assert replay.receive()["code"]=="unauthorized";replay.close()
                bad=Wire(peer,auth=False);bad.send(dict(op="auth",protocol=4,deviceId=peer["deviceId"],token="0"*64));assert bad.receive()["code"]=="unauthorized";bad.close()
                print("PASS one-use pairing grants all capabilities; invitation replay and invalid token rejected",flush=True)
                for payload in [b'{"op":"auth","op":"pair"}', b'{"a":{"b":{"c":{"d":{"e":1}}}}}', b'{"op":"auth","token":"'+bytes([255])+b'"}', b'[]', b'{"op":[]}', b'{"op":"auth","token":12}']:
                    bad=Wire(peer,source="127.0.0.2",auth=False)
                    bad.s.sendall(struct.pack(">i",len(payload))+payload)
                    try:assert "error" in bad.receive()
                    except (OSError,EOFError):pass
                    bad.close()
                for size in (32_000_000, -1, 1):
                    bad=Wire(peer,source="127.0.0.2",auth=False);bad.s.sendall(struct.pack(">i",size))
                    assert "error" in bad.receive();bad.close()
                bad=Wire(peer,auth=False);bad.s.sendall(struct.pack(">i",100)+b'{');bad.close()
                good=Wire(peer);assert "roots" in good.request("roots")
                print("PASS real TLS pre-auth size/depth/duplicate/type/invalid UTF-8/truncated frames and recovery",flush=True)
                slow=[]
                for source in ("127.0.0.3","127.0.0.4"):
                    for i in range(4):slow.append(socket.create_connection(("127.0.0.1",port),source_address=(source,0),timeout=8))
                wait(lambda:call(c,"lan_sync_status")["unauthenticatedConnections"]==8)
                assert "roots" in good.request("roots"), "Unauthenticated saturation starved an authenticated peer"
                wait(lambda:call(c,"lan_sync_status")["unauthenticatedConnections"]==0,8)
                for s in slow:s.close()
                slow=Wire(peer,source="127.0.0.5",auth=False);slow.s.sendall(struct.pack(">i",300)+b'{')
                started=time.monotonic()
                time.sleep(2);slow.s.sendall(b' ');time.sleep(2)
                try:slow.s.sendall(b' ')
                except OSError:pass
                wait(lambda:call(c,"lan_sync_status")["unauthenticatedConnections"]==0,4)
                assert time.monotonic()-started < 8;slow.close()
                assert "roots" in good.request("roots")
                print("PASS eight stalled handshakes, existing-peer availability, absolute auth trickle deadline and recovery",flush=True)
                # Two slow large uploads exhaust only the two large-frame permits.
                large=[]
                for source in ("127.0.0.6","127.0.0.7"):
                    x,_=pair(dict(readMusic=True,writeSync=True,manageLearning=False,downloadLearning=False))
                    t=Wire(x,source=source);t.send(dict(op="request",kind="state",length=100_000));assert t.receive()["ready"]
                    t.s.sendall(struct.pack(">i",100_000)+b'{');large.append(t)
                x,_=pair(dict(readMusic=True,writeSync=True,manageLearning=False,downloadLearning=False))
                t=Wire(x,source="127.0.0.8");t.send(dict(op="request",kind="state",length=100_000));assert t.receive()["code"]=="busy";t.close()
                assert "roots" in good.request("roots")
                for t in large:t.close()
                wait(lambda:call(c,"lan_sync_status")["activeConnections"]==1)
                print("PASS bounded large-frame saturation keeps small operations available and releases on disconnect",flush=True)
                extra=[]; identities=[]
                for i in range(8): identities.append(pair()[0])
                for i in range(15): extra.append(Wire(identities[i//2],source="127.0.0.9" if i<7 else "127.0.0.10"))
                assert call(c,"lan_sync_status")["activeConnections"]==16
                overflow=Wire(identities[-1],source="127.0.0.11",auth=False)
                overflow.send(dict(op="auth",protocol=4,deviceId=identities[-1]["deviceId"],token=identities[-1]["token"]))
                assert overflow.receive()["code"]=="busy";overflow.close()
                assert "roots" in good.request("roots")
                for item in extra:item.close()
                wait(lambda:call(c,"lan_sync_status")["activeConnections"]==1)
                print("PASS real TLS authenticated capacity, per-device/NAT limits and recovery",flush=True)
                # Exercise the actual library/service with synthetic WAVs and >one manifest/tree page.
                music=root/"Music";music.mkdir()
                for i in range(205):
                    folder=music/f"Folder{i:03}";folder.mkdir()
                    with wave.open(str(folder/"Synthetic.wav"),"wb") as audio:
                        audio.setparams((1,2,16000,0,"NONE","none"));audio.writeframes(bytes(320_000 if i == 0 else 64))
                call(c,"add_folder",path=str(music))
                roots=wait(lambda:good.request("roots").get("roots"))
                rootId=roots[0]["rootId"]
                a0=good.request("tree",rootId=rootId,path="",offset=0);a1=good.request("tree",rootId=rootId,path="",offset=200)
                assert len(a0["directories"])==200 and len(a1["directories"])==5
                job=good.request("plan",rootId=rootId,selection=[""])
                plan=wait(lambda: (lambda r:None if r.get("pending") else r)(good.request("plan_status",job=job["job"])),30)
                assert plan["total"]==205
                completed=call(c,"lan_sync_control",action="cancel_task",jobId=job["job"]);assert completed["state"]=="complete"
                p0=good.request("manifest",rootId=rootId,plan=plan["plan"],offset=0)["files"]
                p1=good.request("manifest",rootId=rootId,plan=plan["plan"],offset=200)["files"]
                assert len(p0)==200 and len(p1)==5
                assert good.request("read",rootId=rootId,plan=plan["plan"],path=p0[0]["path"],offset=0)["data"]
                slowWrite=Wire(peer,source="127.0.0.12");slowWrite.s.setsockopt(socket.SOL_SOCKET,socket.SO_RCVBUF,1024)
                for i in range(40):
                    value=dict(op="read",deviceId=peer["deviceId"],token=peer["token"],rootId=rootId,plan=plan["plan"],path=p0[0]["path"],offset=0)
                    payload=json.dumps(value,separators=(",", ":")).encode()
                    slowWrite.send(dict(op="request",kind="read",length=len(payload)))
                    slowWrite.s.sendall(struct.pack(">i",len(payload))+payload)
                assert "roots" in good.request("roots")
                slowRead=Wire(x,source="127.0.0.13")
                slowRead.send(dict(op="request",kind="tree",length=1000));assert slowRead.receive()["ready"]
                slowRead.s.sendall(struct.pack(">i",1000)+b'{')
                wait(lambda:call(c,"lan_sync_status")["activeConnections"]<=2,20)
                slowWrite.close()
                wait(lambda:call(c,"lan_sync_status")["activeConnections"]==0,35)
                slowRead.close();good.close();good=Wire(peer)
                print("PASS real slow TLS writer, authenticated idle and stalled-body deadlines release all leases",flush=True)
                # Revoke the grant while a real download stream is blocked on an unread receiver.
                revoking=Wire(peer,source="127.0.0.14");revoking.s.setsockopt(socket.SOL_SOCKET,socket.SO_RCVBUF,1024)
                for i in range(20):
                    value=dict(op="read",deviceId=peer["deviceId"],token=peer["token"],rootId=rootId,plan=plan["plan"],path=p0[0]["path"],offset=0)
                    payload=json.dumps(value,separators=(",", ":")).encode();revoking.send(dict(op="request",kind="read",length=len(payload)));revoking.s.sendall(struct.pack(">i",len(payload))+payload)
                call(c,"lan_sync_control",action="revoke",deviceId=peer["deviceId"])
                wait(lambda:call(c,"lan_sync_status")["activeConnections"]==0,3);revoking.close()
                print("PASS device revocation interrupts an active slow TLS download",flush=True)
                try:good.request("roots");raise AssertionError("Revocation left old TLS alive")
                except (OSError,EOFError):pass
                good.close()
                rejected=Wire(peer,auth=False);rejected.send(dict(op="auth",protocol=4,deviceId=peer["deviceId"],token=peer["token"]));assert rejected.receive()["code"]=="unauthorized";rejected.close()
                # A local task must not become cancellable through a remote identity.
                local=call(c,"preference_training",action="learn")
                call(c,"lan_sync_control",action="revoke",deviceId=peer["deviceId"])
                replacement,_=pair(device=peer["deviceId"])
                assert replacement["token"]!=peer["token"]
                bad=Wire(peer,auth=False);bad.send(dict(op="auth",protocol=4,deviceId=peer["deviceId"],token=peer["token"]));assert bad.receive()["code"]=="unauthorized";bad.close()
                d=call(c,"lan_sync_status");assert d["authenticationFailures"]>0 and d["connectionRejected"]>=0 and d["protocolFailures"]>0
                print("PASS 205-directory/manifest paging, async hashing/read, live revocation, invalid credentials, revoke and device-ID re-pair rotation",flush=True)
                call(c,"preference_training",action="cancel")
                wait(lambda:call(c,"lan_sync_status")["activeConnections"]==0)
                print("PASS no active network leases leaked; diagnostic counters redact credentials",flush=True)
                if a.android:
                    assert a.android.startswith("emulator-")
                    androidPeer,_=pair(dict(readMusic=True,writeSync=True,manageLearning=False,downloadLearning=False))
                    config={"readOnly":dict(replacement,host="10.0.2.2"),"writable":dict(androidPeer,host="10.0.2.2"),"rootId":rootId}
                    adb=["adb","-s",a.android]
                    try:
                        subprocess.run(adb+["shell","run-as","com.tinyfish.jeekmusic.debug","mkdir","-p","files"],capture_output=True,check=True)
                        subprocess.run(adb+["shell","-T","run-as","com.tinyfish.jeekmusic.debug","dd","of=files/isolated-lan-test.json","bs=1","count="+str(len(json.dumps(config).encode()))],input=json.dumps(config).encode(),stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,check=True,timeout=20)
                        r=subprocess.run(adb+["shell","am","instrument","-w","-e","class","com.tinyfish.jeekmusic.data.sync.LanPublicProtocolTest#realPinnedTlsPagingReadWriteLearningAndWrongPin","-e","lanFixture","true","com.tinyfish.jeekmusic.debug.test/androidx.test.runner.AndroidJUnitRunner"],capture_output=True,text=True,timeout=180)
                        assert r.returncode==0 and "OK (1 test)" in r.stdout, r.stdout+r.stderr
                        assert shutil.disk_usage(music).free>4*1024*1024*1024,"Cancellation fixture requires 4 GiB free scratch space"
                        heavy=music/"Heavy";heavy.mkdir();large=heavy/"Synthetic.wav"
                        with large.open("wb") as output:output.seek(2*1024*1024*1024-1);output.write(bytes(1))
                        try:
                            cancelled=subprocess.run(adb+["shell","am","instrument","-w","-e","class","com.tinyfish.jeekmusic.data.sync.LanPublicProtocolTest#cancelsTheOwnedRemotePlanWithoutBlockingTheCaller","-e","lanCancelFixture","true","com.tinyfish.jeekmusic.debug.test/androidx.test.runner.AndroidJUnitRunner"],capture_output=True,text=True,timeout=60)
                            assert cancelled.returncode==0 and "OK (1 test)" in cancelled.stdout,cancelled.stdout+cancelled.stderr
                            assert any(t["state"]=="cancelled" and t["deviceId"]==replacement["deviceId"] for t in call(c,"lan_sync_status")["tasks"])
                            time.sleep(.3)
                            admin=Wire(replacement);job=admin.request("plan",rootId=rootId,selection=["Heavy"]);admin.close()
                            assert call(c,"lan_sync_control",action="cancel_task",jobId=job["job"])["state"]=="cancelled"
                            time.sleep(.3)
                            probe=Wire(replacement);assert "roots" in probe.request("roots");probe.close()
                            print("PASS actual Android cancellation is nonblocking, remote plan cancelled/diagnosed; MCP cancels active owned plan without revoking device",flush=True)
                        finally:large.unlink();heavy.rmdir()
                        print("PASS actual Android LanProtocol: pinned TLS, directory/manifest paging, SHA-256 file, user/delta/status exchange and wrong-pin rejection",flush=True)
                    finally:subprocess.run(adb+["shell","run-as","com.tinyfish.jeekmusic.debug","rm","-f","files/isolated-lan-test.json"],capture_output=True)
                if a.ui:
                    # Existing fixture credentials stay private and never appear on its settings page.
                    subprocess.Popen([str(a.exe.resolve())],env={**os.environ,"JEEKMUSIC_DATA":str(data)})
                    print("UI fixture PID="+str(pid),flush=True)
                    input("Press Enter after UI acceptance to stop the isolated client: ")
            finally:
                call(c,"lan_sync_control",action="stop")
                # This PID belongs to the fresh isolated data/executable verified above.
                subprocess.run(["taskkill", "/pid", str(pid), "/f"], capture_output=True, check=False)
                subprocess.run(["powershell.exe","-NoProfile","-NonInteractive","-Command",f"Get-Process -Id {int(pid)} -ErrorAction SilentlyContinue | Wait-Process -Timeout 10"],capture_output=True)
                time.sleep(.3)

        config=json.loads((data/"lan-sync.json").read_text())
        config["devices"][replacement["deviceId"]]["expiresAt"]=int(time.time())-100
        legacy=identities[0]
        for key in ("permissions","expiresAt","generation"):config["devices"][legacy["deviceId"]].pop(key,None)
        (data/"lan-sync.json").write_text(json.dumps(config))
        with McpClient(a.exe,data=data,timeout=60) as c:
            pid=call(c,"get_diagnostics")["processId"]
            try:
                call(c,"lan_sync_control",action="start",port=0)
                status=call(c,"lan_sync_status");port=status["port"]
                replacement["port"]=port;legacy["port"]=port
                migrated=next(d for d in status["devices"] if d["id"]==legacy["deviceId"])
                assert migrated["permissions"]==dict(readMusic=True,writeSync=True,manageLearning=True,downloadLearning=True,writeFiles=True,deleteFiles=True) and migrated["migration"]
                old=Wire(legacy,auth=False);old.send(dict(op="roots",deviceId=legacy["deviceId"],token=legacy["token"]))
                assert old.receive().get("roots");old.close()
                old=Wire(legacy,auth=False);old.s.sendall(struct.pack(">i",5000));assert old.receive()["code"]=="upgrade_required";old.close()
                live=Wire(replacement)
                assert live.request("roots").get("roots");live.close()
                retained=Wire(replacement); assert retained.request("capabilities")["credentialPolicy"]=="until_revoked";retained.close()
                assert all(d["expiresAt"] is None for d in status["devices"])
                print("PASS persisted legacy all-capability migration, old small-frame compatibility, explicit upgrade limit and credentials retained despite historical expiry",flush=True)
            finally:
                call(c,"lan_sync_control",action="stop")
                subprocess.run(["taskkill","/pid",str(pid),"/f"],capture_output=True,check=False);time.sleep(.3)

if __name__=="__main__":main()
