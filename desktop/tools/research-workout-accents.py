"""Read-only accent hierarchy experiment, not a workout classifier or cache format.

Uses the same explicit local manifest as research-workout-pulse.py. Samples the
first three production interval centres; optional fixed gain/time perturbations.
No labels, titles, artists, automatic tags, octave selection or learned weights.
Research dependencies stay outside the application. Audio never leaves the host.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import subprocess
import time

spec = importlib.util.spec_from_file_location("pulse", Path(__file__).with_name("research-workout-pulse.py"))
pulse = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pulse)


def accent_measures(envelope, fps, candidates):
    """Describe attacks and alternating grid amplitudes; never infer human beats.

    Fixed 100ms minimum separation and 5% peak prominence remove duplicate local
    attacks. Quantiles describe sensitivity to attack strength, not tuned cutoffs.
    Equal strength ties are retained. Adjacent-onset intervals can be irregular
    and must not be reported as a perceived tempo.
    """
    import numpy as np
    from scipy.signal import find_peaks
    e = np.asarray(envelope, dtype=float)
    if e.ndim != 1 or not np.isfinite(e).all() or (e < 0).any() or not np.isfinite(fps) or fps < 8:
        raise ValueError("Expected finite nonnegative onset envelope and fps >= 8")
    if len(e) < 4 * fps:
        raise ValueError("At least four independent seconds required")
    indices, _ = find_peaks(e, distance=max(1, round(.1 * fps)), prominence=.05 * e.max())
    result = {}
    for q in (0, .5, .75):
        selected = indices[e[indices] >= np.quantile(e[indices], q)] if len(indices) else indices
        intervals = np.diff(selected) / fps
        result[str(q)] = dict(rate=len(selected) / (len(e) / fps),
            intervalBpm=60 / float(np.median(intervals)) if len(intervals) else None,
            cv=float(np.std(intervals) / np.mean(intervals)) if len(intervals) else None)
    if candidates and e.sum() > 0:
        # Same strongest candidate for every recording, including slow and endpoint
        # alternatives. Do not selectively double positive examples.
        bpm = candidates[0]["bpm"]
        if not 40 <= bpm <= 240:
            raise ValueError("Candidate outside measured 40-240 BPM range")
        period = 60 / bpm
        times = np.arange(len(e)) / fps
        z = np.dot(e, np.exp(-2j * np.pi * bpm / 60 * times))
        phase = (-np.angle(z) / (2 * np.pi)) % 1
        grid = np.arange(phase * period, len(e) / fps, period)
        values = np.array([e[max(0, round((t-.04)*fps)):min(len(e), round((t+.04)*fps)+1)].max(initial=0) for t in grid])
        if len(values) > 3:
            a, b = values[::2].mean(), values[1::2].mean()
            result["phase"] = dict(bpm=bpm, alternatingRatio=float(min(a, b) / max(a, b)) if max(a, b) else 0,
                coverage=float(np.mean(values >= np.quantile(e[indices], .5))) if len(indices) else 0)
    return result


def local_candidates(envelope, fps):
    """Fixed three blocks expose local/global phase cancellation; not a classifier."""
    import numpy as np
    return [sorted(pulse.pulse_candidates(chunk, fps), key=lambda c: c["product"], reverse=True)[:3]
            for chunk in np.array_split(envelope, 3)]


def attack_features(pcm, band, sr=11025):
    """Linear RMS rise, independent of HPSS/log spectral novelty; no model."""
    import numpy as np
    from scipy.ndimage import uniform_filter1d
    from scipy.signal import butter, sosfilt
    filtered = sosfilt(butter(4, [30, 150] if band == "bass" else [30, 5000],
        btype="bandpass", fs=sr, output="sos"), pcm)
    rms = np.sqrt(np.maximum(0, uniform_filter1d(filtered**2, size=round(.02*sr), mode="nearest")))[::128]
    attack = np.maximum(0, np.diff(rms))
    candidates = pulse.pulse_candidates(attack, sr / 128)
    peaks = sorted([c for i, c in enumerate(candidates) if c["product"] > 0
        and (i == 0 or c["product"] >= candidates[i-1]["product"])
        and (i == len(candidates)-1 or c["product"] > candidates[i+1]["product"])],
        key=lambda c: c["product"], reverse=True)[:8]
    return accent_measures(attack, sr / 128, peaks), peaks


def foreground_features(mix, percussive=None, sr=11025):
    """Relative attack energy and decay structure, not perceived driving/relaxation.

    Optional explicit percussion is for existing local separated-stem experiments;
    default is deterministic HPSS. All energy remains relative to the full mix.
    """
    import numpy as np
    import librosa
    from scipy.ndimage import median_filter
    from scipy.signal import find_peaks, peak_widths
    mix = np.asarray(mix)
    if sr != 11025 or mix.ndim != 1 or not sr*4 <= len(mix) <= sr*31 or not np.isfinite(mix).all():
        raise ValueError("Expected independent finite 4-31 second mono mix")
    if percussive is not None:
        percussive = np.asarray(percussive)
        if percussive.shape != mix.shape or not np.isfinite(percussive).all():
            raise ValueError("Percussion must share exact mix boundaries")
    hop, fft, fps = 128, 1024, sr / 128
    magnitude = np.abs(librosa.stft(mix, n_fft=fft, hop_length=hop))
    if percussive is None:
        _, percussion = librosa.decompose.hpss(magnitude)
    else:
        percussion = np.abs(librosa.stft(percussive, n_fft=fft, hop_length=hop))
    frequencies = librosa.fft_frequencies(sr=sr, n_fft=fft)
    bins = (frequencies >= 30) & (frequencies < 5000)
    total = np.sqrt((magnitude[bins]**2).sum(0))
    mean_mix, energy = total.mean(), (magnitude[bins]**2).sum()
    out = {}
    for name, low, high in [("bass",30,150), ("body",150,2000), ("high",2000,5000), ("all",30,5000)]:
        amplitude = np.sqrt((percussion[(frequencies >= low) & (frequencies < high)]**2).sum(0))
        excess = np.maximum(0, amplitude-median_filter(amplitude, size=43, mode="nearest"))
        indices, properties = find_peaks(amplitude, prominence=.1*np.quantile(amplitude,.95), distance=round(.2*fps))
        widths = peak_widths(amplitude,indices,rel_height=.5)[0]/fps if len(indices) else np.array([])
        strong = properties["prominences"] >= np.quantile(properties["prominences"],.5) if len(indices) else np.array([],dtype=bool)
        centered = amplitude-amplitude.mean()
        times = np.arange(len(amplitude))/fps
        modulation = [(bpm, float(2*abs(np.dot(centered,np.exp(-2j*np.pi*bpm/60*times)))/len(amplitude)/mean_mix))
            for bpm in range(40,241)] if mean_mix > 1e-12 else []
        out[name] = dict(energyRatio=float((amplitude**2).sum()/energy) if energy > 1e-12 else 0,
            punch=float(np.sqrt((excess**2).mean())/mean_mix) if mean_mix > 1e-12 else 0,
            rise=float(np.maximum(0,np.diff(amplitude)).mean()*fps/mean_mix) if mean_mix > 1e-12 else 0,
            modulation=sorted(modulation,key=lambda c:c[1],reverse=True)[:8],
            modulationSlow=max((v for bpm,v in modulation if bpm <= 120),default=0),
            widthMedian=float(np.median(widths[strong])) if strong.any() else None,
            peakProminence=float(np.median(properties["prominences"][strong])/mean_mix) if strong.any() and mean_mix > 1e-12 else 0,
            crest=float(np.quantile(amplitude,.95)/(amplitude.mean()+1e-12)))
    return out


def decode_exact(path, start, duration_ms, timeout):
    import numpy as np
    # ffmpeg's Ogg/resampler tail may vary by several ms. Decode an explicit extra
    # 100ms, then select exactly 30s. Never pad a short decode or include the tail
    # in features. This research adapter is NOT the production decoder.
    if start < 0 or start + 30100 > duration_ms:
        raise ValueError("Thirty-second context plus bounded decode tail must fit logical track")
    r = subprocess.run(["ffmpeg", "-v", "error", "-nostdin", "-ss", str(start/1000), "-i", path,
        "-t", "30.1", "-vn", "-ac", "1", "-ar", "11025", "-f", "f32le", "pipe:1"],
        capture_output=True, timeout=timeout, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if r.returncode or len(r.stdout) % 4:
        raise ValueError("Local decode failed (private stderr suppressed)")
    samples = np.frombuffer(r.stdout, dtype="<f4")
    if not 330750 <= len(samples) <= 332955 or not np.isfinite(samples).all():
        raise ValueError("Truncated, oversized or non-finite local decode")
    return samples[:330750].copy(), len(samples)


def main():
    import numpy as np
    import psutil
    from threadpoolctl import threadpool_limits
    threadpool_limits(limits=1)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--perturb", action="store_true", help="Fixed baseline, +/-2s and +/-6dB; no search")
    parser.add_argument("--foreground", action="store_true", help="Also measure relative HPSS attack energy/decay; no decision rule")
    parser.add_argument("--budget-seconds", type=int, default=300, choices=range(15, 901))
    args = parser.parse_args()
    rows = json.loads(args.manifest.read_text(encoding="utf-8"))
    if not isinstance(rows, list) or not 1 <= len(rows) <= 32:
        raise ValueError("Explicit manifest of 1-32 recordings required")
    report = dict(contract="accent_context_research_v1", cacheCompatible=False,
        state="incomplete", requestedRecordCount=len(rows), records=[])
    deadline = time.monotonic() + args.budget_seconds
    conditions = [(0, 0), (-2000, 0), (2000, 0), (0, -6), (0, 6)] if args.perturb else [(0, 0)]
    for row in rows:
        t = row["track"]
        if t.get("cueTrackNo") is not None:
            raise ValueError("Research adapter requires non-CUE source intervals")
        record = dict(trackId=t["id"], segments=[])
        contexts = pulse.sampling.intervals(t["durationMs"], row["explanation"]["evidence"]["segments"][:3], 30)
        for centre_start, _ in contexts:
            for offset, gain in conditions:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("Research budget exhausted; previous atomic report retained")
                start = max(0, min(t["durationMs"]-30100, centre_start+offset))
                begin = time.perf_counter()
                pcm, decoded = decode_exact(t["path"], start, t["durationMs"], min(45, remaining))
                pcm *= 10 ** (gain/20)
                bands = pulse.envelope_features(pcm, sr=11025)
                result = dict(startMs=start, endMs=start+30000, decodedSamples=decoded,
                    selectedSamples=len(pcm), offsetMs=offset, gainDb=gain, bands={})
                for band in ("bass", "all"):
                    e = bands[band]
                    attack, peaks = attack_features(pcm, band)
                    result["bands"][band] = dict(spectralAccent=accent_measures(e["envelope"], e["fps"], e["peaks"]),
                        peaks=e["peaks"], rmsAttack=attack, attackPeaks=peaks,
                        localCandidates=local_candidates(e["envelope"], e["fps"]))
                if args.foreground:
                    result["foreground"] = foreground_features(pcm)
                result["elapsedMs"] = (time.perf_counter()-begin)*1000
                record["segments"].append(result)
        if time.monotonic() >= deadline:
            raise TimeoutError("Research budget exhausted; previous atomic report retained")
        report["records"].append(record)
        report["peakWorkingSetBytes"] = psutil.Process().memory_info().peak_wset
        pulse.sampling.write_report(args.output, report)
        print(json.dumps(dict(trackId=t["id"], contexts=len(record["segments"]),
            seconds=round(sum(s["elapsedMs"] for s in record["segments"])/1000, 2))), flush=True)
    report["state"] = "completed"
    pulse.sampling.write_report(args.output, report)


if __name__ == "__main__":
    main()
