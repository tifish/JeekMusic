"""Read-only music-oriented HPSS/onset/beat baseline, without learned weights.

Not a workout classifier. Does not use labels, titles, legacy automatic tags,
or choose a different octave per recording. Writes only explicitly selected output.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import time

spec = importlib.util.spec_from_file_location("beat_experiment", Path(__file__).with_name("research-workout-beats.py"))
sampling = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sampling)


def extract(pcm, sr):
    import librosa
    import numpy as np
    hop = 256
    spect = librosa.stft(pcm, n_fft=2048, hop_length=hop)
    harmonic, percussive = librosa.decompose.hpss(spect)
    percussive_fraction = float(np.sum(abs(percussive) ** 2) / max(np.sum(abs(spect) ** 2), 1e-12))
    y = librosa.istft(percussive, hop_length=hop, length=len(pcm))
    onset = librosa.onset.onset_strength(y=y, sr=sr, hop_length=hop)
    tempo, beats = librosa.beat.beat_track(onset_envelope=onset, sr=sr, hop_length=hop)
    times = librosa.frames_to_time(beats, sr=sr, hop_length=hop)
    ibi = np.diff(times)
    return dict(percussiveFraction=percussive_fraction,
                percussiveBpm=float(np.asarray(tempo).ravel()[0]),
                beatIntervalCv=float(np.std(ibi) / np.mean(ibi)) if len(ibi) else None,
                onsetRate=len(librosa.onset.onset_detect(onset_envelope=onset, sr=sr, hop_length=hop)) / (len(pcm) / sr),
                rmsDb=float(10 * np.log10(max(np.mean(pcm * pcm), 1e-12))),
                beats=times.tolist())


def main():
    import numpy as np
    import librosa
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--context-seconds", type=int, choices=[10, 30], default=30)
    args = parser.parse_args()
    report = dict(contract="workout_hpss_research_v1", cacheCompatible=False, librosa=librosa.__version__, records=[])
    for row in json.loads(args.manifest.read_text(encoding="utf-8")):
        t = row["track"]
        record = dict(trackId=t["id"], segments=[])
        for start, end in sampling.intervals(t["durationMs"], row["explanation"]["evidence"]["segments"], args.context_seconds):
            begin = time.perf_counter()
            pcm = sampling.decode(t["path"], start, end, np)
            s = extract(pcm, 22050)
            s.update(startMs=start, endMs=end, elapsedMs=round((time.perf_counter() - begin) * 1000, 2))
            record["segments"].append(s)
        report["records"].append(record)
        sampling.write_report(args.output, report)
        print(json.dumps(dict(trackId=t["id"], segments=[{k: v for k, v in s.items() if k != "beats"} for s in record["segments"]])), flush=True)


if __name__ == "__main__":
    main()
