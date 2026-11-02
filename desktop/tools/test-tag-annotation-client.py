"""Targeted real MCP regression using generated silence and an isolated library only.

No audio output, network, user labels or training. --keep-running leaves the fixture
available for the small native visual/cell-interaction acceptance step.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import sqlite3
import time
import wave

from mcp_client import McpClient

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("startup_checks", Path(__file__).with_name("test-mcp.py"))
startup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(startup)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--exe", type=Path, required=True)
    p.add_argument("--data", type=Path, required=True)
    p.add_argument("--report", type=Path, required=True)
    p.add_argument("--language", choices=["zh-CN", "en"], default="zh-CN")
    p.add_argument("--keep-running", action="store_true")
    a = p.parse_args()
    data = a.data.resolve()
    assert data.is_relative_to(ROOT / "desktop/artifacts") and data.name.endswith("fixture")
    assert not data.exists(), "Use a fresh fixture; never overwrite an existing library"
    data.mkdir(parents=True)
    music = data / "music"
    music.mkdir()
    (data / "settings.json").write_text(json.dumps(dict(closeAction="Exit", autoUpdateCheck=False,
        analysisMode="Manual", onlineLyrics=False, onlineCovers=False, songSort="FileName",
        sortDescending=False, language=a.language)), encoding="utf-8")
    player = None
    leave_running = False
    report = {}
    try:
        with McpClient(a.exe, data) as c:
            player = startup.call(c, "get_diagnostics")["processId"]
            startup.close_player(player)
            player = None
        with sqlite3.connect(data / "library.db") as db:
            folder = str(music) + "\\"
            db.execute("INSERT INTO library_folder(id,volume_id,path_in_volume,last_root,display_name,available,added_at) VALUES(1,'path',?,?,'Annotation fixture',1,1)", (folder, folder))
            for i in range(1, 49):
                file = music / f"song{i:03}.wav"
                with wave.open(str(file), "wb") as w:
                    w.setnchannels(1); w.setsampwidth(2); w.setframerate(8000); w.writeframes(b"\0\0" * 800)
                ticks = file.stat().st_mtime_ns // 100 + 621355968000000000
                size = file.stat().st_size
                db.execute("""INSERT INTO track(id,track_key,folder_id,dir,file_name,title,artist_display,artist_inferred,artists,
                    duration_ms,has_lyrics,has_cover,mtime,size,sort_title,sort_file,sort_artist,genre)
                    VALUES(?,?,1,'',?,?,'Fixture artist',0,'Fixture artist',100,0,0,?,?,?,?,'fixture','')""",
                    (i, f"annotation{i}", file.name, f"Annotation {i:03}", ticks, size, f"annotation{i:03}", file.name))
                if i != 3:  # One deliberately unrecognized row alongside automatic and manual rows.
                    scores = dict(bpm_confidence=0, loudness=-70, loudness_range=0, onset_rate=0,
                        centroid_hz=0, low_ratio=0, vocal_score=0, classical_score=-1,
                        calm_level=0, energy_level=10, orchestra_level=0)
                    db.execute("INSERT INTO track_features VALUES(?, ?, 4, ?, 0, 1)",
                        (f"annotation{i}", f"{ticks}:{size}", json.dumps(scores)))
        with McpClient(a.exe, data) as c:
            call = lambda name, **args: startup.call(c, name, **args)
            diag = call("get_diagnostics"); player = diag["processId"]
            catalog = c.request("tools/list")
            a.report.with_suffix(".catalog.json").write_text(json.dumps(catalog, indent=2), encoding="utf-8")
            assert len(catalog["tools"]) == 39
            schema = next(t for t in catalog["tools"] if t["name"] == "get_diagnostics")["inputSchema"]
            assert schema["properties"] == {} and schema["additionalProperties"] is False
            for tag in ("Artist", "Album", "Duration", "FileName", "Modified", "Format"):
                call("song_columns", action="set", tag=tag, visible=False)
            for tag in ("Mood", "Voice", "Genre", "Calmness"):
                call("song_columns", action="set", tag=tag, visible=True, width=116)
            call("show_window")

            def columns():
                return call("get_diagnostics")["mainLayout"]["smartTagColumns"]

            def settled():
                deadline = time.monotonic() + 15
                while True:
                    d = columns(); selection = d["selectionAfterCorrection"]
                    if not selection["computingRows"] and not selection["pending"]:
                        return d
                    assert time.monotonic() < deadline, d
                    time.sleep(.05)

            settled()
            initial = call("get_status")
            assert not initial["playing"] and initial["queueCount"] == 0
            assert not any(cell["manualIndicatorVisible"] for cell in columns()["realizedCells"])
            automatic = call("explain_analysis", trackId=2)["automatic"]
            assert automatic["VOICE"] == "VOCAL"
            call("set_smart_tags", trackIds=[2], mood="SPORT", voice="VOCAL", genre="CLASSICAL", calmness="CALM")
            d = settled()
            cells = [cell for cell in d["realizedCells"] if cell["trackId"] == 2]
            assert len(cells) == 4 and all(cell["manualIndicatorVisible"] for cell in cells), d
            expected_help = "Manually annotated" if a.language == "en" else "手工标注"
            assert all(cell["helpText"] == expected_help for cell in cells)
            assert d["selectionAfterCorrection"]["selectedTrackIds"] == [3]
            report["sameValueManualFourIndicatorsAndNextSelection"] = True
            call("set_smart_tags", trackIds=[2], mood="AUTO", voice="AUTO", genre="AUTO", calmness="AUTO")
            d = settled()
            assert not any(cell["manualIndicatorVisible"] for cell in d["realizedCells"] if cell["trackId"] in (2, 3))
            assert call("get_track", trackId=2)["tagSources"]["voice"] == "AUTO"
            report["autoImmediatelyRemovesMarkersAndUnknownHasNone"] = True
            # Offscreen targets exercise real DataGrid virtualization and next/tail visibility.
            for targets, expected in [([25], 26), ([48], 47), ([32, 28], 29), ([1], 2)]:
                call("set_smart_tags", trackIds=targets, voice="VOCAL")
                d = settled()
                assert d["selectionAfterCorrection"]["selectedTrackIds"] == [expected], d
                deadline = time.monotonic() + 5
                while not d["selectionAfterCorrection"]["selectedFullyVisible"]:
                    assert time.monotonic() < deadline, d
                    time.sleep(.05); d = columns()
            assert not any(cell["manualIndicatorVisible"] for cell in columns()["realizedCells"] if cell["trackId"] == 3)
            report["firstMiddleTailBatchFirstAndVirtualizedVisibility"] = True
            # A failed batch never advances selection or partially changes the valid song.
            selected = columns()["selectionAfterCorrection"]["selectedTrackIds"]
            assert c.call("set_smart_tags", dict(trackIds=[2, 99999], voice="VOCAL"))["isError"]
            assert columns()["selectionAfterCorrection"]["selectedTrackIds"] == selected
            assert call("get_track", trackId=2)["tagSources"]["voice"] == "AUTO"
            report["invalidBatchDoesNotAdvance"] = True
            final = call("get_status")
            assert not final["playing"] and final["queueCount"] == 0
            report.update(version=diag["version"], dynamicCodeSupported=diag["dynamicCodeSupported"],
                framework=diag["frameworkDescription"], processPath=diag["processPath"],
                processId=player, language=a.language, toolCount=38, noPlayback=True)
            call("checkpoint_client")
            a.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
            print(json.dumps(report))
            leave_running = a.keep_running
            if not a.keep_running:
                startup.close_player(player); player = None
    finally:
        if player and not leave_running:
            import subprocess
            subprocess.run(["taskkill", "/PID", str(player), "/F"], capture_output=True,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))


if __name__ == "__main__":
    main()
