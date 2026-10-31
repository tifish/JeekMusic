"""One predeclared, bounded local preference probe; never a product model.

Reads complete frozen representation exports, never audio or production caches.
Two training-only PCA coordinates and one fixed ridge penalty; no model search.
Keep the full output in ignored artifacts. No fitted model is exported.
"""
import argparse
import hashlib
import json
from pathlib import Path
import time

import numpy as np

import importlib.util

SPEC = importlib.util.spec_from_file_location(
    "representations", Path(__file__).with_name("research-workout-representations.py"))
REP = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(REP)
PROTOCOL = REP.REPO / "shared/testcases/workout-preference-protocol.json"
FIRST_PAIR = (353, 354)  # Split membership only, never model input.
CONDITIONS = tuple(c for c, _, _ in REP.CONDITIONS)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def combine(reports, protocol_hash):
    """Reuse completed recordings, reject overlap or incompatible extraction."""
    records = []
    for report in reports:
        REP.validate_report(report, protocol_hash)
        if report.get("normalizeRms") != .1 or report.get("sourceCommit") != "516acb2a0ff5ef73f64547898e018e793152c506":
            raise ValueError("Frozen preprocessing/source mismatch")
        records.extend(report["records"])
    merged = {**reports[0], "records": records, "requestedRecordCount": len(records)}
    REP.validate_report(merged, protocol_hash)
    return merged


def pooled(record, condition):
    segments = [s for s in record["segments"] if s["condition"] == condition]
    if len(segments) != 3 or {s["context"] for s in segments} != {0, 1, 2}:
        raise ValueError("Whole recording requires all three contexts")
    return REP.unit(np.mean([s["vectors"]["penultimate"] for s in segments], axis=0))


def fit(x, y):
    """Training-only PCA/scaling plus balanced mean-loss ridge, alpha=1."""
    x, y = np.asarray(x, dtype=float), np.asarray(y, dtype=float)
    if (x.ndim != 2 or len(x) < 3 or y.shape != (len(x),)
            or set(y) != {-1, 1} or not np.isfinite(x).all()):
        raise ValueError("Need finite training recordings and both explicit labels")
    center = x.mean(axis=0)
    _, singular, vt = np.linalg.svd(x-center, full_matrices=False)
    if len(singular) < 2 or singular[1] <= 1e-10:
        raise ValueError("Training PCA rank below two")
    axes = vt[:2].T
    projected = (x-center) @ axes
    scale = np.maximum(projected.std(axis=0), 1e-6)
    z = projected / scale
    weights = np.array([1 / np.count_nonzero(y == value) for value in y])
    weights /= weights.sum()
    zmean, ymean = weights @ z, weights @ y
    zc, yc = z-zmean, y-ymean
    coef = np.linalg.solve(zc.T @ (weights[:, None]*zc) + np.eye(2), zc.T @ (weights*yc))
    return dict(center=center, axes=axes, scale=scale, coef=coef,
                intercept=ymean-zmean @ coef, pcaVarianceFraction=float(np.sum(singular[:2]**2)/np.sum(singular**2)))


def score(model, x):
    return ((np.asarray(x)-model["center"]) @ model["axes"] / model["scale"]) @ model["coef"] + model["intercept"]


def folds(rows, mode):
    if mode == "first_pair":
        if not set(FIRST_PAIR).issubset({r["trackId"] for r in rows}) or len(rows) != 11:
            raise ValueError("First-pair probe requires the frozen nine plus both new recordings")
        groups = [list(FIRST_PAIR)]
    else:
        groups_by_key = {}
        for r in rows:
            if mode == "recording":
                key = r["trackId"]
            elif mode == "album":
                key = (r["artist"], r["album"]) if r.get("album") else ("missing", r["trackId"])
            elif mode == "artist":
                key = r["artist"]
            else:
                raise ValueError("Unknown grouping")
            groups_by_key.setdefault(key, []).append(r["trackId"])
        groups = list(groups_by_key.values())
    all_ids = {r["trackId"] for r in rows}
    return [(sorted(all_ids-set(test)), sorted(test)) for test in groups]


def confusion(predictions, baseline=False):
    result = dict(tp=0, fn=0, tn=0, fp=0)
    for p in predictions:
        positive = p["baselineSport"] if baseline else p["scores"]["base"] > 0
        result[("tp" if positive else "fn") if p["label"] == "SPORT" else ("fp" if positive else "tn")] += 1
    result["sportRecall"] = result["tp"]/(result["tp"]+result["fn"]) if result["tp"]+result["fn"] else None
    result["nonSportRecall"] = result["tn"]/(result["tn"]+result["fp"]) if result["tn"]+result["fp"] else None
    return result


