"""Bounded local musicnn feature audit; no fitting, downloads or production writes.

The protocol is fixed in shared/testcases/workout-representation-protocol.json.
Only original-author MTT weights are used. Output includes private vectors: keep
it under ignored artifacts, never commit it or treat it as importable cache data.
"""
import argparse
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import time

import numpy as np

REPO = Path(__file__).resolve().parents[2]
PROTOCOL = REPO / "shared/testcases/workout-representation-protocol.json"
CONDITIONS = (("base", 0, 0), ("gain-6", -6, 0), ("gain+6", 6, 0),
              ("shift-2", 0, -2000), ("shift+2", 0, 2000))
DIMENSIONS = {"penultimate": 200, "temporal": 153, "tags": 50}
CHECKPOINT_SHA256 = "041f3935e981a65cea739842d30c188d3a7d2adf4410955c8668eb3de4a5acab"


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def unit(values):
    values = np.asarray(values, dtype=np.float64)
    if values.ndim < 1 or not np.isfinite(values).all():
        raise ValueError("Invalid feature vector")
    norm = np.linalg.norm(values, axis=-1, keepdims=True)
    if np.any(norm <= 1e-12):
        raise ValueError("Zero feature vector has no cosine direction")
    return values / norm


def pool(windows, dimension):
    values = np.asarray(windows, dtype=np.float64)
    if values.shape != (10, dimension):
        raise ValueError("Expected ten complete musicnn windows")
    return unit(np.mean(unit(values), axis=0))


def normalized_pcm(pcm, gain_db):
    pcm = np.asarray(pcm, dtype=np.float64)
    if pcm.shape != (480000,) or not np.isfinite(pcm).all():
        raise ValueError("Expected finite 30s mono 16k PCM")
    pcm = pcm * 10**(gain_db / 20)
    rms = float(np.sqrt(np.mean(pcm**2)))
    if rms <= 1e-9:
        raise ValueError("Silent context has no auditable musical representation")
    return (pcm * (.1 / rms)).astype(np.float32), rms


def contexts(row, sampling):
    track = row["track"]
    if track.get("cueTrackNo") is not None:
        raise ValueError("Only explicit non-CUE logical recordings are supported")
    spans = sampling.intervals(track["durationMs"],
        row["explanation"]["evidence"]["segments"][:3], 30)
    if len(spans) != 3 or any(b-a != 30000 for a, b in spans):
        raise ValueError("Three complete contexts required")
    ordered = sorted(spans)
    if any(b > c for (_, b), (c, _) in zip(ordered, ordered[1:])):
        raise ValueError("Independent contexts must not overlap")
    for start, _ in spans:
        if start < 2000 or start+32100 > track["durationMs"]:
            raise ValueError("All fixed shifts and decode tail must fit without clamping")
    return spans


def validate_report(report, protocol_hash):
    if (report.get("contract") != "workout_representation_research_v1"
            or report.get("protocolSha256") != protocol_hash
            or report.get("state") != "completed"
            or report.get("cacheCompatible") is not False
            or report.get("model") != "MTT_musicnn"
            or report.get("checkpointSha256") != CHECKPOINT_SHA256):
        raise ValueError("Only completed, matching local audit contracts are accepted")
    records = report["records"]
    if len(records) != report["requestedRecordCount"] or not 2 <= len(records) <= 16:
        raise ValueError("Missing/invalid recording count")
    ids = [r["trackId"] for r in records]
    if len(set(ids)) != len(ids):
        raise ValueError("Duplicate recording IDs")
    expected = {(i, name) for i in range(3) for name, _, _ in CONDITIONS}
    for record in records:
        segments = record["segments"]
        if len(segments) != 15 or {(s["context"], s["condition"]) for s in segments} != expected:
            raise ValueError("Missing/duplicate context or perturbation")
        for s in segments:
            for name, dim in DIMENSIONS.items():
                vector = np.asarray(s["vectors"][name])
                if (vector.shape != (dim,) or not np.isfinite(vector).all()
                        or abs(np.linalg.norm(vector)-1) > 1e-6):
                    raise ValueError("Invalid/non-unit feature contract")
    return records


