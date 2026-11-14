"""Actual Android/Windows TLS and reconciliation timings, isolated synthetic data only."""
import argparse
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import uuid
import wave
from urllib.parse import urlsplit
from mcp_client import McpClient

spec=importlib.util.spec_from_file_location("security",Path(__file__).with_name("test-lan-security.py"))
security=importlib.util.module_from_spec(spec);spec.loader.exec_module(security)
call,Wire=security.call,security.Wire

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe",type=Path,required=True);parser.add_argument("--android",required=True)
    parser.add_argument("--report",type=Path,required=True);args=parser.parse_args()
    assert args.android.startswith("emulator-")
    adb=["adb","-s",args.android]
    with tempfile.TemporaryDirectory(prefix="jeek-performance-") as tmp:
        folder=Path(tmp);data=folder/"data";music=folder/"music";selected=music/"Selected"
        data.mkdir();selected.mkdir(parents=True)
        (data/"settings.json").write_text(json.dumps(dict(autoCheckUpdate=False,analysisMode="Manual",closeAction="Exit")))
        (data/"lan-sync.json").write_text(json.dumps(dict(enabled=False,bindAddress="127.0.0.1",roots={},devices={})))
        for index in range(21):
            with wave.open(str(selected/(f"{index:03}.wav")),"wb") as output:
                output.setparams((1,2,16000,0,"NONE","none"));output.writeframes(bytes(8*1024*1024 if index==20 else 64))
        with McpClient(args.exe,data,timeout=60) as client:
            pid=call(client,"get_diagnostics")["processId"]
            try:
                call(client,"add_folder",path=str(music.resolve()));call(client,"lan_sync_control",action="start",port=0)
                status=call(client,"lan_sync_status");root=status["roots"][0]["rootId"]
                invite=urlsplit(call(client,"lan_sync_control",action="invite",host="127.0.0.1")["invitation"])
                pin,secret=invite.path.strip("/").split("/");peer=dict(port=status["port"],pin=pin,deviceId=uuid.uuid4().hex)
                wire=Wire(peer,auth=False)
                try:
                    wire.send(dict(op="pair",deviceId=peer["deviceId"],name="Performance fixture",invitation=secret));peer["token"]=wire.receive()["token"]
                finally:wire.close()
                fixture=json.dumps(dict(connection=dict(peer,host="10.0.2.2"),rootId=root)).encode()
                subprocess.run(adb+["shell","run-as","com.tinyfish.jeekmusic.debug","mkdir","-p","files"],check=True,capture_output=True)
                subprocess.run(adb+["shell","-T","run-as","com.tinyfish.jeekmusic.debug","dd","of=files/isolated-lan-performance.json","bs=1",f"count={len(fixture)}"],input=fixture,check=True,capture_output=True)
                result=subprocess.run(adb+["shell","am","instrument","-w","-e","class","com.tinyfish.jeekmusic.data.sync.LanPublicProtocolTest#measureMetadataAndFileSync","-e","lanPerformanceFixture","true","com.tinyfish.jeekmusic.debug.test/androidx.test.runner.AndroidJUnitRunner"],capture_output=True,text=True,timeout=240)
                assert result.returncode==0 and "OK (1 test)" in result.stdout,result.stdout+result.stderr
                report=json.loads(subprocess.check_output(adb+["exec-out","run-as","com.tinyfish.jeekmusic.debug","cat","files/isolated-lan-performance-result.json"]))
                state=call(client,"lan_sync_status")["fileSync"];assert state["hashBytes"]==0
                report["pc"]=dict(hashBytes=state["hashBytes"],transferBytes=state["transferBytes"],metadataObservations=state["metadataObservations"])
                report["cache"]=call(client,"lan_sync_status").get("manifestCache")
                args.report.parent.mkdir(parents=True,exist_ok=True);args.report.write_text(json.dumps(report,indent=2),encoding="utf-8")
                print(json.dumps({k:v for k,v in report.items() if k not in ("initial","unchanged")},ensure_ascii=True))
                print(json.dumps({k:{a:b for a,b in report[k].items() if a!="stages"} for k in ("initial","unchanged")}))
            finally:
                subprocess.run(adb+["shell","run-as","com.tinyfish.jeekmusic.debug","rm","-f","files/isolated-lan-performance.json","files/isolated-lan-performance-result.json"],capture_output=True)
                call(client,"lan_sync_control",action="stop");client.close()
                subprocess.run(["taskkill","/pid",str(pid),"/f"],capture_output=True)
                subprocess.run(["powershell.exe","-NoProfile","-NonInteractive","-Command",f"Get-Process -Id {int(pid)} -ErrorAction SilentlyContinue | Wait-Process -Timeout 10"],capture_output=True)

if __name__=="__main__":main()
