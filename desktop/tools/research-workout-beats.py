"""Read-only isolated Beat This! experiment; never imports results into the library.

Requires the pinned research environment described in docs/workout-music-v8.md.
Manifest: JSON list of {track: {id, path, durationMs}, explanation: ...} from the
existing local production snapshot. Paths and decoded PCM never leave this process.
No labels are used to choose intervals, tempo octaves, or model outputs.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time


def write_report(path, report):
    """Keep a usable previous report if an experiment is interrupted mid-write."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(report, ensure_ascii=False), encoding="utf-8")
    temporary.replace(path)


def intervals(duration_ms, segments, context_seconds):
    """Production intervals, or longer continuous context around the same centres."""
    if not isinstance(duration_ms, int) or duration_ms <= 0:
        raise ValueError("A positive logical duration is required")
    result = []
    for s in segments:
        start, end = s["start_ms"], s["end_ms"]
        if not 0 <= start < end <= duration_ms:
            raise ValueError("Research manifest must use bounded non-CUE source intervals")
        if context_seconds is not None:
            length = min(duration_ms, round(context_seconds * 1000))
            start = max(0, min(duration_ms - length, (start + end - length) // 2))
            end = start + length
        if (start, end) not in result:
            result.append((start, end))
    return result


def decode(path, start, end, np):
    r = subprocess.run([
        "ffmpeg", "-v", "error", "-nostdin", "-ss", str(start / 1000),
        "-i", str(path), "-t", str((end - start) / 1000), "-vn", "-ac", "1",
        "-ar", "22050", "-f", "f32le", "pipe:1",
    ], capture_output=True, timeout=45, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if r.returncode:
        raise RuntimeError("Local ffmpeg decode failed; private stderr suppressed")
    pcm = np.frombuffer(r.stdout, dtype="<f4").copy()
    if len(pcm) < (end - start) * 22.05 - 2205 or not np.isfinite(pcm).all():
        raise ValueError("Truncated or non-finite local PCM")
    return pcm


def summarize(beats, downbeats, logits, np):
    ibi = np.diff(beats)
    bpm = float(60 / np.median(ibi)) if len(ibi) else None
    indices = np.clip(np.rint(beats * 50).astype(int), 0, len(logits) - 1)
    probabilities = 1 / (1 + np.exp(-np.clip(logits[indices], -50, 50)))
    return dict(
        bpm=bpm,
        intervalCv=float(np.std(ibi) / np.mean(ibi)) if len(ibi) else None,
        beatProbabilityMedian=float(np.median(probabilities)) if len(indices) else None,
        beats=beats.tolist(), downbeats=downbeats.tolist(), beatLogits=logits.tolist(),
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--checkpoint", required=True, help="Explicit local checkpoint, no implicit download")
    parser.add_argument("--context-seconds", type=float, choices=[10, 30])
    parser.add_argument("--threads", type=int, default=4, choices=range(1, 9))
    args = parser.parse_args()
    import numpy as np
    import psutil
    import torch
    from beat_this.inference import Audio2Frames
    from beat_this.model.postprocessor import Postprocessor
    checkpoint = Path(args.checkpoint).resolve(strict=True)
    torch.set_num_threads(args.threads)
    torch.set_num_interop_threads(1)
    begin = time.perf_counter()
    model = Audio2Frames(checkpoint_path=str(checkpoint), device="cpu", float16=False)
    post = Postprocessor(type="minimal")
    report = dict(contract="workout_beats_research_v1", cacheCompatible=False,
                  checkpointSha256=hashlib.sha256(checkpoint.read_bytes()).hexdigest(),
                  checkpointBytes=checkpoint.stat().st_size, torch=torch.__version__,
                  threads=args.threads, contextSeconds=args.context_seconds,
                  modelLoadMs=round((time.perf_counter() - begin) * 1000, 2), records=[])
    for row in json.loads(args.manifest.read_text(encoding="utf-8")):
        t = row["track"]
        if t.get("cueTrackNo") is not None:
            raise ValueError("This research adapter supports non-CUE recordings only")
        segments = row["explanation"]["evidence"]["segments"]
        record = dict(trackId=t["id"], segments=[])
        for start, end in intervals(t["durationMs"], segments, args.context_seconds):
            begin = time.perf_counter()
            pcm = decode(t["path"], start, end, np)
            decode_ms = (time.perf_counter() - begin) * 1000
            begin = time.perf_counter()
            beat, downbeat = model(pcm, 22050)
            beats, downbeats = post(beat, downbeat)
            s = summarize(beats, downbeats, beat.cpu().numpy(), np)
            s.update(startMs=start, endMs=end, decodeMs=round(decode_ms, 2),
                     inferMs=round((time.perf_counter() - begin) * 1000, 2))
            record["segments"].append(s)
        report["records"].append(record)
        report["peakWorkingSetBytes"] = psutil.Process(os.getpid()).memory_info().peak_wset
        write_report(args.output, report)
        print(json.dumps(dict(trackId=t["id"], bpm=[s["bpm"] for s in record["segments"]],
                              inferMs=round(sum(s["inferMs"] for s in record["segments"]), 2))), flush=True)


if __name__ == "__main__":
    main()