def audit(report, feedback, protocol_hash):
    records = validate_report(report, protocol_hash)
    labels = {r["trackId"]: r for r in feedback["records"]}
    if any(r["trackId"] not in labels for r in records):
        raise ValueError("Explicit feedback required, never infer unknown labels")
    if any(labels[r["trackId"]].get("label_source") != "explicit_user_feedback"
           or labels[r["trackId"]].get("independent") is not False
           or labels[r["trackId"]].get("used_for_tuning") is not True
           or labels[r["trackId"]].get("label") not in ("SPORT", "NON_SPORT")
           or not isinstance(labels[r["trackId"]].get("artist"), str)
           or not labels[r["trackId"]]["artist"].strip() for r in records):
        raise ValueError("This audit requires honestly marked calibration feedback")
    result = dict(contract="workout_representation_audit_v1", independentAccuracy=False,
        cacheCompatible=False, protocolSha256=protocol_hash, representations={})
    positive_artists = {labels[r["trackId"]]["artist"] for r in records
                        if labels[r["trackId"]]["label"] == "SPORT"}
    result["positiveArtistCount"] = len(positive_artists)
    result["identityGeneralizationEstablished"] = False
    for name in DIMENSIONS:
        bases = [(r["trackId"], s) for r in records for s in r["segments"] if s["condition"] == "base"]
        vectors = np.asarray([s["vectors"][name] for _, s in bases])
        detail = []
        for r in records:
            tid = r["trackId"]
            own_base = {s["context"]: s for s in r["segments"] if s["condition"] == "base"}
            other = np.asarray([i for i, (other_id, _) in enumerate(bases) if other_id != tid])
            # All three contexts from the query recording are excluded, including shifted/gain versions.
            queries = []
            for s in r["segments"]:
                v = np.asarray(s["vectors"][name])
                distances = np.clip(1-vectors[other] @ v, 0, 2)
                order = np.argsort(distances, kind="stable")
                j = int(other[order[0]])
                neighbor, ns = bases[j]
                base = np.asarray(own_base[s["context"]]["vectors"][name])
                displacement = float(np.clip(1-base @ v, 0, 2))
                base_other_distance = float(np.min(np.clip(1-vectors[other] @ base, 0, 2)))
                # Three equal-distance contexts of ONE target recording are not
                # three independent neighbors, nor an ambiguous recording tie.
                different = [k for k in order if bases[int(other[k])][0] != neighbor]
                tie = bool(different and abs(distances[different[0]]-distances[order[0]]) < 1e-9)
                queries.append(dict(context=s["context"], condition=s["condition"],
                    neighborId=neighbor, neighborContext=ns["context"], distance=float(distances[order[0]]),
                    tied=tie, labelAgrees=labels[tid]["label"] == labels[neighbor]["label"],
                    sameArtist=labels[tid]["artist"] == labels[neighbor]["artist"],
                    displacement=displacement, nearestOtherBaseDistance=base_other_distance,
                    perturbationStable=displacement < .5*base_other_distance))
            base_queries = [q for q in queries if q["condition"] == "base"]
            base_distances = np.asarray([own_base[i]["vectors"][name] for i in range(3)])
            internal = np.clip(1-base_distances @ base_distances.T, 0, 2)
            detail.append(dict(trackId=tid, queries=queries,
                baseContextMaxDistance=float(np.max(internal)),
                baseContextLabelAgreement=sum(q["labelAgrees"] and not q["tied"] for q in base_queries),
                allContextLabelAgreement=sum(q["labelAgrees"] and not q["tied"] for q in queries),
                perturbationStable=all(q["perturbationStable"] for q in queries),
                gainMaxDistance=max(q["displacement"] for q in queries if q["condition"].startswith("gain")),
                shiftMaxDistance=max(q["displacement"] for q in queries if q["condition"].startswith("shift"))))
        all_queries = [q for r in detail for q in r["queries"]]
        base_queries = [q for q in all_queries if q["condition"] == "base"]
        result["representations"][name] = dict(records=detail,
            stabilityGate=all(r["perturbationStable"] for r in detail),
            neighborhoodGate=all(q["labelAgrees"] and not q["tied"] for q in all_queries),
            baseContextLabelAgreement=sum(q["labelAgrees"] and not q["tied"] for q in base_queries),
            baseContextCount=len(base_queries),
            baseContextSameArtistCount=sum(q["sameArtist"] for q in base_queries),
            interpretation="Calibration neighborhood agreement, not accuracy; no learned classifier and no same-recording neighbors.")
    return result


