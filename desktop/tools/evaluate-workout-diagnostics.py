"""Evaluate explicitly named diagnostic exports; never scans a library or runs audio.

Input: directory containing <trackId>.json exports from AnalysisProbe --diagnostics
or assembled MCP diagnose_analysis results. Labels are the reviewed recording manifest.
Output has no paths, recording keys or audio. Experiments are counterfactuals, not rules
or independent accuracy. Missing new features stay missing; old caches are not accepted.
"""
from contextlib import closing
import argparse
import hashlib
import json
import math
import sqlite3
from pathlib import Path
from statistics import mean, median


def longest_run(values):
    best = run = 0
    for value in values:
        run = run + 1 if value else 0
        best = max(best, run)
    return best


def persistent(candidate):
    # Fixed descriptive screen, not fitted to the six labels, not a beat detector.
    return candidate["correlation"] >= .3 and sum(v >= .2 for v in candidate["quarterCorrelations"]) >= 3


def cache_impact(path):
    """Read-only changed-count review; no acoustic features or user labels invented."""
    counts = dict(usable=0, baselineSport=0, anyRun3Adds=0, twoSegmentsRun3Adds=0, missingNewEvidence=0)
    with closing(sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True)) as db:
        for version, data, signature, mtime, size in db.execute(
            "select f.analyzer_version,f.data,f.signature,t.mtime,t.size from track_features f "
            "join track t on t.track_key=f.track_key where f.failed=0"
        ):
            if version not in (5, 6) or signature != f"{mtime}:{size}":
                continue
            e = json.loads(data).get("analysis_evidence")
            e = json.loads(e) if e else None
            if not e or e.get("contract") not in (5, 6) or not e["feature_complete"]:
                continue
            good = lambda s: s.get("mean") is not None and s["frames"] and s.get("error") is None
            segments = e["segments"]
            if not any(not s["additional"] for s in segments) or not all(good(s) for s in segments if not s["additional"]):
                continue
            allow_extra = all(good(s) for s in segments if s["additional"])
            segments = [s for s in segments if good(s) and (not s["additional"] or allow_extra)]
            scores = [[math.log((f["energy"]+.003)/(f["calm"]+.003)) for f in s["frames"]] for s in segments]
            baseline = median([v for s in scores for v in s]) >= 2.75
            runs = [longest_run(v >= 2.75 for v in s) for s in scores]
            counts["usable"] += 1
            counts["baselineSport"] += baseline
            counts["anyRun3Adds"] += not baseline and max(runs) >= 3
            counts["twoSegmentsRun3Adds"] += not baseline and sum(r >= 3 for r in runs) >= 2
            counts["missingNewEvidence"] += 1
    return dict(**counts, correctnessUnknown=True, note="Cached group windows only. No raw521 or rhythm features inferred; changes counted before manual overrides, not accuracy.")


