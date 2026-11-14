"""Real production TLS regression for independent sync sessions and Android busy retry.

Uses an isolated loopback listener, temporary data and generated silence. Optional
--android targets an explicitly named emulator with the debug/test APKs installed.
No pairing credentials enter console output or the public report.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import time
import uuid
import wave
from urllib.parse import urlsplit

from mcp_client import McpClient

spec = importlib.util.spec_from_file_location("security", Path(__file__).with_name("test-lan-security.py"))
security = importlib.util.module_from_spec(spec)
spec.loader.exec_module(security)
Wire, call, wait = security.Wire, security.call, security.wait


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--android")
    args = parser.parse_args()
    if args.android:
        assert args.android.startswith("emulator-"), "Only an explicitly named emulator is supported"
    report = {}
    with tempfile.TemporaryDirectory(prefix="jeek-session-budget-") as temp:
        root = Path(temp)
        data, music = root / "data", root / "music"
        data.mkdir(); music.mkdir()
        (data / "settings.json").write_text(json.dumps(dict(closeAction="Exit", autoUpdateCheck=False, analysisMode="Manual")))
        (data / "lan-sync.json").write_text(json.dumps(dict(enabled=False, bindAddress="127.0.0.1", roots={}, devices={})))
        with wave.open(str(music / "fixture.wav"), "wb") as audio:
            audio.setparams((1, 2, 16000, 0, "NONE", "none")); audio.writeframes(bytes(3200))
        with McpClient(args.exe, data, timeout=60) as client:
            pid = call(client, "get_diagnostics")["processId"]
            sessions, instrument = [], None
            adb = ["adb", "-s", args.android] if args.android else None
            try:
                call(client, "add_folder", path=str(music.resolve()))
                call(client, "lan_sync_control", action="start", port=0)
                status = call(client, "lan_sync_status")
                assert status["budgets"]["deviceAuthenticatedConnections"] == 4
                assert status["budgets"]["authenticated"] == 16
                invitation = urlsplit(call(client, "lan_sync_control", action="invite", host="127.0.0.1")["invitation"])
                pin, secret = invitation.path.strip("/").split("/")
                peer = dict(port=status["port"], pin=pin, deviceId=uuid.uuid4().hex)
                pairing = Wire(peer, auth=False)
                try:
                    pairing.send(dict(op="pair", deviceId=peer["deviceId"], name="Session budget fixture", invitation=secret))
                    peer["token"] = pairing.receive()["token"]
                finally:
                    pairing.close()
                for _ in range(4):
                    sessions.append(Wire(peer))
                    assert "roots" in sessions[-1].request("roots")
                rejected = Wire(peer, auth=False)
                try:
                    rejected.send(dict(op="auth", protocol=4, deviceId=peer["deviceId"], token=peer["token"]))
                    assert rejected.receive()["code"] == "busy"
                finally:
                    rejected.close()
                report.update(fourIndependentSessions=True, fifthSessionRejected=True, globalLimitUnchanged=True)
                if adb:
                    roots = wait(lambda: sessions[0].request("roots").get("roots"))
                    opened = sessions[0].request("file_open", fileProtocol=3, rootId=roots[0]["rootId"], mapping=uuid.uuid4().hex, selection=[""])
                    fixture = json.dumps(dict(connection=dict(peer, host="10.0.2.2"), scope=opened["scope"])).encode()
                    subprocess.run(adb + ["shell", "run-as", "com.tinyfish.jeekmusic.debug", "mkdir", "-p", "files"], check=True, capture_output=True)
                    subprocess.run(adb + ["shell", "-T", "run-as", "com.tinyfish.jeekmusic.debug", "dd", "of=files/isolated-lan-budget-test.json", "bs=1", f"count={len(fixture)}"], input=fixture, check=True, capture_output=True)
                    before = call(client, "lan_sync_status")["overloadRejected"]
                    instrument = subprocess.Popen(adb + ["shell", "am", "instrument", "-w", "-e", "class", "com.tinyfish.jeekmusic.data.sync.LanPublicProtocolTest#retriesBusyRevisionAfterHostReleasesSession", "-e", "lanBudgetFixture", "true", "com.tinyfish.jeekmusic.debug.test/androidx.test.runner.AndroidJUnitRunner"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
                    wait(lambda: call(client, "lan_sync_status")["overloadRejected"] > before, seconds=20)
                    sessions.pop().close()
                    deadline = time.monotonic() + 80
                    while instrument.poll() is None:
                        assert time.monotonic() < deadline, "Android busy retry exceeded its deadline"
                        # Keep the three competing sessions alive across the 30-second retry.
                        for session in sessions: assert "roots" in session.request("roots")
                        time.sleep(1)
                    stdout, stderr = instrument.communicate(timeout=5)
                    assert instrument.returncode == 0 and "OK (1 test)" in stdout, stdout + stderr
                    report["androidRevisionRetriesBusyAndRecovers"] = True
                else:
                    sessions.pop().close()
                wait(lambda: call(client, "lan_sync_status")["activeConnections"] == 3)
                sessions.append(Wire(peer))
                assert "roots" in sessions[-1].request("roots")
                report["releasedSlotReusable"] = True
            finally:
                for session in sessions: session.close()
                if instrument is not None and instrument.poll() is None:
                    instrument.kill(); instrument.communicate(timeout=10)
                if adb:
                    subprocess.run(adb + ["shell", "run-as", "com.tinyfish.jeekmusic.debug", "rm", "-f", "files/isolated-lan-budget-test.json"], capture_output=True)
                call(client, "lan_sync_control", action="stop")
                client.close()
                subprocess.run(["taskkill", "/pid", str(pid), "/f"], capture_output=True, check=False)
                subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
                                f"Get-Process -Id {int(pid)} -ErrorAction SilentlyContinue | Wait-Process -Timeout 10"], capture_output=True, check=False)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report))


if __name__ == "__main__":
    main()
