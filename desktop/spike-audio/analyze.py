"""比较 --test-gapless 的输出（testdata/out/*.raw）和原信号（testdata/gapless/reference.s16le）。

FLAC / CUE 是无损的，应该逐样本一致；MP3 有损，看两点：总长度对不对，分界处有没有错位
（在 ±2000 个样本内找让残差最小的偏移，应该是 0）和残差有没有明显变大。
"""
from pathlib import Path
import numpy as np

root = Path(__file__).resolve().parent / "testdata"
ref = np.frombuffer((root / "gapless" / "reference.s16le").read_bytes(), dtype="<i2").reshape(-1, 2).astype(np.float64) / 32768
BOUNDARIES = {"flac": [321_987, 654_321], "mp3": [321_987, 654_321], "cue": [6 * 44100, int(13.4 * 44100)]}


def load(raw: Path):
    fmt = raw.with_suffix(".fmt").read_text().strip()
    data = raw.read_bytes()
    if fmt == "s16":
        x = np.frombuffer(data, "<i2") / 32768
    elif fmt == "s32":
        x = np.frombuffer(data, "<i4") / 2147483648
    elif fmt in ("float", "floatp"):
        x = np.frombuffer(data, "<f4").astype(np.float64)
    else:
        raise ValueError(fmt)
    return fmt, x.reshape(-1, 2)


def best_lag(out, at, window=4096, search=2000):
    seg = ref[at - window // 2: at + window // 2, 0]
    best = (1e9, 0)
    for lag in range(-search, search + 1):
        a, b = at - window // 2 + lag, at + window // 2 + lag
        if a < 0 or b > len(out):
            continue
        err = np.mean((out[a:b, 0] - seg) ** 2)
        if err < best[0]:
            best = (err, lag)
    return best[1], best[0] ** 0.5


for raw in sorted((root / "out").glob("*.raw")):
    fmt, out = load(raw)
    kind = raw.stem.split("-")[0]
    n = min(len(out), len(ref))
    diff = np.abs(out[:n] - ref[:n])
    line = f"{raw.stem}: format {fmt}, {len(out)} samples ({len(out) - len(ref):+d} vs reference)"
    if kind in ("flac", "cue"):
        differing = int(np.count_nonzero(diff > 0.5 / 32768))
        line += f", differing samples {differing}"
        if differing == 0 and len(out) == len(ref):
            line += " -> bit-exact"
        else:
            first = int(np.argmax(diff.max(axis=1) > 0.5 / 32768)) if differing else None
            line += f", first difference at {first}"
    else:
        lags = [best_lag(out, b) for b in BOUNDARIES[kind]]
        line += f", lag at boundaries {[l for l, _ in lags]}, rms there {[round(e, 4) for _, e in lags]}"
        line += f", overall rms {np.sqrt(np.mean(diff ** 2)):.4f}, max {diff.max():.3f}"
    print(line)
