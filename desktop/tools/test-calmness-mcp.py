"""Rules v7 real-client contract with isolated synthetic files and cached v5 evidence.
No inference, playback, network or user-library writes. --data retains fixtures for native UI QA.
"""
import argparse
import importlib.util
import json
import sqlite3
import tempfile
import time
import wave
from pathlib import Path
from contextlib import closing
from mcp_client import McpClient, DEFAULT_EXE

spec = importlib.util.spec_from_file_location("startup", Path(__file__).with_name("test-mcp.py"))
startup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(startup)


def run(exe, root):
    root.mkdir(parents=True, exist_ok=True)
    data, music = root / "data", root / "music"
    data.mkdir(); music.mkdir()
    (data / "settings.json").write_text(json.dumps(dict(closeAction="Exit", autoUpdateCheck=False,
        analysisMode="Manual", onlineLyrics=False, onlineCovers=False, language="zh-CN")), encoding="utf-8")
    for name in ("Quiet fixture", "Active fixture", "Failed fixture"):
        with wave.open(str(music / (name + ".wav")), "wb") as f:
            f.setnchannels(1); f.setsampwidth(2); f.setframerate(16000); f.writeframes(bytes(32000 * 3))
    pid = None
    try:
        with McpClient(exe, data) as c:
            pid = startup.call(c, "get_diagnostics")["processId"]
            startup.call(c, "add_folder", path=str(music.resolve()))
            for _ in range(100):
                found = startup.call(c, "search_tracks")["items"]
                if len(found) == 3: break
                time.sleep(.1)
            assert len(found) == 3
            startup.close_player(pid); pid = None
        with closing(sqlite3.connect(data / "library.db")) as db, db:
            records = db.execute("SELECT id,track_key,title,mtime,size FROM track").fetchall()
            ids = {r[2].split()[0]: r[0] for r in records}
            for tid, key, title, mtime, size in records:
                quiet, failed = title.startswith("Quiet"), title.startswith("Failed")
                m = dict(vocal=-3, classical=-1, calm=.015 if quiet else .02, energy=.005 if quiet else .12, orchestra=0)
                evidence = dict(contract=5, feature_complete=not failed, segments=[dict(start_ms=0, end_ms=2925,
                    additional=False, sample_count=46800, decode_attempts=1, model_attempts=1, mean=m, frames=[m]*3)],
                    additional_reasons=[], elapsed_ms=1)
                features = dict(bpm_confidence=0, loudness=-20, loudness_range=0, onset_rate=0, centroid_hz=0, low_ratio=0,
                    vocal_score=m["vocal"], classical_score=m["classical"], calm_level=m["calm"], energy_level=m["energy"], orchestra_level=0,
                    analysis_evidence=json.dumps(evidence), analysis_attempts=3 if failed else 0)
                db.execute("INSERT OR REPLACE INTO track_features VALUES(?,?,?,?,?,1)", (key, f"{mtime}:{size}", 5, json.dumps(features), int(failed)))
                if quiet: db.execute("INSERT INTO tag_override VALUES(?, 'MOOD', 'QUIET', 123)", (key,))
            before = db.execute("SELECT * FROM track_features ORDER BY track_key").fetchall()
        with McpClient(exe, data) as c:
            diag = startup.call(c, "get_diagnostics"); pid = diag["processId"]
            for _ in range(100):
                quiet = startup.call(c, "get_track", trackId=ids["Quiet"])
                if quiet["tags"] and quiet["tags"].get("calmness") == "CALM": break
                time.sleep(.1)
            assert quiet["tags"]["calmness"] == "CALM" and quiet["tags"]["mood"] == "NON_SPORT"
            explanation = startup.call(c, "explain_analysis", trackId=ids["Quiet"])
            assert explanation["rulesVersion"] == 8 and explanation["analyzerVersion"] == 6 and not explanation["pendingReanalysis"]
            assert explanation["workout"]["baseEvidenceReused"] is True
            assert explanation["evidence"]["rhythm_state"] == "not_needed"
            assert explanation["calmness"]["support"] == 1
            assert explanation["calmness"]["strictSupport"] == 0
            assert explanation["calmness"]["relativeSupport"] == 1
            assert explanation["calmness"]["supportDefinition"] == "strict_or_relative_per_window"
            assert explanation["calmness"]["segments"][0]["support"] == 1
            assert explanation["calmness"]["thresholds"]["relativeEnergyToCalmCeiling"] == .5
            assert explanation["acousticFeatures"]["usedForCalmness"] is False
            for _ in range(100):
                filtered = startup.call(c, "search_tracks", tags=["CALM", "NON_SPORT"])
                if filtered["total"] == 1: break
                time.sleep(.1)
            assert filtered["total"] == 1, filtered
            assert startup.call(c, "search_tracks", tags=["CALM", "SPORT"])["total"] == 0
            assert startup.call(c, "search_tracks", tags=["NON_CALM"])["total"] == 1
            assert startup.call(c, "search_tracks", tags=[])["total"] == 3
            assert c.call("search_tracks", {"tags": ["CALM", "NON_CALM"]}).get("isError")
            failed = startup.call(c, "explain_analysis", trackId=ids["Failed"])
            assert failed["automatic"]["CALMNESS"] is None and failed["pendingReanalysis"]
            result = startup.call(c, "set_smart_tags", trackIds=[ids["Quiet"]], calmness="NON_CALM", reason="synthetic contract")
            assert result["items"][0]["tagSources"]["calmness"] == "MANUAL"
            assert c.call("set_smart_tags", {"trackIds": [ids["Quiet"]], "calmness": "QUIET"}).get("isError")
            with closing(sqlite3.connect(data / "library.db")) as db, db:
                db.execute("CREATE TRIGGER fail_calm BEFORE INSERT ON track_tag WHEN NEW.dimension='CALMNESS' BEGIN SELECT RAISE(ABORT,'fixture'); END")
            try:
                assert c.call("set_smart_tags", {"trackIds": [ids["Quiet"]], "calmness": "AUTO"}).get("isError")
            except RuntimeError as error:
                assert "Internal error" in str(error)
            assert startup.call(c, "get_track", trackId=ids["Quiet"])["tags"]["calmness"] == "NON_CALM"
            with closing(sqlite3.connect(data / "library.db")) as db, db:
                db.execute("DROP TRIGGER fail_calm")
            result = startup.call(c, "set_smart_tags", trackIds=[ids["Quiet"]], calmness="AUTO")
            assert result["items"][0]["tags"]["calmness"] == "CALM"
            with closing(sqlite3.connect(data / "library.db")) as db:
                after = db.execute("SELECT * FROM track_features ORDER BY track_key").fetchall()
                for old,new in zip(before,after):
                    if old[4]:
                        assert old == new  # terminal old failure remains available for an explicit v6 retry
                        continue
                    assert old[:2] == new[:2] and new[2] == 6
                    a,b = json.loads(old[3]),json.loads(new[3])
                    assert all(b[k] == v for k,v in a.items() if k != "analysis_evidence")
                    e0,e1 = json.loads(a["analysis_evidence"]),json.loads(b["analysis_evidence"])
                    assert e1["base_evidence_reused"] and e1["rhythm_state"] == "not_needed"
                    assert all(all(s1[k] == v for k,v in s0.items()) for s0,s1 in zip(e0["segments"],e1["segments"]))
                assert db.execute("SELECT value,updated_at FROM tag_override WHERE dimension='MOOD'").fetchall() == [("QUIET", 123)]
            assert not startup.call(c, "get_status")["playing"]
            (root / "catalog.json").write_text(json.dumps(c.request("tools/list"), ensure_ascii=False, indent=2), encoding="utf-8")
            print(json.dumps(dict(result="PASS", data=str(data.resolve()), ids=ids, runtime=diag.get("runtime")), ensure_ascii=False))
            startup.close_player(pid); pid = None
    finally:
        if pid is not None: startup.close_player(pid)


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--exe", type=Path, default=DEFAULT_EXE)
    p.add_argument("--data", type=Path, help="New fixture parent directory to retain for native UI QA")
    args = p.parse_args()
    if args.data: run(args.exe, args.data)
    else:
        with tempfile.TemporaryDirectory(prefix="jeek-calmness-") as temp: run(args.exe, Path(temp))
