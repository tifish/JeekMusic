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
                deadline = time.monotonic() + 40
                explanation = None
                while time.monotonic() < deadline:
                    tracks = call("search_tracks", limit=1)["items"]
                    if tracks:
                        explanation = call("explain_analysis", trackId=tracks[0]["id"])
                        if explanation["complete"]:
                            break
                    time.sleep(.2)
                assert explanation and explanation["complete"], explanation
                evidence = explanation["evidence"]
                assert evidence["contract"] == 5
                assert len(evidence["segments"]) in (3, 4, 5)
                assert all(s["sample_count"] == 160000 and len(s["frames"]) == 10 for s in evidence["segments"])
                assert not explanation["pendingReanalysis"]
                assert not call("get_status")["playing"]
                print(json.dumps(dict(result="PASS", native=not call("get_diagnostics").get("dynamicCodeSupported", True),
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
