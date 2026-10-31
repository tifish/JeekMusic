"""Music pulse-level experiment on percussive/accompaniment onset envelopes.

Does not choose tempo octaves from labels. Fourier/ACF product describes measured
pulse salience, not perceived tempo or workout probability. No cache/tag writes.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import time

spec = importlib.util.spec_from_file_location("sampling", Path(__file__).with_name("research-workout-beats.py"))
sampling = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sampling)


def pulse_candidates(envelope, fps):
    import numpy as np
    envelope = np.asarray(envelope, dtype=float)
    if not np.isfinite(fps) or fps < 8 or envelope.ndim != 1 or not np.isfinite(envelope).all() or (envelope < 0).any():
        raise ValueError("Expected finite nonnegative mono onset envelope and fps >= 8")
    duration = len(envelope) / fps
    if duration < 4 or envelope.sum() <= 1e-9:
        return []
    candidates = []
    times = np.arange(len(envelope)) / fps
    for bpm in range(40, 241):
        period = 60 / bpm
        lag = int(round(period * fps))
        x, y = envelope[:-lag], envelope[lag:]
        x, y = x - x.mean(), y - y.mean()
        denominator = np.sqrt(np.sum(x*x) * np.sum(y*y))
        acf = float(np.dot(x, y) / denominator) if denominator > 1e-9 else 0
        fourier = float(abs(np.dot(envelope, np.exp(-2j*np.pi*bpm/60*times))) / envelope.sum())
        candidates.append(dict(bpm=bpm, correlation=acf, coherence=fourier, product=max(acf, 0)*fourier))
    return candidates


def envelope_features(y, sr=22050, hpss=True):
    import numpy as np
    import librosa
    from scipy.ndimage import uniform_filter1d
    if sr not in (11025, 22050):
        raise ValueError("Supported research rates: 11025 or 22050")
    hop, fft = (128, 1024) if sr == 11025 else (256, 2048)
    spect = librosa.stft(y, n_fft=fft, hop_length=hop)
    if hpss:
        _, spect = librosa.decompose.hpss(spect)
    magnitudes = np.abs(spect)
    freqs = librosa.fft_frequencies(sr=sr, n_fft=fft)
    output = {}
    for name, low, high in [("bass", 30, 150), ("mid", 150, 2000), ("high", 2000, 10000), ("all", 30, 10000)]:
        m = np.log1p(100 * magnitudes[(freqs >= low) & (freqs < high)])
        flux = np.maximum(0, np.diff(m, axis=1)).mean(axis=0)
        envelope = np.maximum(0, flux - uniform_filter1d(flux, size=43, mode="nearest"))
        candidates = pulse_candidates(envelope, sr/hop)
        peaks = [c for i, c in enumerate(candidates) if c["product"] > 0
                 and (i == 0 or c["product"] >= candidates[i-1]["product"])
                 and (i == len(candidates)-1 or c["product"] > candidates[i+1]["product"])]
        output[name] = dict(envelope=envelope.tolist(), fps=sr/hop,
                            peaks=sorted(peaks, key=lambda c: c["product"], reverse=True)[:8])
    return output


def main():
    import numpy as np
    from threadpoolctl import threadpool_limits
    threadpool_limits(limits=1)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--context-seconds", type=int, choices=[10, 30], default=30)
    parser.add_argument("--sample-rate", type=int, choices=[11025, 22050], default=22050)
    parser.add_argument("--offset-ms", type=int, choices=[-2000, 0, 2000], default=0)
    parser.add_argument("--gain-db", type=int, choices=[-6, 0, 6], default=0)
    args = parser.parse_args()
    report = dict(contract="workout_pulse_research_v1", cacheCompatible=False,
                  contextSeconds=args.context_seconds, sampleRate=args.sample_rate,
                  offsetMs=args.offset_ms, gainDb=args.gain_db, records=[])
    for row in json.loads(args.manifest.read_text(encoding="utf-8")):
        t = row["track"]
        record = dict(trackId=t["id"], segments=[])
        for start, end in sampling.intervals(t["durationMs"], row["explanation"]["evidence"]["segments"], args.context_seconds):
            length = end-start
            start = max(0, min(t["durationMs"]-length, start+args.offset_ms))
            end = start+length
            begin = time.perf_counter()
            pcm = sampling.decode(t["path"], start, end, np)
            pcm *= 10 ** (args.gain_db / 20)
            if args.sample_rate == 11025:
                import librosa
                pcm = librosa.resample(pcm, orig_sr=22050, target_sr=11025)
            features = envelope_features(pcm, sr=args.sample_rate)
            record["segments"].append(dict(startMs=start, endMs=end, bands=features,
                                           elapsedMs=(time.perf_counter()-begin)*1000))
        report["records"].append(record)
        sampling.write_report(args.output, report)
        print(json.dumps(dict(trackId=t["id"], peaks=[{b: [(c['bpm'], round(c['product'], 3)) for c in f['peaks'][:3]] for b, f in s['bands'].items()} for s in record['segments']])), flush=True)


if __name__ == "__main__":
    main()
