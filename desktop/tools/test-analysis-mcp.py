"""Real production EXE/MCP + decoder/model in an isolated synthetic library.
No user library, audio output, network request or native UI automation.
"""
import argparse
import json
import math
import struct
import subprocess
import tempfile
import time
import wave
from pathlib import Path
from mcp_client import McpClient, DEFAULT_EXE


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, default=DEFAULT_EXE)
    parser.add_argument("--diagnostics-only", action="store_true", help="Explicitly skip the automatic-background-analysis acceptance gate when desktop conditions defer it; still run full production diagnostic inference")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="jeek-analysis-v5-") as temp:
        root = Path(temp).resolve()
        assert root.parent == Path(tempfile.gettempdir()).resolve() and root.name.startswith("jeek-analysis-v5-")
        data = root / "data"
        data.mkdir()
        music = root / "controlled"
        music.mkdir()
        (data / "settings.json").write_text(json.dumps(dict(closeAction="Exit", autoUpdateCheck=False,
            analysisMode="Always", onlineLyrics=False, onlineCovers=False)), encoding="utf-8")
        with wave.open(str(music / "controlled.wav"), "wb") as audio:
            audio.setnchannels(1); audio.setsampwidth(2); audio.setframerate(8000)
            audio.writeframes(b"".join(struct.pack("<h", int(math.sin(i*2*math.pi*440/8000)*8192)) for i in range(8000*100)))
        pid = None
        try:
            with McpClient(args.exe, data) as client:
                def call(name, **arguments):
                    result = client.call(name, arguments)
                    assert not result.get("isError"), result
                    return result["structuredContent"]
                pid = call("get_diagnostics")["processId"]
                call("add_folder", path=str(music))
                deadline = time.monotonic() + (5 if args.diagnostics_only else 40)
                explanation = None
                while time.monotonic() < deadline:
                    tracks = call("search_tracks", limit=1)["items"]
                    if tracks:
                        explanation = call("explain_analysis", trackId=tracks[0]["id"])
                        if explanation["complete"]:
                            break
                    time.sleep(.2)
                assert explanation is not None
                # Fullscreen/power protections can legitimately defer automatic analysis.
                # Explicit read-only diagnostics are still exercised; never disable protection.
                if not args.diagnostics_only:
                    assert explanation["complete"] and not explanation["pendingReanalysis"], explanation
                background_verified = explanation["complete"]
                evidence = explanation["evidence"] if background_verified else None
                assert not call("get_status")["playing"]
                # Explicit, bounded production diagnostic API; no cache import or tag correction.
                for bad in [dict(action="start"), dict(action="status", trackId=tracks[0]["id"]),
                            dict(action="result"), dict(action="start", trackId=tracks[0]["id"], budgetSeconds=181)]:
                    assert client.call("diagnose_analysis", bad).get("isError"), bad
                before = explanation
                call("diagnose_analysis", action="start", trackId=tracks[0]["id"], budgetSeconds=15)
                deadline = time.monotonic() + 20
                while time.monotonic() < deadline:
                    diagnostic = call("diagnose_analysis", action="status")
                    if diagnostic["state"] != "running": break
                    time.sleep(.05)
                assert diagnostic["state"] == "completed", diagnostic
                summary = diagnostic["summary"]
                if evidence is None:
                    evidence = summary["productionEvidence"]
                assert evidence["contract"] == 5 and summary["complete"]
                assert len(evidence["segments"]) in (3, 4, 5)
                assert all(s["sample_count"] == 160000 and len(s["frames"]) == 10 for s in evidence["segments"])
                assert summary["segmentCount"] == len(evidence["segments"])
                assert "segments" not in summary and len(summary["classNames"]) == 521
                assert not summary["cacheCompatible"] and not summary["writesFeaturesOrTags"]
                for index in range(summary["segmentCount"]):
                    segment = call("diagnose_analysis", action="result", segmentIndex=index)["segment"]
                    assert len(segment["envelope"]) > 400 and len(segment["tempoCandidates"]) > 40
                    for window in segment["windows"]:
                        raw = window["scores521"]
                        assert len(raw) == 521
                        assert abs(sum(raw[k] for k in summary["energyIds"]) - window["energy"]) < 1e-6
                        assert abs(sum(raw[k] for k in summary["calmIds"]) - window["calm"]) < 1e-6
                assert call("explain_analysis", trackId=tracks[0]["id"]) == before
                assert call("diagnose_analysis", action="cancel")["state"] == "completed"
                assert "scores521" not in json.dumps(call("get_status"))
                print(json.dumps(dict(result="PASS", backgroundAnalysisVerified=background_verified, diagnosticsContract=summary["contract"], native=not call("get_diagnostics").get("dynamicCodeSupported", True),
                    segments=len(evidence["segments"]), frames=sum(len(s["frames"]) for s in evidence["segments"]),
                    elapsedMs=evidence["elapsed_ms"], reasons=evidence["additional_reasons"])))
        finally:
            if pid is not None:
                # Only this explicitly isolated fixture process. No window automation.
                subprocess.run(["powershell", "-NoProfile", "-Command",
                    f"$fixture = Get-Process -Id {int(pid)} -ErrorAction SilentlyContinue\n"
                    "if ($fixture) { Stop-Process -InputObject $fixture -Force; $fixture.WaitForExit() }"],
                    check=True, capture_output=True)

if __name__ == "__main__": main()
