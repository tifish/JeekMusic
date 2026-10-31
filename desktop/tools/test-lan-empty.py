"""Isolated production TLS regression for empty-library pairing and automatic library sharing.

Interactive commands support real PC/Android UI acceptance with synthetic roots only.
Invitations are entered directly into the emulator field, never printed or saved.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import time
import uuid
from urllib.parse import urlsplit
from mcp_client import McpClient

spec = importlib.util.spec_from_file_location("fixture", Path(__file__).with_name("test-lan-sync.py"))
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)
call, wire = fixture.call, fixture.wire


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", required=True, type=Path)
    parser.add_argument("--interactive", action="store_true")
    parser.add_argument("--avd", default="JeekMusicAcceptance")
    parser.add_argument("--serial", default="emulator-5582")
    parser.add_argument("--legacy-grant", action="store_true")
    a = parser.parse_args()
    base = Path(__file__).resolve().parents[1] / "artifacts/lan-empty" / uuid.uuid4().hex
    data = base / "data"; data.mkdir(parents=True)
    (data / "settings.json").write_text(json.dumps({"closeAction": "Exit", "autoCheckUpdate": False, "language": "en", "analysisMode": "Manual", "hotkeysEnabled": False}))
    if a.legacy_grant:
        (data / "lan-sync.json").write_text(json.dumps({"roots": {uuid.uuid4().hex: {"folderId": 1, "name": "Legacy grant without identity", "enabled": True}}}))
    pid = None
    try:
        with McpClient(a.exe, data=data) as c:
            d = call(c, "get_diagnostics"); pid = d["processId"]
            assert Path(d["processPath"]).resolve() == a.exe.resolve()
            assert not call(c, "lan_sync_status")["enabled"], "New/disabled service must remain off"
            status = call(c, "lan_sync_control", action="start", port=0)
            assert status["libraryFolderCount"] == status["sharedFolderCount"] == 0
            if a.legacy_grant:
                assert len(status["roots"]) == 1 and status["roots"][0]["enabled"] is False
            assert c.call("lan_sync_control", {"action": "share", "folderId": 1, "port": 0}).get("isError")
            assert c.call("lan_sync_control", {"action": "invite", "host": "10.0.2.2", "includeQr": True, "port": 0}).get("isError")
            invitation = urlsplit(call(c, "lan_sync_control", action="invite", host="10.0.2.2")["invitation"])
            pin, secret = invitation.path.strip("/").split("/")
            connection = {"port": invitation.port, "pin": pin, "deviceId": uuid.uuid4().hex}
            assert "error" in wire(connection, "roots", token="invalid")
            connection["token"] = wire(connection, "pair", invitation=secret, name="Automatic library sharing regression")["token"]
            assert wire(connection, "roots")["roots"] == []
            def wait_folders(count):
                until = time.monotonic() + 40
                while time.monotonic() < until:
                    folders = call(c, "list_folders")["folders"]
                    if len(folders) == count and not call(c, "get_status")["scanning"]: return folders
                    time.sleep(.1)
                raise AssertionError("Library operation timed out")
            # Unregistered neighbors and parents must never become network namespaces.
            fixture.audio(base / "Unregistered neighbor" / "Private.wav")
            for name in ("First music", "Second music"):
                folder = base / name; fixture.audio(folder / "Synthetic.wav")
                call(c, "add_folder", path=str(folder))
                folders = wait_folders(1 if name == "First music" else 2)
                remote = wire(connection, "roots")["roots"]
                assert len(remote) == len(folders)
                assert {r["folderId"] for r in remote} == {f["id"] for f in folders}
            first = next(f for f in folders if f["name"] == "First music")
            roots = wire(connection, "roots")["roots"]
            first_root = next(r["rootId"] for r in roots if r["folderId"] == first["id"])
            original = Path(first["path"]); offline = base / "Temporarily offline"
            original.rename(offline)
            try:
                unavailable = next(r for r in wire(connection, "roots")["roots"] if r["rootId"] == first_root)
                assert not unavailable["available"] and unavailable["enabled"] and not unavailable["revoked"]
                assert "error" in wire(connection, "tree", rootId=first_root, path="")
            finally: offline.rename(original)
            assert next(r for r in wire(connection, "roots")["roots"] if r["rootId"] == first_root)["available"]
            alias = call(c, "lan_sync_control", action="share", folderId=first["id"])
            assert alias["rootId"] == first_root and alias["deprecated"] and alias["sharingMode"] == "library"
            refused = c.call("lan_sync_control", {"action": "unshare", "folderId": first["id"]})
            assert refused.get("isError") and "Per-folder unshare" in str(refused)
            assert len(wire(connection, "roots")["roots"]) == 2
            assert "error" in wire(connection, "tree", rootId=first_root, path="../")
            assert "error" in wire(connection, "tree", rootId="unregistered", path="")
            call(c, "lan_sync_control", action="stop")
            stopped = call(c, "lan_sync_status")
            assert not stopped["enabled"] and stopped["sharedFolderCount"] == 0
            assert c.call("lan_sync_control", {"action": "share", "folderId": first["id"]}).get("isError")
            assert c.call("lan_sync_control", {"action": "invite", "host": "10.0.2.2"}).get("isError")
            assert not call(c, "lan_sync_status")["enabled"]
            try: wire(connection, "roots")
            except OSError: pass
            else: raise AssertionError("Stopped service accepted a connection")
            restarted = call(c, "lan_sync_control", action="start")
            assert restarted["port"] == connection["port"] and restarted["sharedFolderCount"] == 2
            assert {r["rootId"] for r in wire(connection, "roots")["roots"]} == {r["rootId"] for r in roots}
            for folder in folders: call(c, "remove_folder", folderId=folder["id"])
            wait_folders(0)
            assert wire(connection, "roots")["roots"] == []
            assert "error" in wire(connection, "tree", rootId=first_root, path="")
            call(c, "lan_sync_control", action="revoke", deviceId=connection["deviceId"])
            assert "error" in wire(connection, "roots")
            proof = {"result": "PASS", "nativeAot": not d["dynamicCodeSupported"], "emptyLibraryPairsWithoutGrant": True, "samePairSeesNewFoldersAutomatically": True, "offlineIdentityRetained": True, "legacyUnshareRejected": True, "globalStopEnforced": True, "unregisteredNeighborsExcluded": True, "legacyIdentitylessGrantRevoked": a.legacy_grant}
            (base / "proof.json").write_text(json.dumps(proof, indent=2))
            print(json.dumps({**proof, "processId": pid, "evidence": str(base)}), flush=True)
            if a.interactive:
                call(c, "show_window")
                for line in sys.stdin:
                    cmd = line.strip()
                    if cmd == "quit": break
                    if cmd == "invite":
                        assert subprocess.check_output(["adb", "-s", a.serial, "emu", "avd", "name"]).splitlines()[0].strip().decode() == a.avd
                        value = call(c, "lan_sync_control", action="invite", host="10.0.2.2")["invitation"]
                        subprocess.run(["adb", "-s", a.serial, "shell", "input", "text", value], check=True, capture_output=True)
                        print("Invitation entered privately", flush=True)
                    elif cmd == "add":
                        folder = base / "Interactive music"; fixture.audio(folder / "Synthetic.wav")
                        call(c, "add_folder", path=str(folder)); wait_folders(1)
                        print("Registered isolated music folder; automatically shared", flush=True)
                    elif cmd == "remove":
                        for folder in call(c, "list_folders")["folders"]: call(c, "remove_folder", folderId=folder["id"])
                        wait_folders(0); print("Removed isolated registered folders", flush=True)
                    elif cmd == "stop": call(c, "lan_sync_control", action="stop"); print("Stopped", flush=True)
                    elif cmd == "start": call(c, "lan_sync_control", action="start"); print("Started", flush=True)
                    elif cmd == "revoke":
                        for device in call(c, "lan_sync_status")["devices"]:
                            call(c, "lan_sync_control", action="revoke", deviceId=device["id"])
                        print("Fixture devices revoked", flush=True)
                    elif cmd == "status":
                        status = call(c, "lan_sync_status")
                        print(json.dumps({k: status[k] for k in ("enabled", "libraryFolderCount", "sharedFolderCount")}), flush=True)
            call(c, "lan_sync_control", action="stop")
    finally:
        if pid: subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)


if __name__ == "__main__": main()
