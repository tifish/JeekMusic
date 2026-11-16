"""Two isolated production Windows servers + a real Android emulator. Never operate a physical phone."""
import argparse
import base64
import contextlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time

from mcp_client import McpClient

spec = importlib.util.spec_from_file_location("multi_peer_wire", Path(__file__).with_name("test-lan-security.py"))
wire = importlib.util.module_from_spec(spec)
spec.loader.exec_module(wire)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--android", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    assert args.android.startswith("emulator-")
    args.evidence.mkdir(parents=True, exist_ok=True)
    adb = Path(os.environ.get("ANDROID_HOME", "C:/Android/sdk")) / "platform-tools/adb.exe"

    def device(*command, **options):
        return subprocess.run([str(adb), "-s", args.android, *command], check=True, **options)

    device("install", "-r", "mobile/app/build/outputs/apk/debug/app-debug.apk", capture_output=True)
    device("install", "-r", "mobile/app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk", capture_output=True)
    device("shell", "am", "force-stop", "com.tinyfish.jeekmusic.debug", capture_output=True)
    original = subprocess.run([str(adb), "-s", args.android, "exec-out", "run-as", "com.tinyfish.jeekmusic.debug", "cat", "files/lan-sync.json"], capture_output=True)
    if original.returncode != 0:
        raise RuntimeError("Initialize the synthetic debug client before running this fixture")
    original_settings = original.stdout
    json.loads(original_settings)  # Refuse to overwrite an unreadable pre-existing configuration.
    with tempfile.TemporaryDirectory(prefix="jeek-multi-peer-") as temp, contextlib.ExitStack() as stack:
        invitations, clients, ports, processes, music_roots = [], [], [], [], []
        Path(temp, "android-original-settings.json").write_bytes(original_settings)
        try:
            for index in range(2):
                root = Path(temp) / str(index)
                data, music = root / "data", root / "Music"
                data.mkdir(parents=True)
                music.mkdir()
                music_roots.append(music)
                (music / "same.lrc").write_text(f"PC-{'A' if index == 0 else 'B'} synthetic lyrics", encoding="utf-8")
                (data / "settings.json").write_text(json.dumps(dict(closeAction="Exit", autoUpdateCheck=False, analysisMode="Manual", language="en")))
                (data / "lan-sync.json").write_text(json.dumps(dict(bindAddress="127.0.0.1", enabled=False, roots={}, devices={})))
                client = stack.enter_context(McpClient(args.exe, data=data, timeout=60))
                clients.append(client)
                processes.append(wire.call(client, "get_diagnostics")["processId"])
                wire.call(client, "add_folder", path=str(music))
                wire.call(client, "lan_sync_control", action="start", port=0)
                status = wire.wait(lambda: (lambda s: s if s["roots"] else None)(wire.call(client, "lan_sync_status")))
                ports.append(status["port"])
                device("reverse", f"tcp:{status['port']}", f"tcp:{status['port']}", capture_output=True)
                invitations.append(wire.call(client, "lan_sync_control", action="invite", host="127.0.0.1")["invitation"])
            fixture = base64.b64encode(json.dumps(dict(isolated=True, invitations=invitations)).encode()).decode()
            result = device("shell", "am", "instrument", "-w", "-e", "class", "com.tinyfish.jeekmusic.data.sync.LanMultiPeerTest",
                            "-e", "jeekMultiPeerFixture", fixture, "com.tinyfish.jeekmusic.debug.test/androidx.test.runner.AndroidJUnitRunner",
                            capture_output=True, text=True, encoding="utf-8", timeout=240)
            if "OK (1 test)" not in result.stdout or "FAILURES!!!" in result.stdout:
                failure = subprocess.run([str(adb), "-s", args.android, "exec-out", "run-as", "com.tinyfish.jeekmusic.debug", "cat", "cache/multi-peer-ui/failure.png"], capture_output=True)
                if failure.stdout.startswith(b"\x89PNG\r\n\x1a\n"):
                    (args.evidence / "failure.png").write_bytes(failure.stdout)
                raise AssertionError(result.stdout)
            for index, music in enumerate(music_roots):
                assert (music / "phone.lrc").read_text(encoding="utf-8") == f"PHONE-{index+1}"
            for name in ("second-offline", "first-selected"):
                png = device("exec-out", "run-as", "com.tinyfish.jeekmusic.debug", "cat", f"cache/multi-peer-ui/{name}.png", capture_output=True).stdout
                assert png.startswith(b"\x89PNG\r\n\x1a\n")
                (args.evidence / f"{name}.png").write_bytes(png)
            report = dict(twoWindowsTlsServers=True, android=args.android, tests=1, syntheticMusic=True,
                          actualSelectorUi=True, privateFileIo=True, publicSafAcceptance=False)
            (args.evidence / "result.json").write_text(json.dumps(report, indent=2) + "\n")
            print("PASS two-PC pairing, explicit mapping routing, different same-path files, offline isolation and real selector UI")
        finally:
            subprocess.run([str(adb), "-s", args.android, "shell", "am", "force-stop", "com.tinyfish.jeekmusic.debug"], capture_output=True)
            encoded = base64.b64encode(original_settings).decode("ascii")
            restore_command = f"'printf %s {encoded} | base64 -d > files/lan-sync.multi-peer-restore && mv -f files/lan-sync.multi-peer-restore files/lan-sync.json'"
            restored = subprocess.run([str(adb), "-s", args.android, "shell", "run-as", "com.tinyfish.jeekmusic.debug", "sh", "-c", restore_command], capture_output=True)
            for client in clients:
                with contextlib.suppress(Exception):
                    wire.call(client, "lan_sync_control", action="stop")
            for port in ports:
                with contextlib.suppress(Exception):
                    device("reverse", "--remove", f"tcp:{port}", capture_output=True)
            for pid in processes:
                subprocess.run(["taskkill", "/pid", str(pid), "/f"], capture_output=True)
            time.sleep(.5)  # Wait for Windows to release SQLite handles before TemporaryDirectory cleanup.
            if restored.returncode != 0:
                recovery = Path(tempfile.mkdtemp(prefix="jeek-multi-peer-recovery-")) / "android-original-settings.json"
                recovery.write_bytes(original_settings)
                raise RuntimeError(f"Android fixture settings restore failed; original retained at {recovery}")


if __name__ == "__main__":
    main()
