"""Bounded local reference inference with the original author's musicnn checkpoints.

No classifier, library/cache/tag writes, implicit downloads, audio files or uploads.
Author source/checkpoints must be supplied explicitly (repository ISC licence).
This is not Essentia's differently licensed weights or a third-party conversion.
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

COMMIT = "516acb2a0ff5ef73f64547898e018e793152c506"
SOURCE_HASHES = {
    "configuration.py": "00fd595eb16bad3a605e10bb6eaf0152cb8125bed6e2c884a636f679ae100419",
    "models.py": "ca0a4e64b33fa79ef716d56188202cfdb31c123fa5721addb1e295405b671cf8",
    "extractor.py": "a634059ade166c3b7379c4243bc0728c060d564ca498676af755a1e37523288e",
}
WEIGHT_HASHES = {
    "MTT_musicnn": "041f3935e981a65cea739842d30c188d3a7d2adf4410955c8668eb3de4a5acab",
    "MSD_musicnn": "8579734ee6388f799ed0def4bbd0d1fdcbeaf213ef2a9dc860917bd76a303de6",
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest",type=Path)
    parser.add_argument("--author-root",required=True,type=Path)
    parser.add_argument("--output",required=True,type=Path)
    parser.add_argument("--model",choices=list(WEIGHT_HASHES),default="MTT_musicnn")
    parser.add_argument("--normalize-rms",action="store_true",help="One fixed reference RMS=.1; not a level search")
    parser.add_argument("--gain-db",type=int,choices=[-6,0,6],default=0)
    parser.add_argument("--offset-ms",type=int,choices=[-2000,0,2000],default=0)
    parser.add_argument("--budget-seconds",type=int,choices=range(15,901),default=300)
    args = parser.parse_args()
    root = args.author_root.resolve()
    for filename, expected in SOURCE_HASHES.items():
        if hashlib.sha256((root/"musicnn"/filename).read_bytes()).hexdigest() != expected:
            raise ValueError("Author source identity mismatch")
    weight = root/"musicnn"/args.model/".data-00000-of-00001"
    if hashlib.sha256(weight.read_bytes()).hexdigest() != WEIGHT_HASHES[args.model]:
        raise ValueError("Original checkpoint identity mismatch")
    rows = json.loads(args.manifest.read_text(encoding="utf-8"))
    if not isinstance(rows,list) or not 1 <= len(rows) <= 32:
        raise ValueError("Explicit 1-32 recording manifest required")
    deadline = time.monotonic()+args.budget_seconds
    os.environ["TF_CPP_MIN_LOG_LEVEL"] = "2"
    os.environ["TF_ENABLE_ONEDNN_OPTS"] = "0"
    os.environ["OMP_NUM_THREADS"] = "2"
    import numpy as np
    import psutil
    import soundfile as sf
    import tensorflow as tf
    from threadpoolctl import threadpool_limits
    threadpool_limits(limits=1)
    sys.path.insert(0,str(root))
    from musicnn import models, extractor, configuration
    tf.compat.v1.disable_eager_execution()
    labels = configuration.MTT_LABELS if args.model == "MTT_musicnn" else configuration.MSD_LABELS
    with tf.name_scope("model"):
        x = tf.compat.v1.placeholder(tf.float32,[None,187,96])
        training = tf.compat.v1.placeholder(tf.bool)
        output = tf.nn.sigmoid(models.define_model(x,training,args.model,50)[0])
    saver = tf.compat.v1.train.Saver()
    spec = importlib.util.spec_from_file_location("sampling",Path(__file__).with_name("research-workout-beats.py"))
    sampling = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(sampling)
    report = dict(contract="original_musicnn_research_v1",cacheCompatible=False,state="incomplete",
        requestedRecordCount=len(rows),sourceCommit=COMMIT,model=args.model,
        checkpointSha256=WEIGHT_HASHES[args.model],checkpointBytes=weight.stat().st_size,
        normalizeRms=.1 if args.normalize_rms else None,gainDb=args.gain_db,offsetMs=args.offset_ms,
        tags=labels,records=[],tensorflow=tf.__version__,numpy=np.__version__)
    with tf.compat.v1.Session(config=tf.compat.v1.ConfigProto(intra_op_parallelism_threads=2,inter_op_parallelism_threads=1)) as session:
        begin = time.perf_counter()
        saver.restore(session,str(weight.parent)+"/")
        report["modelLoadSeconds"] = time.perf_counter()-begin
        for row in rows:
            t = row["track"]
            if t.get("cueTrackNo") is not None:
                raise ValueError("Explicit non-CUE source intervals required")
            contexts = sampling.intervals(t["durationMs"],row["explanation"]["evidence"]["segments"][:3],30)
            record = dict(trackId=t["id"],segments=[])
            for centre_start, _ in contexts:
                remaining = deadline-time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("Research budget exhausted")
                start = max(0,min(t["durationMs"]-30100,centre_start+args.offset_ms))
                if start < 0 or start+30100 > t["durationMs"]:
                    raise ValueError("Bounded 30s context and decode tail must fit source")
                begin = time.perf_counter()
                decoded = subprocess.run(["ffmpeg","-v","error","-nostdin","-ss",str(start/1000),"-i",t["path"],
                    "-t","30.1","-vn","-ac","1","-ar","16000","-f","f32le","pipe:1"],
                    capture_output=True,timeout=min(45,remaining),creationflags=getattr(subprocess,"CREATE_NO_WINDOW",0))
                if decoded.returncode or len(decoded.stdout)%4 or not 480000 <= len(decoded.stdout)//4 <= 483200:
                    raise ValueError("Local decode failed/truncated/oversized; private stderr suppressed")
                pcm = np.frombuffer(decoded.stdout,dtype="<f4")[:480000].copy()
                if not np.isfinite(pcm).all():
                    raise ValueError("Non-finite local decode")
                pcm *= 10**(args.gain_db/20)
                rms = float(np.sqrt(np.mean(pcm.astype(float)**2)))
                if args.normalize_rms and rms > 1e-9:
                    pcm *= .1/rms
                audio = io.BytesIO()
                sf.write(audio,pcm,16000,format="WAV",subtype="FLOAT")
                audio.seek(0)
                # Author's exact batch_data: mel -> float16 -> log10 compression;
                # no private audio file or full-track decode to feed that function.
                batch, _ = extractor.batch_data(audio,187,187)
                if batch.shape != (10,187,96) or not np.isfinite(batch).all():
                    raise ValueError("Author input contract/non-finite mel")
                values = []
                inference = time.perf_counter()
                for window in batch:
                    if time.monotonic() >= deadline:
                        raise TimeoutError("Research budget exhausted")
                    values.append(session.run(output,feed_dict={x:window[None],training:False})[0].tolist())
                record["segments"].append(dict(startMs=start,inputRms=rms,rmsNormalized=args.normalize_rms,
                    outputs=values,mean=np.mean(values,axis=0).tolist(),inferSeconds=time.perf_counter()-inference,
                    totalSeconds=time.perf_counter()-begin))
            if time.monotonic() >= deadline:
                raise TimeoutError("Research budget exhausted")
            report["records"].append(record)
            report["peakWorkingSetBytes"] = psutil.Process().memory_info().peak_wset
            sampling.write_report(args.output,report)
            print(json.dumps(dict(trackId=t["id"],segments=len(record["segments"]))),flush=True)
    report["state"] = "completed"
    sampling.write_report(args.output,report)


if __name__ == "__main__":
    main()
