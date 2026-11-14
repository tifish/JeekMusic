"""Real emulator UI regression with a temporary production Windows TLS peer.

Requires JeekLanEmpty35's existing Documents/JeekLanFinalDirect SAF grant. Never
touches a physical phone, the daily Windows service, or real music files.
Install the current debug app and instrumentation APK before running.
"""
import argparse
import importlib.util
import json
import socket
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "desktop/tools"))
from mcp_client import McpClient

spec = importlib.util.spec_from_file_location("lan_security", ROOT / "desktop/tools/test-lan-security.py")
security = importlib.util.module_from_spec(spec)
spec.loader.exec_module(security)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--serial", default="emulator-5584")
    parser.add_argument("--exe", type=Path, default=ROOT / "bin/JeekMusic.exe")
    parser.add_argument("--output", type=Path, default=ROOT / "desktop/artifacts/mapping-save-ui")
    args = parser.parse_args()
    assert args.serial.startswith("emulator-")
    adb = ["adb", "-s", args.serial]
    package = "com.tinyfish.jeekmusic.debug"

    def device(*values):
        return subprocess.check_output(adb + list(values), timeout=30)

    def write_config(raw):
        subprocess.run(adb + ["shell", "-T", "run-as", package, "tee", "files/lan-sync.json"],
                       input=raw, stdout=subprocess.DEVNULL, check=True, timeout=20)

    assert device("emu", "avd", "name").splitlines()[0].strip() == b"JeekLanEmpty35"
    original = device("exec-out", "run-as", package, "cat", "files/lan-sync.json")
    args.output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="jeek-mapping-save-") as temp:
        work = Path(temp)
        data = work / "data"
        data.mkdir()
        music = work / "Mapping save fixture"
        music.mkdir()
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        (data / "settings.json").write_text(json.dumps(dict(closeAction="Exit", autoUpdateCheck=False, language="en")))
        (data / "lan-sync.json").write_text(json.dumps(dict(bindAddress="127.0.0.1", port=port, enabled=False)))
        with McpClient(args.exe, data=data) as client:
            pid = security.call(client, "get_diagnostics")["processId"]
            reversed_port = False
            try:
                security.call(client, "add_folder", path=str(music))
                security.call(client, "lan_sync_control", action="start")
                status = security.wait(lambda: (lambda s: s if s["roots"] else None)(security.call(client, "lan_sync_status")))
                invite = urlsplit(security.call(client, "lan_sync_control", action="invite", host="127.0.0.1")["invitation"])
                pin, secret = invite.path.strip("/").split("/")
                peer = dict(host="127.0.0.1", port=port, pin=pin, deviceId=uuid.uuid4().hex)
                wire = security.Wire(peer, auth=False)
                try:
                    wire.send(dict(op="pair", deviceId=peer["deviceId"], name="Mapping save fixture", invitation=secret))
                    peer["token"] = wire.receive()["token"]
                finally:
                    wire.close()
                device("reverse", f"tcp:{port}", f"tcp:{port}")
                reversed_port = True
                mapping = dict(layoutVersion=2, key=str(uuid.uuid4()), rootId=status["roots"][0]["rootId"],
                               name="Mapping save fixture", selection=[""], enabled=False,
                               treeUri="content://com.android.externalstorage.documents/tree/primary%3ADocuments%2FJeekLanFinalDirect",
                               documentId="primary:Documents/JeekLanFinalDirect", volume="external_primary", relative="Documents/JeekLanFinalDirect/")
                device("shell", "am", "force-stop", package)
                write_config(json.dumps(dict(connection=peer, mappings=[mapping], autoCancelled=True)).encode())
                result = subprocess.run(adb + ["shell", "am", "instrument", "-w", "-e", "class",
                    "com.tinyfish.jeekmusic.data.sync.LanMappingSaveTest#actualUiBusyPromptAndLocalSave",
                    "-e", "mappingSaveUi", "true", package + ".test/androidx.test.runner.AndroidJUnitRunner"],
                    capture_output=True, text=True, timeout=150)
                print(result.stdout)
                for name in ("ready", "busy-error", "busy-disabled", "saved-offline"):
                    image = device("exec-out", "run-as", package, "cat", f"cache/mapping-save-ui/{name}.png")
                    (args.output / f"{name}.png").write_bytes(image)
                assert result.returncode == 0 and "OK (1 test)" in result.stdout, result.stderr
                (args.output / "verification.txt").write_text(result.stdout, encoding="utf-8")
            finally:
                device("shell", "am", "force-stop", package)
                write_config(original)
                assert device("exec-out", "run-as", package, "cat", "files/lan-sync.json") == original
                if reversed_port:
                    device("reverse", "--remove", f"tcp:{port}")
                security.call(client, "lan_sync_control", action="stop")
                subprocess.run(["taskkill", "/pid", str(pid), "/f"], capture_output=True, timeout=10)
                device("shell", "am", "start", "-n", package + "/com.tinyfish.jeekmusic.MainActivity")
                time.sleep(.5)


if __name__ == "__main__":
    main()