def extract(args, protocol_hash):
    sampling = load_module("sampling", Path(__file__).with_name("research-workout-beats.py"))
    author = load_module("author_identity", Path(__file__).with_name("research-workout-music-tags.py"))
    root = args.author_root.resolve(strict=True)
    for filename, expected in author.SOURCE_HASHES.items():
        if hashlib.sha256((root/"musicnn"/filename).read_bytes()).hexdigest() != expected:
            raise ValueError("Original author source mismatch")
    weight = root/"musicnn/MTT_musicnn/.data-00000-of-00001"
    if hashlib.sha256(weight.read_bytes()).hexdigest() != author.WEIGHT_HASHES["MTT_musicnn"]:
        raise ValueError("Original checkpoint mismatch")
    rows = json.loads(args.manifest.read_text(encoding="utf-8"))
    if not isinstance(rows, list) or not 1 <= len(rows) <= 16:
        raise ValueError("Explicit 1-16 recording manifest required")
    if len({r["track"]["id"] for r in rows}) != len(rows):
        raise ValueError("Duplicate recording manifest")
    for row in rows:
        contexts(row, sampling)
    deadline = time.monotonic()+args.budget_seconds
    started = time.perf_counter()
    report = dict(contract="workout_representation_research_v1", protocolSha256=protocol_hash,
        state="incomplete", cacheCompatible=False, model="MTT_musicnn", sourceCommit=author.COMMIT,
        checkpointSha256=author.WEIGHT_HASHES["MTT_musicnn"], checkpointBytes=weight.stat().st_size,
        requestedRecordCount=len(rows), normalizeRms=.1, records=[], budgetSeconds=args.budget_seconds)
    sampling.write_report(args.output, report)

    def check_budget():
        if time.monotonic() >= deadline:
            raise TimeoutError("Research budget exhausted")

    os.environ["TF_CPP_MIN_LOG_LEVEL"] = "2"
    os.environ["TF_ENABLE_ONEDNN_OPTS"] = "0"
    os.environ["OMP_NUM_THREADS"] = "2"
    import psutil
    import soundfile as sf
    from threadpoolctl import threadpool_limits
    threadpool_limits(limits=1)
    try:
        import tensorflow as tf
        sys.path.insert(0, str(root))
        from musicnn import models, extractor
        tf.compat.v1.disable_eager_execution()
        with tf.name_scope("model"):
            x = tf.compat.v1.placeholder(tf.float32, [None, 187, 96])
            training = tf.compat.v1.placeholder(tf.bool)
            features = models.define_model(x, training, "MTT_musicnn", 50)
            outputs = [features[8], tf.reduce_mean(features[2], axis=1), tf.nn.sigmoid(features[0])]
        saver = tf.compat.v1.train.Saver()
        report.update(tensorflow=tf.__version__, numpy=np.__version__)
        with tf.compat.v1.Session(config=tf.compat.v1.ConfigProto(
                intra_op_parallelism_threads=2, inter_op_parallelism_threads=1)) as session:
            begin = time.perf_counter()
            saver.restore(session, str(weight.parent)+"/")
            report["modelLoadSeconds"] = time.perf_counter()-begin
            check_budget()
            for row in rows:
                track = row["track"]
                record = dict(trackId=track["id"], segments=[])
                for i, (base_start, _) in enumerate(contexts(row, sampling)):
                    for condition, gain, offset in CONDITIONS:
                        check_budget()
                        begin = time.perf_counter()
                        start = base_start+offset
                        decoded = subprocess.run(["ffmpeg", "-v", "error", "-nostdin", "-ss", str(start/1000),
                            "-i", track["path"], "-t", "30.1", "-vn", "-ac", "1", "-ar", "16000", "-f", "f32le", "pipe:1"],
                            capture_output=True, timeout=min(45, max(.001, deadline-time.monotonic())),
                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
                        if decoded.returncode or len(decoded.stdout)%4 or not 480000 <= len(decoded.stdout)//4 <= 483200:
                            raise ValueError("Local decode failed/truncated/oversized; private stderr suppressed")
                        pcm, rms = normalized_pcm(np.frombuffer(decoded.stdout, dtype="<f4")[:480000], gain)
                        wav = io.BytesIO()
                        sf.write(wav, pcm, 16000, format="WAV", subtype="FLOAT")
                        wav.seek(0)
                        batch, _ = extractor.batch_data(wav, 187, 187)
                        if batch.shape != (10, 187, 96) or not np.isfinite(batch).all():
                            raise ValueError("Original mel input contract")
                        values = {name: [] for name in DIMENSIONS}
                        inference = time.perf_counter()
                        for window in batch:
                            check_budget()
                            fetched = session.run(outputs, feed_dict={x: window[None], training: False})
                            for name, value in zip(DIMENSIONS, fetched):
                                values[name].append(value[0])
                        check_budget()
                        record["segments"].append(dict(context=i, condition=condition, startMs=start, inputRms=rms,
                            vectors={name: pool(values[name], dim).tolist() for name, dim in DIMENSIONS.items()},
                            inferSeconds=time.perf_counter()-inference, totalSeconds=time.perf_counter()-begin))
                check_budget()
                report["records"].append(record)
                report["peakWorkingSetBytes"] = psutil.Process().memory_info().peak_wset
                sampling.write_report(args.output, report)
                print(json.dumps(dict(trackId=track["id"], contexts=len(record["segments"]))), flush=True)
        report["state"] = "completed"
    except (TimeoutError, subprocess.TimeoutExpired):
        report["stopReason"] = "budget_exhausted"
        raise
    except KeyboardInterrupt:
        report["stopReason"] = "cancelled"
        raise
    except Exception:
        report["stopReason"] = "failed"
        raise
    finally:
        report["elapsedSeconds"] = time.perf_counter()-started
        sampling.write_report(args.output, report)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    run = sub.add_parser("extract")
    run.add_argument("manifest", type=Path)
    run.add_argument("--author-root", type=Path, required=True)
    run.add_argument("--output", type=Path, required=True)
    run.add_argument("--budget-seconds", type=int, choices=range(15, 901), default=300)
    evaluate = sub.add_parser("audit")
    evaluate.add_argument("report", type=Path)
    evaluate.add_argument("--feedback", type=Path, default=REPO/"shared/testcases/workout-feedback.json")
    evaluate.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    protocol_hash = hashlib.sha256(PROTOCOL.read_bytes()).hexdigest()
    if args.command == "extract":
        extract(args, protocol_hash)
    else:
        report = json.loads(args.report.read_text(encoding="utf-8"))
        feedback = json.loads(args.feedback.read_text(encoding="utf-8"))
        result = audit(report, feedback, protocol_hash)
        sampling = load_module("sampling", Path(__file__).with_name("research-workout-beats.py"))
        sampling.write_report(args.output, result)
        print(json.dumps({name: {k: v for k, v in summary.items() if k != "records"}
                          for name, summary in result["representations"].items()}))


if __name__ == "__main__":
    main()
