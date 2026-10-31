"""Real EXE/MCP v6 upgrade, commit, rollback and cancel in an isolated synthetic library.

The v5 model evidence is a declared synthetic fixture. New pulse inference and
force=true model inference are real. This verifies contracts, not music accuracy.
Never plays audio, imports user music, changes source metadata or accesses network.
"""
import argparse
from array import array
from contextlib import closing
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import sqlite3
import tempfile
import time
import wave

from mcp_client import McpClient, DEFAULT_EXE

spec = importlib.util.spec_from_file_location("startup", Path(__file__).with_name("test-mcp.py"))
startup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(startup)


def finished(client):
    deadline = time.monotonic()+65
    while time.monotonic() < deadline:
        status = startup.call(client,"analyze_tracks",action="status")
        if status["state"] not in ("running","cancelling"):
            return status
        time.sleep(.05)
    raise AssertionError("Bounded analysis batch did not finish")


def run(exe, root):
    data, music = root/"data", root/"music"
    data.mkdir(); music.mkdir()
    (data/"settings.json").write_text(json.dumps(dict(closeAction="Exit",autoUpdateCheck=False,
        analysisMode="Manual",sensitivity="LOOSE",onlineLyrics=False,onlineCovers=False)),encoding="utf-8")
    pcm = array("h")
    state = 12345
    for i in range(16000*100):
        state ^= (state << 13) & 0xffffffff; state ^= state >> 17; state ^= (state << 5) & 0xffffffff
        phase = (i/16000+.1) % .5
        sample = (state/4294967295*2-1)*.4*math.exp(-phase/.025)+math.sin(2*math.pi*80*phase)*.6*math.exp(-phase/.07)
        pcm.append(round(sample*30000))
    audio = music/"controlled.wav"
    with wave.open(str(audio),"wb") as f:
        f.setnchannels(1); f.setsampwidth(2); f.setframerate(16000); f.writeframes(pcm.tobytes())
    audio_hash = hashlib.sha256(audio.read_bytes()).hexdigest()
    pid = None
    try:
        with McpClient(exe,data) as c:
            pid = startup.call(c,"get_diagnostics")["processId"]
            startup.call(c,"add_folder",path=str(music.resolve()))
            found = startup.wait_for(c,"search_tracks",lambda r:r["total"] == 1)["items"][0]
            startup.close_player(pid); pid = None
        fixture = json.loads((Path(__file__).resolve().parents[2]/"shared/testcases/workout-v8.json").read_text(encoding="utf-8"))["cases"][0]["evidence"]
        fixture.update(contract=5,rhythm_contract=0)
        fixture.pop("rhythm_state")
        for s in fixture["segments"]: s.pop("pulse")
        mean = fixture["segments"][0]["mean"]
        features = dict(bpm_confidence=0,loudness=-20,loudness_range=0,onset_rate=0,centroid_hz=0,low_ratio=0,
            vocal_score=mean["vocal"],classical_score=mean["classical"],calm_level=mean["calm"],energy_level=mean["energy"],
            orchestra_level=0,analysis_evidence=json.dumps(fixture),analysis_attempts=1)
        with closing(sqlite3.connect(data/"library.db")) as db, db:
            tid,key,mtime,size = db.execute("SELECT id,track_key,mtime,size FROM track").fetchone()
            db.execute("INSERT OR REPLACE INTO track_features VALUES(?,?,?,?,0,1)",(key,f"{mtime}:{size}",5,json.dumps(features)))
        with McpClient(exe,data) as c:
            pid = startup.call(c,"get_diagnostics")["processId"]
            assert len(c.request("tools/list")["tools"]) == 36
            startup.wait_for(c,"get_track",lambda r:(r.get("tags") or {}).get("mood") == "NON_SPORT",trackId=tid)
            before = startup.call(c,"explain_analysis",trackId=tid)
            assert before["analyzerVersion"] == 5 and before["pendingReanalysis"]
            playback = startup.call(c,"get_status"); queue = startup.call(c,"get_queue")
            for bad in [dict(action="start"),dict(action="start",trackIds=[]),dict(action="start",trackIds=[tid,tid]),
                        dict(action="start",trackIds=[tid],budgetSeconds=181),dict(action="status",force=True),
                        dict(action="start",trackIds=[999999])]:
                assert c.call("analyze_tracks",bad).get("isError"), bad
            startup.call(c,"analyze_tracks",action="start",trackIds=[tid],budgetSeconds=60)
            assert c.call("analyze_tracks",dict(action="start",trackIds=[tid])).get("isError")
            status = finished(c)
            assert status["state"] == "completed" and status["results"][0]["state"] == "committed",status
            explanation = startup.call(c,"explain_analysis",trackId=tid)
            assert explanation["analyzerVersion"] == 6 and explanation["rulesVersion"] == 8 and explanation["complete"]
            assert explanation["automatic"]["MOOD"] == "SPORT" and explanation["sources"]["MOOD"] == "AUTO"
            assert explanation["workout"]["method"] == "model_and_music_pulse" and explanation["workout"]["baseEvidenceReused"]
            assert explanation["workout"]["supportedIndependentSegments"] == 2
            assert [s["frames"] for s in explanation["evidence"]["segments"]] == [s["frames"] for s in fixture["segments"]]
            after_playback = startup.call(c,"get_status")
            # Materialized tag changes deliberately publish a new library snapshot.
            assert {k:v for k,v in after_playback.items() if k != "snapshotVersion"} == {k:v for k,v in playback.items() if k != "snapshotVersion"}
            assert startup.call(c,"get_queue") == queue
            startup.call(c,"analyze_tracks",action="start",trackIds=[tid],budgetSeconds=60)
            assert finished(c)["results"][0]["state"] == "already_current"
            # Force real ONNX inference, then fail the materialized-tag write.
            # The feature row must roll back with that failure, including its timestamp.
            with closing(sqlite3.connect(data/"library.db")) as db, db:
                db.execute("DELETE FROM track_tag WHERE dimension='GENRE'")
                before_features = db.execute("SELECT * FROM track_features").fetchall()
                before_tags = db.execute("SELECT * FROM track_tag ORDER BY dimension").fetchall()
                db.execute("CREATE TRIGGER fail_auto BEFORE INSERT ON track_tag BEGIN SELECT RAISE(ABORT,'fixture'); END")
            startup.call(c,"analyze_tracks",action="start",trackIds=[tid],force=True,budgetSeconds=60)
            failure = finished(c)
            assert failure["results"][0]["error"] == "SqliteException",failure
            with closing(sqlite3.connect(data/"library.db")) as db, db:
                assert before_features == db.execute("SELECT * FROM track_features").fetchall()
                assert before_tags == db.execute("SELECT * FROM track_tag ORDER BY dimension").fetchall()
                assert db.execute("SELECT COUNT(*) FROM tag_override").fetchone()[0] == 0
                db.execute("DROP TRIGGER fail_auto")
            startup.call(c,"analyze_tracks",action="start",trackIds=[tid],force=True,budgetSeconds=60)
            startup.call(c,"analyze_tracks",action="cancel")
            cancelled = finished(c)
            assert cancelled["state"] == "cancelled",cancelled
            with closing(sqlite3.connect(data/"library.db")) as db:
                assert before_features == db.execute("SELECT * FROM track_features").fetchall()
            assert hashlib.sha256(audio.read_bytes()).hexdigest() == audio_hash
            assert not startup.call(c,"get_status")["playing"]
            print(json.dumps(dict(result="PASS",native=not startup.call(c,"get_diagnostics")["dynamicCodeSupported"],
                reusedV5=True,pulseSegments=2,realForcedInference=True,atomicRollback=True,cancelVerified=True,tools=28)))
            startup.close_player(pid); pid = None
    finally:
        if pid is not None: startup.close_player(pid)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe",type=Path,default=DEFAULT_EXE)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="jeek-workout-v6-") as temp:
        run(args.exe,Path(temp))
