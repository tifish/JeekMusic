"""Real loopback TLS/MCP regression for the optional paired vocal-language metadata extension.

Uses a fresh private data directory and generated silence only. Never touches the
daily listener, downloads a model, plays audio, or writes user media.
"""
import argparse
import copy
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
    args = parser.parse_args()
    report = {}
    with tempfile.TemporaryDirectory(prefix="jeek-language-wire-") as temp:
        root = Path(temp)
        data, music = root / "data", root / "music"
        data.mkdir(); music.mkdir()
        (data / "settings.json").write_text(json.dumps(dict(closeAction="Exit", autoUpdateCheck=False, analysisMode="Manual")), encoding="utf-8")
        (data / "lan-sync.json").write_text(json.dumps(dict(enabled=False, bindAddress="127.0.0.1", roots={}, devices={})), encoding="utf-8")
        with wave.open(str(music / "fixture.wav"), "wb") as output:
            output.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
            output.writeframes(bytes(64000))
        with McpClient(args.exe, data, timeout=60) as client:
            pid = call(client, "get_diagnostics")["processId"]
            try:
                call(client, "add_folder", path=str(music.resolve()))
                tracks = wait(lambda: (lambda r: r["items"] if r["total"] == 1 else None)(call(client, "search_tracks", limit=10)))
                track_id = tracks[0]["id"]
                call(client, "singing_language", action="set", trackIds=[track_id], value="CHINESE")
                call(client, "lan_sync_control", action="start", port=0)
                status = wait(lambda: (lambda s: s if s["userDataSyncReady"] else None)(call(client, "lan_sync_status")))
                invitation = urlsplit(call(client, "lan_sync_control", action="invite", host="127.0.0.1")["invitation"])
                pin, secret = invitation.path.strip("/").split("/")
                peer = dict(port=status["port"], pin=pin, deviceId=uuid.uuid4().hex)
                wire = Wire(peer, auth=False)
                try:
                    wire.send(dict(op="pair", deviceId=peer["deviceId"], name="Language fixture", invitation=secret))
                    peer["token"] = wire.receive()["token"]
                finally:
                    wire.close()
                wire = Wire(peer)
                try:
                    state = dict(format=2, device=dict(id=peer["deviceId"], name="Language fixture"), written_at=0, playlists=[], deleted_playlists=[])

                    def exchange(extension=None):
                        nonlocal wire
                        payload = copy.deepcopy(state)
                        if extension is not None:
                            payload["vocal_languages"] = extension
                        result = wire.request("state", state=payload, includeFeatures=False)
                        for _ in range(4):
                            if result.get("code") != "busy":
                                break
                            # Preserve the production per-device rate/resource budget.
                            wire.close()
                            time.sleep(30)
                            wire = Wire(peer)
                            result = wire.request("state", state=payload, includeFeatures=False)
                        assert "error" not in result, result
                        assert result.get("features") is None
                        return result["state"]["vocal_languages"]

                    exported = exchange()
                    assert exported["version"] == 1 and len(exported["items"]) == 1
                    ref, rows = next(iter(exported["items"].items()))
                    original = rows[0]
                    assert original["source"] == "MANUAL" and original["languages"] == ["zh"]

                    def language():
                        return call(client, "singing_language", action="status", trackIds=[track_id])["tracks"]["items"][0]["language"]

                    changed = copy.deepcopy(exported)
                    record = changed["items"][ref][0]
                    record.update(languages=["en"], updated_at=original["updated_at"] + 1)
                    exchange(changed)
                    assert language()["languages"] == ["en"]
                    restored = copy.deepcopy(changed)
                    restored["items"][ref][0].update(state="auto", languages=[], updated_at=record["updated_at"] + 1)
                    exchange(restored)
                    assert language() is None
                    exchange(exported)
                    assert language() is None  # An older manual label cannot resurrect.
                    wrong = copy.deepcopy(changed)
                    wrong["items"][ref][0].update(size=original["size"] + 1, updated_at=record["updated_at"] + 2)
                    exchange(wrong)
                    assert language() is None
                    report.update(metadataOnly=True, roundTrip=True, manualOverride=True, restoreAutoTombstone=True,
                                  oldReplayIgnored=True, mismatchedRecordingIgnored=True, noPlayback=True)
                finally:
                    wire.close()
            finally:
                # The only PID used here came from this fresh isolated data directory.
                client.close()
                subprocess.run(["taskkill", "/pid", str(pid), "/f"], capture_output=True, check=False)
                subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
                                f"Get-Process -Id {int(pid)} -ErrorAction SilentlyContinue | Wait-Process -Timeout 10"], capture_output=True, check=False)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report))


if __name__ == "__main__":
    main()