def describe(d):
    if d["contract"] != "analysis_diagnostics_v1" or not d["complete"]:
        raise ValueError("Needs complete production diagnostic evidence")
    names = d["classNames"]
    energy, calm = d["energyIds"], d["calmIds"]
    assert len(names) == 521 and len(energy) == 12 and len(calm) == 6
    assert energy == [274, 271, 269, 234, 212, 214, 157, 159, 31, 231, 211, 240]
    assert calm == [273, 272, 266, 248, 241, 262]
    segments = d["segments"]
    assert len(segments) == len(d["productionEvidence"]["segments"])
    windows = [w for s in segments for w in s["windows"]]
    scores, augmented = [], []
    for s, production in zip(segments, d["productionEvidence"]["segments"]):
        assert s["complete"] and len(s["windows"]) == len(production["frames"])
        for w, f in zip(s["windows"], production["frames"]):
            raw = w["scores521"]
            assert len(raw) == 521 and all(math.isfinite(v) and 0 <= v <= 1 for v in raw)
            e, c = sum(raw[k] for k in energy), sum(raw[k] for k in calm)
            assert abs(e - f["energy"]) < 1e-6 and abs(c - f["calm"]) < 1e-6
            score = math.log((e + .003) / (c + .003))
            assert abs(score - w["sportScore"]) < 1e-6
            scores.append(score)
            # Exploratory inclusive percussion sum. Hierarchical classes overlap;
            # this deliberately generous counterfactual is not a calibrated group.
            extra = sum(raw[k] for k in range(156, 179) if k not in energy)
            augmented.append(math.log((e + extra + .003) / (c + .003)))
    assert abs(median(scores) - d["sportMedian"]) < 1e-6
    contributions = [mean(w["scores521"][k] for w in windows) for k in range(521)]
    describe_class = lambda k: dict(id=k, name=names[k], meanResponse=contributions[k])
    runs = [longest_run(w["sportScore"] >= 2.75 for w in s["windows"]) for s in segments]
    rhythm = []
    for s in segments:
        candidates = s["tempoCandidates"]
        peaks = [c for j, c in enumerate(candidates) if 0 < j < len(candidates)-1
                 and c["correlation"] >= candidates[j-1]["correlation"] and c["correlation"] > candidates[j+1]["correlation"]]
        peaks.sort(key=lambda c: c["correlation"], reverse=True)
        bpm = s["productionBpm"]
        alternatives = []
        for factor in (.5, 1, 2):
            if bpm and 40 <= bpm * factor <= 240:
                c = min(candidates, key=lambda c: abs(c["bpm"] - bpm * factor))
                alternatives.append(dict(factor=factor, **c))
        rhythm.append(dict(startMs=s["startMs"], endMs=s["endMs"], productionBpm=bpm,
            longestLooseRunWindows=runs[len(rhythm)], topLocalPeaks=peaks[:4], halfDoubleAlternatives=alternatives,
            persistentPeriodPresent=any(persistent(c) for c in candidates)))
    # Same physical period must occur independently; gaps do not imply continuity.
    seed_periods = [c["bpm"] for s in segments for c in s["tempoCandidates"] if persistent(c)]
    segment_support = max((sum(any(persistent(c) and abs(c["bpm"] / bpm - 1) <= .05
        for c in s["tempoCandidates"]) for s in segments) for bpm in seed_periods), default=0)
    return dict(sportMedian=median(scores), looseSupport=sum(v >= 2.75 for v in scores), windows=len(scores),
        selectedSeconds=sum((s["endMs"] - s["startMs"])/1000 for s in segments),
        segmentRuns=runs, segmentsWithSharedPersistentPeriod=segment_support,
        experiments=dict(production=median(scores) >= 2.75, anyRun3=max(runs) >= 3,
            twoSegmentsRun3=sum(r >= 3 for r in runs) >= 2,
            run3AndPersistentPeriod=any(r >= 3 and s["persistentPeriodPresent"] for r,s in zip(runs,rhythm)),
            inclusivePercussionMedian=median(augmented) >= 2.75),
        inclusivePercussionMedian=median(augmented),
        energyClasses=[describe_class(k) for k in energy], calmClasses=[describe_class(k) for k in calm],
        topOutsideGroups=[describe_class(k) for k in sorted(set(range(521))-set(energy)-set(calm), key=lambda k: contributions[k], reverse=True)[:12]],
        drumKitPlusDrumMean=contributions[157] + contributions[159],
        popShareOfMeanEnergy=contributions[211] / max(sum(contributions[k] for k in energy), 1e-12),
        rhythm=rhythm, elapsedMs=d["elapsedMs"], rhythmExtraMs=d["rhythmExtraMs"], modelSha256=d["modelSha256"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--labels", type=Path, default=Path(__file__).resolve().parents[2]/"shared/testcases/workout-feedback.json")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cache-db", type=Path, help="Optional explicit existing library.db; read-only aggregate impact counts, no identities exported")
    args = parser.parse_args()
    rows = []
    for label in json.loads(args.labels.read_text(encoding="utf-8"))["records"]:
        path = args.directory / f'{label["trackId"]}.json'
        row = dict(trackId=label["trackId"], label=label["label"], used_for_tuning=True, independent=False)
        if path.is_file():
            row.update(describe(json.loads(path.read_text(encoding="utf-8-sig"))))
            row["inputSha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        else:
            row["missingDiagnosticEvidence"] = True
        rows.append(row)
    report = dict(contract="workout_diagnostic_evaluation_v1", independentEvaluation=False,
        note="Explicit feedback, tuning/regression only. Experiment flags describe the old median and candidate rules, not deployed v6 tags. No missing features inferred from cache. Quarter/segment support does not assert continuity across gaps.",
        scriptSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), rows=rows)
    if args.cache_db:
        report["cacheImpact"] = cache_impact(args.cache_db)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    for r in rows:
        print(json.dumps({k:r[k] for k in ("trackId", "experiments", "segmentRuns", "inclusivePercussionMedian", "popShareOfMeanEnergy") if k in r}))


if __name__ == "__main__":
    main()
