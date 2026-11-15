"""Verify event-driven desktop recognition and restart through the real client/MCP.

Uses only generated silence and an existing verified local model. No start action,
model download, user library access, audio playback or Android installation.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import shutil
import time
import wave

from mcp_client import McpClient

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("startup", Path(__file__).with_name("test-mcp.py"))
startup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(startup)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    data = args.data.resolve()
    assert data.is_relative_to(ROOT / "desktop/artifacts") and data.name.endswith("fixture") and not data.exists()
    data.mkdir(parents=True)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    (data / "settings.json").write_text(json.dumps(dict(closeAction="Exit", autoUpdateCheck=False,
        analysisMode="Always", onlineLyrics=False, onlineCovers=False)), encoding="utf-8")
    model = data / "models/singing-language/ggml-tiny.bin"
    model.parent.mkdir(parents=True)
    shutil.copyfile(args.model, model)
    music = data / "music"
    music.mkdir()

    def make_song(path):
        with wave.open(str(path), "wb") as output:
            output.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
            output.writeframes(bytes(3 * 16000 * 2))

    for index in range(8):
        make_song(music / f"{index:02}.wav")
    report = dict(noPlayback=True, recognitionStartCalls=0)
    pid = None

    def wait(read, predicate, timeout=180):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            value = read()
            if predicate(value):
                return value
            time.sleep(.1)
        raise AssertionError(("Timed out", value))

    try:
        with McpClient(args.exe, data, timeout=60) as client:
            call = lambda tool, **kw: startup.call(client, tool, **kw)
            pid = call("get_diagnostics")["processId"]
            call("add_folder", path=str(music))
            read = lambda: call("singing_language", action="status", limit=1)
            partial = wait(read, lambda r: 0 < r["libraryCoverage"]["validCachedTracks"] < 8)
            assert partial["automation"]["enabled"] and partial["resumesAfterRestart"]
            report["automaticPartialCoverage"] = partial["libraryCoverage"]
            call("singing_language", action="cancel")
            held = wait(read, lambda r: r["state"] == "cancelled")
            assert held["automation"]["suppressedUntilRestart"]
            time.sleep(.75)
            assert read()["libraryCoverage"] == held["libraryCoverage"]
            report["cancelHoldsAutomaticWork"] = True
            call("checkpoint_client")
            startup.close_player(pid)
            pid = None

        with McpClient(args.exe, data, timeout=60) as client:
            call = lambda tool, **kw: startup.call(client, tool, **kw)
            pid = call("get_diagnostics")["processId"]
            report["nativeAot"] = not call("get_diagnostics")["dynamicCodeSupported"]
            read = lambda: call("singing_language", action="status", limit=24)
            completed = wait(read, lambda r: r["libraryCoverage"]["totalTracks"] == 8 and
                r["libraryCoverage"]["pendingTracks"] == 0 and r["state"] == "completed")
            assert not completed["automation"]["suppressedUntilRestart"]
            assert completed["failed"] == 0
            assert completed["total"] == 8 - held["libraryCoverage"]["validCachedTracks"]
            report["restartCoverage"] = completed["libraryCoverage"]
            report["restartSkippedCommittedTracks"] = True
            track_id = completed["tracks"]["items"][0]["trackId"]
            call("singing_language", action="set", trackIds=[track_id], value="CHINESE")
            make_song(music / "new.wav")
            call("rescan_library")
            changed = wait(read, lambda r: r["libraryCoverage"]["totalTracks"] == 9 and
                r["libraryCoverage"]["pendingTracks"] == 0)
            assert changed["libraryCoverage"]["manualTracks"] == 1
            report["newTrackAutomaticallyRecognized"] = True
            status = call("get_status")
            assert not status["playing"] and status["queueCount"] == 0
            report["finalCoverage"] = changed["libraryCoverage"]
            call("checkpoint_client")
            startup.close_player(pid)
            pid = None
    finally:
        if pid:
            startup.close_player(pid)
        args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report))


if __name__ == "__main__":
    main()
