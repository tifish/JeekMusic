"""Frozen-prediction grouped evaluation. No label inference or threshold fitting.

Input JSONL: song_id, album_id, artist_ids, labels {VOICE/MOOD/GENRE/CALMNESS: enum or ?},
automatic {dimension: enum}, provenance {labels, scores, independent: bool}.
Only independently reviewed human labels with production_v5 score provenance may enter
calibration/test. Existing calibration labels and title-generated labels stay regression.
Connected components across song/album/artist prevent transitive leakage; unknowns remain unknown.
"""
import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path

VALUES = {"VOICE": ("VOCAL", "INSTRUMENTAL"), "MOOD": ("SPORT", "NON_SPORT"), "GENRE": ("CLASSICAL", "NON_CLASSICAL"), "CALMNESS": ("CALM", "NON_CALM")}
UNKNOWN = (None, "", "?", "UNKNOWN")


def evaluate(records, seed="jeek-v5-frozen-1", group_by=("song", "album", "artist")):
    if set(group_by) - {"song", "album", "artist"}:
        raise ValueError("Unknown grouping dimension")
    if "song" not in group_by:
        raise ValueError("song grouping is mandatory")
    parent = list(range(len(records)))
    def root(i):
        while i != parent[i]:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i
    seen = {}
    row_tokens = []
    for i, row in enumerate(records):
        if not row.get("song_id") or not row.get("provenance"):
            raise ValueError("song_id and provenance are required; never fabricate identity or labels")
        if not isinstance(row.get("artist_ids", []), list):
            raise ValueError("artist_ids must be a list")
        for dim, allowed in VALUES.items():
            if row.get("labels", {}).get(dim) not in (*allowed, *UNKNOWN):
                raise ValueError(f"invalid/ambiguous label in {dim}")
        tokens = [("song", row["song_id"])]
        if "album" in group_by and row.get("album_id"):
            tokens.append(("album", row["album_id"]))
        if "artist" in group_by:
            tokens.extend(("artist", a) for a in row.get("artist_ids", []) if a)
        row_tokens.append(tokens)
        for token in tokens:
            if token in seen:
                parent[root(i)] = root(seen[token])
            else:
                seen[token] = i
    components = {}
    for i in range(len(records)):
        components.setdefault(root(i), []).append(i)
    assignments = {}
    for indices in components.values():
        tokens = sorted(set(t for i in indices for t in row_tokens[i]))
        digest = hashlib.sha256((seed + json.dumps(tokens, ensure_ascii=False)).encode()).hexdigest()
        # A component touching reused/weak labels is entirely regression.
        eligible = all(records[i]["provenance"].get("independent") is True and
            records[i]["provenance"].get("used_for_tuning") is not True and
            records[i]["provenance"].get("labels") == "human_reviewed" and
            "production_v5" in records[i]["provenance"].get("scores", "") and
            ("album" not in group_by or records[i].get("album_id")) and
            ("artist" not in group_by or records[i].get("artist_ids")) for i in indices)
        split = ("test" if int(digest[:8], 16) / 2**32 < .25 else "calibration") if eligible else "regression"
        for i in indices:
            assignments[i] = (split, digest[:16])
    # Repeated song IDs must not overweight the metrics or hide conflicting labels.
    songs = {}
    for i, row in enumerate(records):
        sid = row["song_id"]
        if sid in songs:
            previous = records[songs[sid]]
            if row.get("labels") != previous.get("labels") or row.get("automatic") != previous.get("automatic"):
                raise ValueError("conflicting duplicate song records; review/deduplicate explicitly")
        else:
            songs[sid] = i
    results = {}
    for split in ("regression", "calibration", "test"):
        rows = [records[i] for i in songs.values() if assignments[i][0] == split]
        dimensions = {}
        for dim, (positive, negative) in VALUES.items():
            counts = Counter(tp=0, fp=0, tn=0, fn=0, unknown=0, missing_prediction=0)
            for row in rows:
                truth = row.get("labels", {}).get(dim)
                predicted = row.get("automatic", {}).get(dim)
                if truth in UNKNOWN:
                    counts["unknown"] += 1
                    continue
                if predicted not in (positive, negative):
                    counts["missing_prediction"] += 1
                    continue
                counts["tp" if truth == positive and predicted == positive else
                    "fn" if truth == positive else "fp" if predicted == positive else "tn"] += 1
            def ratio(a, b):
                return a / b if b else None
            dimensions[dim] = dict(counts, precision=ratio(counts["tp"], counts["tp"] + counts["fp"]),
                recall=ratio(counts["tp"], counts["tp"] + counts["fn"]))
        results[split] = {"songs": len(rows), "dimensions": dimensions}
    manifest = [{"song_id": row["song_id"], "split": assignments[i][0], "group": assignments[i][1],
        "provenance": row["provenance"]} for i, row in enumerate(records)]
    warnings = ["Frozen split is an evaluation aid, not proof of generalization. Keep test labels sealed during tuning.",
        "Review all repeated releases, aliases and multi-artist IDs before asserting independence."]
    if not results["test"]["songs"]:
        warnings.append("No eligible independent test samples; no independent accuracy claim is possible.")
    return dict(seed=seed, group_by=list(group_by), components=len(components), duplicates=len(records)-len(songs),
        results=results, manifest=manifest, warnings=warnings)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("records", type=Path)
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--seed", default="jeek-v5-frozen-1")
    p.add_argument("--group-by", default="song,album,artist")
    args = p.parse_args()
    raw = args.records.read_bytes()
    rows = [json.loads(line) for line in raw.decode("utf-8").splitlines() if line.strip()]
    report = evaluate(rows, args.seed, tuple(args.group_by.split(",")))
    report["input_sha256"] = hashlib.sha256(raw).hexdigest()
    report["tool_sha256"] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({"components": report["components"], "results": report["results"], "warnings": report["warnings"]}))

if __name__ == "__main__":
    main()
