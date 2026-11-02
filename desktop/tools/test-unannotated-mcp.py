"""Unannotated filter real MCP regression using generated silence and an isolated library only.

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
            for i in range(1, 246):
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
            call=lambda name,**args: startup.call(c,name,**args)
            diag=call("get_diagnostics");player=diag["processId"]
            call("show_window")
            catalog=c.request("tools/list")
            a.report.with_suffix(".catalog.json").write_text(json.dumps(catalog,indent=2),encoding="utf-8")
            assert len(catalog["tools"])==39
            def wait(tool,predicate,**args):
                return startup.wait_for(c,tool,predicate,**args)
            def search(state,**args):return call("search_tracks",annotation=state,**args)
            def view(total):return wait("song_filter",lambda x:x["ready"] and x["total"]==total,action="status",limit=1)
            def selection(expected):
                return wait("get_diagnostics",lambda x:not x["mainLayout"]["smartTagColumns"]["selectionAfterCorrection"]["computingRows"] and x["mainLayout"]["smartTagColumns"]["selectionAfterCorrection"]["selectedTrackIds"]==expected)
            initial=call("get_status")
            wait("search_tracks",lambda x:x["total"]==245,annotation="unannotated",limit=1)
            assert search("manual")["total"]==0
            assert search("unannotated",offset=200,limit=200)["total"]==245
            assert len(search("unannotated",offset=200,limit=200)["items"])==45
            # Missing model results are included too.
            assert any(x["id"]==3 for x in search("unannotated",limit=200)["items"])
            call("song_filter",action="set",annotation="unannotated",tags=[])
            view(245)
            for dimension,value in (("mood","SPORT"),("voice","VOCAL"),("genre","CLASSICAL"),("calmness","CALM")):
                call("set_smart_tags",trackIds=[220],**{dimension:value})
                wait("search_tracks",lambda x:x["total"]==244,annotation="unannotated",limit=1)
                assert search("manual")["total"]==1
                assert call("search_tracks",onlyManual=True)["items"][0]["id"]==220
                view(244);selection([221])
                call("set_smart_tags",trackIds=[220],**{dimension:"AUTO"})
                wait("search_tracks",lambda x:x["total"]==245,annotation="unannotated",limit=1)
                view(245)
            # Same automatic value must become manual, and partial AUTO is still manual.
            auto=call("explain_analysis",trackId=2)["automatic"]["VOICE"]
            assert auto=="VOCAL"
            call("set_smart_tags",trackIds=[2],voice=auto,genre="CLASSICAL")
            wait("search_tracks",lambda x:x["total"]==244,annotation="unannotated",limit=1)
            call("set_smart_tags",trackIds=[2],voice="AUTO")
            assert call("get_track",trackId=2)["tagSources"]["genre"]=="MANUAL"
            assert search("manual")["total"]==1
            call("set_smart_tags",trackIds=[2],genre="AUTO")
            wait("search_tracks",lambda x:x["total"]==245,annotation="unannotated",limit=1)
            # Full source/query/tag intersection; search never changes saved UI preference.
            all_vocal=search("all",query="Annotation",folderId=1,tags=["VOCAL"])["total"]
            assert all_vocal>200
            assert search("unannotated",query="Annotation",folderId=1,tags=["VOCAL"],offset=200)["total"]==all_vocal
            assert search("unannotated",folderId=999)["total"]==0
            assert search("unannotated",artistKey="missing")["total"]==0
            assert search("unannotated",albumKey="missing")["total"]==0
            for args in (dict(annotation="unannotated",onlyManual=True),dict(annotation="all",onlyManual=True),dict(annotation="bad"),dict(annotation="unannotated",tags=["VOCAL","INSTRUMENTAL"])):
                assert c.call("search_tracks",args).get("isError"),args
            assert c.call("song_filter",dict(action="status",annotation="manual")).get("isError")
            assert c.call("song_filter",dict(action="set")).get("isError")
            call("song_filter",action="set",annotation="manual");view(0)
            call("song_filter",action="set",annotation="unannotated");view(245)
            assert call("get_diagnostics")["songFilter"]["annotation"]=="unannotated"
            call("checkpoint_client")
            assert json.loads((data/"settings.json").read_text(encoding="utf-8-sig"))["tagFilter"]==["UNANNOTATED"]
            final=call("get_status")
            assert final["playing"]==initial["playing"] and final["queueCount"]==initial["queueCount"]
            report.update(fullLibrary245=True,fourDimensions=True,sameAutoValue=True,lastAutoWithdrawal=True,auditNotManual=True,unknownIncluded=True,sourceQueryTagPaging=True,nextSelection221=True,legacyAliasAndConflictRejection=True,persisted=True,playbackUnchanged=True,version=diag["version"],nativeAot=not diag["dynamicCodeSupported"])
            if a.keep_running:leave_running=True
        if not leave_running:
            startup.close_player(player);player=None
            with McpClient(a.exe,data) as c:
                player=startup.call(c,"get_diagnostics")["processId"]
                saved=startup.wait_for(c,"song_filter",lambda x:x["ready"] and x["total"]==245,action="status")
                assert saved["annotation"]=="unannotated"
                report["restartPersistence"]=True
    finally:
        if player and not leave_running:startup.close_player(player)
        report["processId"]=player
        a.report.write_text(json.dumps(report,indent=2),encoding="utf-8")
    print(json.dumps(report))

if __name__=="__main__":main()
