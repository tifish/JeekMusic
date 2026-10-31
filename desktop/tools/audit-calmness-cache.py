"""Compare two SQLite snapshots after a rules-only rollout; never read audio or write a DB.
Outputs aggregates/hashes, not song names or paths. A change is not an accuracy score.
Take consistent snapshots with sqlite3.Connection.backup before/after restarting the client.
"""
import argparse
from collections import Counter
from contextlib import closing
import hashlib
import json
from pathlib import Path
import sqlite3


def snapshot(path):
    with closing(sqlite3.connect(Path(path).resolve().as_uri() + "?mode=ro", uri=True)) as db:
        features = db.execute("SELECT * FROM track_features ORDER BY track_key").fetchall()
        overrides = db.execute("SELECT * FROM tag_override ORDER BY track_key, dimension").fetchall()
        tags = {(key, dim): (value, source) for key, dim, value, source in
                db.execute("SELECT track_key, dimension, value, source FROM track_tag")}
        keys = {r[0] for r in db.execute("SELECT track_key FROM track")}
    def digest(rows):
        return hashlib.sha256(json.dumps(rows, ensure_ascii=False, separators=(",", ":")).encode()).hexdigest()
    return dict(keys=keys, tags=tags, features=digest(features), overrides=digest(overrides))


def compare(before, after):
    keys = before["keys"] | after["keys"]
    def calm(s, key):
        return s["tags"].get((key, "CALMNESS"), (None, None))[0]
    changed = [k for k in keys if calm(before, k) != calm(after, k)]
    other = sum(before["tags"].get(k) != after["tags"].get(k)
                for k in before["tags"].keys() | after["tags"].keys() if k[1] != "CALMNESS")
    return dict(tracksBefore=len(before["keys"]), tracksAfter=len(after["keys"]),
        sameTracks=before["keys"] == after["keys"], featuresUnchanged=before["features"] == after["features"],
        overridesUnchanged=before["overrides"] == after["overrides"], otherDimensionChanges=other,
        calmnessBefore=dict(Counter(str(calm(before, k)) for k in before["keys"])),
        calmnessAfter=dict(Counter(str(calm(after, k)) for k in after["keys"])),
        transitions=dict(Counter(f"{calm(before, k)} -> {calm(after, k)}" for k in changed)),
        changedPreviousMood=dict(Counter(str(before["tags"].get((k, "MOOD"), (None, None))[0]) for k in changed)),
        featureHashes=[before["features"], after["features"]],
        limitation="Materialized results and change counts only; no inferred human labels or accuracy claim")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("before", type=Path)
    p.add_argument("after", type=Path)
    args = p.parse_args()
    result = compare(snapshot(args.before), snapshot(args.after))
    print(json.dumps(result, indent=2))
    return 0 if result["sameTracks"] and result["featuresUnchanged"] and result["overridesUnchanged"] and not result["otherDimensionChanges"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
