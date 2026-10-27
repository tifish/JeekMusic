"""Verify production resource-batch MCP with isolated offline tracks and cached lyrics.

No network sources, audio playback, user library, settings migration or media writes.
Use --exe to run the same checks against a NativeAOT publish directory.
"""
import argparse
from contextlib import closing
from importlib import util
import json
from pathlib import Path
import sqlite3
import subprocess
import tempfile
import time

from mcp_client import DEFAULT_EXE, McpClient

spec = util.spec_from_file_location("startup_checks", Path(__file__).with_name("test-mcp.py"))
startup = util.module_from_spec(spec)
spec.loader.exec_module(startup)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, default=DEFAULT_EXE)
    args = parser.parse_args()
    player_pid = None
    with tempfile.TemporaryDirectory(prefix="jeekmusic-resource-batch-") as temp:
        data = Path(temp)
        (data / "settings.json").write_text(json.dumps({
            "closeAction": "Exit", "autoUpdateCheck": False, "analysisMode": "Manual",
            "onlineLyrics": True, "onlineCovers": False, "lyricsFetchMode": "Manual",
            "coverFetchMode": "Manual", "lyricsSaveLocation": "AppCache",
            "lyricsSources": [], "coverSources": [],
        }), encoding="utf-8")
        try:
            with McpClient(args.exe, data) as client:
                player_pid = startup.call(client, "get_diagnostics")["processId"]
                startup.close_player(player_pid)
                player_pid = None
            with closing(sqlite3.connect(data / "library.db")) as db, db:
                db.execute("INSERT INTO library_folder(id,volume_id,path_in_volume,last_root,display_name,available,added_at) VALUES(1,'offline-fixture','\\offline-resources\\','Z:\\offline-resources\\','Offline resources',0,1)")
                for i in (1, 2):
                    db.execute("""INSERT INTO track(id,track_key,folder_id,dir,file_name,title,artist_display,artist_inferred,artists,
                        duration_ms,has_lyrics,has_cover,mtime,size,sort_title,sort_file,sort_artist)
                        VALUES(?,?,1,'',?,?,'',0,'',120000,0,0,42,1000,?,?,'')""",
                        (i, f"offline{i}", f"song{i}.flac", f"Synthetic song {i}", f"song{i}", f"song{i}.flac"))
                    db.execute("INSERT INTO lyrics_cache VALUES(?, 'fixture', 1, '[00:00.00]Local fixture lyrics', 1)", (f"offline{i}",))
                before = db.execute("SELECT * FROM lyrics_cache ORDER BY track_key").fetchall()
            settings_before = json.loads((data / "settings.json").read_text(encoding="utf-8-sig"))
            with McpClient(args.exe, data) as client:
                diagnostic = startup.call(client, "get_diagnostics")
                player_pid = diagnostic["processId"]
                startup.wait_for(client, "get_diagnostics", lambda d: d["resourceBatch"]["state"] != "unavailable")
                catalog = client.request("tools/list")["tools"]
                assert len(catalog) == 25
                tool = next(t for t in catalog if t["name"] == "fetch_resources")
                assert tool["inputSchema"]["additionalProperties"] is False
                assert tool["annotations"]["openWorldHint"] and tool["annotations"]["destructiveHint"]
                assert startup.call(client, "fetch_resources", action="status")["state"] == "idle"
                invalid = [dict(action="start", kind="lyrics", scope="current_list"),
                    dict(action="start", kind="lyrics", scope="track_ids", trackIds=[1, 999]),
                    dict(action="start", kind="covers", scope="track_ids", trackIds=[1]),
                    dict(action="start", kind="lyrics", scope="current_list", trackIds=[1]),
                    dict(action="status", kind="lyrics"), dict(action="cancel", scope="current_list"),
                    dict(action="start", kind="lyrics", scope="track_ids", trackIds=[]),
                    dict(action="start", kind="lyrics", scope="track_ids", trackIds=[1] * 1001),
                    dict(action="start", kind="lyrics", scope="library"), dict(action="start", force=True)]
                for request in invalid:
                    assert client.call("fetch_resources", request)["isError"], request
                initial = startup.call(client, "fetch_resources", action="start", kind="lyrics", scope="track_ids", trackIds=[1, 2, 1])
                assert initial["targetSongCount"] == 3 and initial["scope"] == "track_ids"
                state = startup.wait_for(client, "fetch_resources", lambda s: not s["busy"], action="status")
                assert state["state"] == "completed", state
                assert state["totalUnits"] == state["processedUnits"] == 2
                assert state["savedUnits"] == state["failedUnits"] == 0
                assert startup.call(client, "get_diagnostics")["resourceBatch"]["state"] == "completed"
                assert startup.call(client, "fetch_resources", action="cancel")["state"] == "completed"
                playback = startup.call(client, "get_status")
                assert not playback["playing"] and playback["queueCount"] == 0
                with closing(sqlite3.connect(data / "library.db")) as db:
                    assert db.execute("SELECT * FROM lyrics_cache ORDER BY track_key").fetchall() == before
                settings_after = json.loads((data / "settings.json").read_text(encoding="utf-8-sig"))
                for key in ("onlineLyrics", "onlineCovers", "lyricsSaveLocation", "coverSaveMode", "lyricsSources", "coverSources"):
                    assert settings_before.get(key) == settings_after.get(key), key
                startup.close_player(player_pid)
                player_pid = None
            print("PASS: 25 production tools, frozen explicit batch, duplicate tracks, cached-lyrics skip, status/cancel, errors/empty/disabled-online validation, unchanged save settings/cache/queue; no network or playback")
        finally:
            if player_pid is not None:
                subprocess.run(["taskkill", "/PID", str(player_pid), "/F"], capture_output=True, timeout=10)
            time.sleep(.5)


if __name__ == "__main__":
    main()