def evaluate(merged, feedback, check_budget=lambda: None):
    # Reuse the strict provenance/vector validator, not automatic library labels.
    REP.audit(merged, feedback, merged["protocolSha256"])
    labels = {r["trackId"]: r for r in feedback["records"]}
    if len(labels) != len(feedback["records"]):
        raise ValueError("Duplicate feedback identity")
    records = {r["trackId"]: r for r in merged["records"]}
    rows = [labels[tid] for tid in sorted(records)]
    result = {}
    for mode in ("first_pair", "recording", "album", "artist"):
        predictions, details = [], []
        for train, test in folds(rows, mode):
            check_budget()
            x = np.array([pooled(records[tid], "base") for tid in train])
            y = np.array([1 if labels[tid]["label"] == "SPORT" else -1 for tid in train])
            model = fit(x, y)
            details.append(dict(trainIds=train, testIds=test, pcaVarianceFraction=model["pcaVarianceFraction"]))
            for tid in test:
                scores = {condition: float(score(model, pooled(records[tid], condition))) for condition in CONDITIONS}
                if not all(np.isfinite(list(scores.values()))):
                    raise ValueError("Invalid held-out margin")
                predictions.append(dict(trackId=tid, label=labels[tid]["label"], scores=scores,
                    baselineSport=bool(np.count_nonzero(y == 1) > np.count_nonzero(y == -1)),
                    perturbationSignStable=all((v > 0) == (scores["base"] > 0) for v in scores.values())))
        result[mode] = dict(folds=details, predictions=predictions, confusion=confusion(predictions),
                            majorityBaseline=confusion(predictions, True),
                            signStable=all(p["perturbationSignStable"] for p in predictions))
    return result


def first_pair_neighbors(merged, feedback):
    """New pair queried against OLD nine only, then augmented audit separately."""
    labels = {r["trackId"]: r for r in feedback["records"]}
    refs = [(r["trackId"], s) for r in merged["records"] if r["trackId"] not in FIRST_PAIR
            for s in r["segments"] if s["condition"] == "base"]
    output = {}
    for name in REP.DIMENSIONS:
        vectors = np.array([s["vectors"][name] for _, s in refs])
        queries = []
        for r in merged["records"]:
            if r["trackId"] not in FIRST_PAIR:
                continue
            for s in r["segments"]:
                distances = np.clip(1-vectors @ s["vectors"][name], 0, 2)
                order = np.argsort(distances, kind="stable")
                tid = refs[order[0]][0]
                others = [j for j in order if refs[j][0] != tid]
                tied = bool(others and abs(distances[others[0]]-distances[order[0]]) < 1e-9)
                queries.append(dict(trackId=r["trackId"], context=s["context"], condition=s["condition"],
                    neighborId=tid, distance=float(distances[order[0]]), tied=tied,
                    labelAgrees=not tied and labels[tid]["label"] == labels[r["trackId"]]["label"]))
        output[name] = queries
    return output


