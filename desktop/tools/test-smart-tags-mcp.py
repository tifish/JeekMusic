"""Real EXE/MCP smart-tag regression in a temporary offline library; never plays audio.

Run against both the daily build and NativeAOT package. No user data is touched.
"""
import argparse
import json
from pathlib import Path
import sqlite3
import tempfile
import time
from contextlib import closing

from mcp_client import DEFAULT_EXE, McpClient
from importlib import util

spec = util.spec_from_file_location("startup_checks", Path(__file__).with_name("test-mcp.py"))
startup = util.module_from_spec(spec)
spec.loader.exec_module(startup)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, default=DEFAULT_EXE)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="jeekmusic-smart-tags-") as temp:
        data = Path(temp)
        (data / "settings.json").write_text(json.dumps({
            "closeAction": "Exit", "autoUpdateCheck": False, "analysisMode": "Manual",
            "onlineLyrics": False, "onlineCovers": False,
        }), encoding="utf-8")
        player_pid = None
        try:
            # Let the real application create/migrate its schema, then seed only
            # this isolated fixture while its client is stopped.
            with McpClient(args.exe, data) as client:
                player_pid = startup.call(client, "get_diagnostics")["processId"]
                startup.close_player(player_pid)
                player_pid = None
            with closing(sqlite3.connect(data / "library.db")) as db, db:
                db.execute("INSERT INTO library_folder VALUES(1,'offline-fixture','\\offline-tags\\','Z:\\offline-tags\\','Offline tags',0,1)")
                for i in (1, 2):
                    db.execute("""INSERT INTO track(id,track_key,folder_id,dir,file_name,title,artist_display,artist_inferred,artists,
                        duration_ms,has_lyrics,has_cover,mtime,size,sort_title,sort_file,sort_artist,genre)
                        VALUES(?,?,1,'',?,?,'',0,'',120000,0,0,42,1000,?,?,'','file genre')""",
                        (i, f"offline{i}", f"song{i}.flac", f"Synthetic song {i}", f"song{i}", f"song{i}.flac"))
                    scores = json.dumps(dict(bpm_confidence=0, loudness=-70, loudness_range=0, onset_rate=0,
                        centroid_hz=0, low_ratio=0, vocal_score=0, classical_score=-1, calm_level=0, energy_level=10, orchestra_level=0))
                    db.execute("INSERT INTO track_features VALUES(?, '42:1000', 4, ?, 0, 1)", (f"offline{i}", scores))
            with McpClient(args.exe, data) as client:
                diagnostic = startup.call(client, "get_diagnostics")
                player_pid = diagnostic["processId"]
                catalog = client.request("tools/list")["tools"]
                assert len(catalog) == 22
                tool = next(t for t in catalog if t["name"] == "set_smart_tags")
                assert tool["inputSchema"]["minProperties"] == 2
                # Offline songs remain addressable by ID but are deliberately
                # excluded from the browsing trackCount by LibrarySnapshot.
                assert startup.call(client, "get_status")["trackCount"] == 0
                assert startup.call(client, "get_track", trackId=1)["title"] == "Synthetic song 1"
                result = startup.call(client, "set_smart_tags", trackIds=[1, 2, 1], voice="VOCAL", genre="CLASSICAL")
                assert result["count"] == 2
                assert all(t["tagSources"]["voice"] == "MANUAL" and t["tags"]["voice"] == "VOCAL" for t in result["items"])
                assert all(t["path"] is None for t in result["items"])
                with closing(sqlite3.connect(data / "library.db")) as db, db:
                    before = db.execute("SELECT * FROM tag_override ORDER BY track_key,dimension").fetchall()
                    tags_before = db.execute("SELECT * FROM track_tag ORDER BY track_key,dimension").fetchall()
                    for arguments in [dict(trackIds=[1]), dict(trackIds=[1, 999], mood="SPORT"),
                        dict(trackIds=[1], mood="VOCAL"), dict(trackIds=[1], voice=None),
                        dict(trackIds=[1] * 1001, voice="AUTO")]:
                        assert client.call("set_smart_tags", arguments)["isError"]
                    assert db.execute("SELECT * FROM tag_override ORDER BY track_key,dimension").fetchall() == before
                    db.execute("CREATE TRIGGER fail_result BEFORE INSERT ON track_tag WHEN NEW.dimension='GENRE' BEGIN SELECT RAISE(ABORT,'fixture result failure'); END")
                    db.commit()
                    try:
                        failure = client.call("set_smart_tags", dict(trackIds=[1, 2], mood="SPORT", voice="INSTRUMENTAL"))
                        assert failure.get("isError"), failure
                    except RuntimeError as error:
                        assert "Internal error" in str(error), error
                    assert db.execute("SELECT * FROM tag_override ORDER BY track_key,dimension").fetchall() == before
                    assert db.execute("SELECT * FROM track_tag ORDER BY track_key,dimension").fetchall() == tags_before
                    db.execute("DROP TRIGGER fail_result")
                    future = int(time.time() * 1000) + 60000
                    db.execute("UPDATE tag_override SET value='INSTRUMENTAL',updated_at=? WHERE track_key='offline1' AND dimension='VOICE'", (future,))
                    db.commit()
                restored = startup.call(client, "set_smart_tags", trackIds=[1], voice="AUTO")
                assert restored["items"][0]["tags"]["voice"] == "VOCAL"
                assert restored["items"][0]["tagSources"]["voice"] == "AUTO"
                detail = startup.call(client, "get_track", trackId=1)
                assert detail["tagSources"]["voice"] == "AUTO" and detail["tags"]["genre"] == "CLASSICAL"
                with closing(sqlite3.connect(data / "library.db")) as db, db:
                    reset = db.execute("SELECT value,updated_at FROM tag_override WHERE track_key='offline1' AND dimension='VOICE'").fetchone()
                    assert reset[0] is None and reset[1] > future
                    assert db.execute("SELECT * FROM tag_override WHERE NOT(track_key='offline1' AND dimension='VOICE') ORDER BY track_key,dimension").fetchall() == [
                        row for row in before if not (row[0] == 'offline1' and row[1] == 'VOICE')]
                    assert db.execute("SELECT DISTINCT genre FROM track").fetchall() == [('file genre',)]
                startup.close_player(player_pid)
                player_pid = None
            print("PASS: 22 tools, real async MCP, offline single/batch, same-value manual, AUTO/null sync record, omitted timestamps, validation and computed-write rollback; no audio")
        except Exception:
            with closing(sqlite3.connect(data / "library.db")) as db:
                print("Fixture database:", db.execute("SELECT id,title FROM track").fetchall(), flush=True)
            log = data / "logs" / "app.log"
            if log.exists():
                print(log.read_text(encoding="utf-8-sig")[-8000:], flush=True)
            raise
        finally:
            if player_pid is not None:
                import subprocess
                subprocess.run(["taskkill", "/PID", str(player_pid), "/F"], capture_output=True, timeout=10)
            time.sleep(.5)  # Windows releases SQLite/log handles after process exit.


if __name__ == "__main__":
    main()