def evidence_index(result, reports, feedback):
    """Public reproducibility index: no paths, vectors, PCM or fitted weights."""
    index = {k: v for k, v in result.items() if k not in (
        "evaluations", "firstPairAgainstPriorNine", "augmentedRepresentationAudit")}
    index["contract"] = "workout_preference_evidence_index_v1"
    index["representationScriptSha256"] = sha(Path(REP.__file__))
    author = REP.load_module("author_identity", Path(__file__).with_name("research-workout-music-tags.py"))
    index["authorSourceHashes"] = author.SOURCE_HASHES
    index["checkpointSha256"] = REP.CHECKPOINT_SHA256
    index["extractionRuns"] = [dict(recordings=r["requestedRecordCount"], contexts=sum(len(t["segments"]) for t in r["records"]),
        elapsedSeconds=r["elapsedSeconds"], peakWorkingSetBytes=r["peakWorkingSetBytes"],
        inferenceSeconds=sum(s["inferSeconds"] for t in r["records"] for s in t["segments"])) for r in reports]
    index["evaluations"] = {}
    for mode, evaluation in result["evaluations"].items():
        predictions = evaluation["predictions"]
        summary = {k: evaluation[k] for k in ("confusion", "majorityBaseline", "signStable")}
        summary.update(foldCount=len(evaluation["folds"]),
            falsePositiveIds=[p["trackId"] for p in predictions if p["label"] == "NON_SPORT" and p["scores"]["base"] > 0],
            falseNegativeIds=[p["trackId"] for p in predictions if p["label"] == "SPORT" and p["scores"]["base"] <= 0])
        if mode == "first_pair":
            summary["predictions"] = [dict(trackId=p["trackId"], baseScore=p["scores"]["base"],
                scoreRange=[min(p["scores"].values()), max(p["scores"].values())]) for p in predictions]
        index["evaluations"][mode] = summary
    labels = {r["trackId"]: r for r in feedback["records"]}
    index["representations"] = {}
    for name, audit in result["augmentedRepresentationAudit"]["representations"].items():
        summary = {k: v for k, v in audit.items() if k != "records"}
        summary["newPair"] = []
        for record in audit["records"]:
            tid = record["trackId"]
            if tid not in FIRST_PAIR:
                continue
            old_queries = [q for q in result["firstPairAgainstPriorNine"][name] if q["trackId"] == tid]
            row = {k: v for k, v in record.items() if k != "queries"}
            row["priorNineBaseNeighbors"] = [q["neighborId"] for q in old_queries if q["condition"] == "base"]
            row["priorNineAllContextAgreement"] = sum(q["labelAgrees"] for q in old_queries)
            row["augmentedBaseNeighbors"] = [q["neighborId"] for q in record["queries"] if q["condition"] == "base"]
            summary["newPair"].append(row)
        summary["identityByArtist"] = []
        for artist in sorted({labels[r["trackId"]]["artist"] for r in audit["records"]}):
            own = [r for r in audit["records"] if labels[r["trackId"]]["artist"] == artist]
            base = [q for r in own for q in r["queries"] if q["condition"] == "base"]
            summary["identityByArtist"].append(dict(artist=artist, recordings=len(own),
                sportRecordings=sum(labels[r["trackId"]]["label"] == "SPORT" for r in own),
                sameArtistBaseNeighbors=sum(q["sameArtist"] for q in base), baseContexts=len(base)))
        index["representations"][name] = summary
    failures = []
    first = result["evaluations"]["first_pair"]
    if first["confusion"]["fp"] or first["confusion"]["fn"]:
        failures.append("first_pair_not_separated")
    if any(v["confusion"]["fp"] for v in result["evaluations"].values()):
        failures.append("held_out_negative_regression")
    if any(not v["signStable"] for v in result["evaluations"].values()):
        failures.append("perturbation_sign_flip")
    index["decision"] = dict(candidateRejected=bool(failures), reasons=failures,
        productionDeploymentSupported=False,
        next="Stop this candidate if rejected. Even all passes justify only separately planned prospective personal-preference validation; never a public-model claim.")
    return index


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, action="append", required=True)
    parser.add_argument("--feedback", type=Path, default=REP.REPO/"shared/testcases/workout-feedback.json")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--index", type=Path, help="Optional public summary without private vectors/paths")
    args = parser.parse_args()
    protocol = json.loads(PROTOCOL.read_text(encoding="utf-8"))
    if protocol["contract"] != "workout_preference_protocol_v1" or protocol["parameters"] != dict(components=2, scaleFloor=1e-6, ridgeAlpha=1.0, threshold=0.0):
        raise ValueError("Unsupported preference protocol; do not silently change the candidate")
    started = time.perf_counter()
    deadline = time.monotonic()+30
    def check_budget():
        if time.monotonic() >= deadline:
            raise TimeoutError("Preference probe budget exhausted")
    sampling = REP.load_module("sampling", Path(__file__).with_name("research-workout-beats.py"))
    result = dict(contract="workout_preference_research_v1", state="incomplete", cacheCompatible=False,
        independentAccuracy=False, preferenceProtocolSha256=sha(PROTOCOL), representationProtocolSha256=sha(REP.PROTOCOL),
        scriptSha256=sha(Path(__file__)), feedbackSha256=sha(args.feedback), inputReportSha256=[sha(p) for p in args.report],
        budgetSeconds=30)
    sampling.write_report(args.output, result)
    try:
        reports = [json.loads(p.read_text(encoding="utf-8")) for p in args.report]
        merged = combine(reports, sha(REP.PROTOCOL))
        feedback = json.loads(args.feedback.read_text(encoding="utf-8"))
        result["evaluations"] = evaluate(merged, feedback, check_budget)
        check_budget()
        result["firstPairAgainstPriorNine"] = first_pair_neighbors(merged, feedback)
        result["augmentedRepresentationAudit"] = REP.audit(merged, feedback, sha(REP.PROTOCOL))
        check_budget()
        result["state"] = "completed"
    except TimeoutError:
        result["stopReason"] = "budget_exhausted"
        raise
    except KeyboardInterrupt:
        result["stopReason"] = "cancelled"
        raise
    except Exception:
        result["stopReason"] = "failed"
        raise
    finally:
        result["elapsedSeconds"] = time.perf_counter()-started
        sampling.write_report(args.output, result)
    if args.index:
        sampling.write_report(args.index, evidence_index(result, reports, feedback))
    print(json.dumps({k: {n: v[n] for n in ("confusion", "majorityBaseline", "signStable")}
                      for k, v in result["evaluations"].items()}))


if __name__ == "__main__":
    main()
