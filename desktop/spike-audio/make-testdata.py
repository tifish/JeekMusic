"""生成播放内核验证用的测试音频（输出到 testdata/，需要 ffmpeg 和 numpy）。

gapless/  一段 20 秒的连续信号（扫频 + 两个正弦，立体声左右不同），在不对齐任何编码帧的
          采样位置切成 3 段，分别编码成 FLAC 和 MP3。无缝播放时把 3 段接起来应该和原信号一样
          （FLAC 逐样本一致，MP3 有损但不应该有错位或空隙）。
cue/      同一信号的整轨 FLAC + CUE（分轨点在 1/75 秒的整数倍上，CUE 的时间精度）
formats/  3 秒静音，44.1k/16、48k/24、96k/24、192k/24：测独占模式下设备实际用的格式，不会出声
load/     30 秒低电平噪声的 96k/24 FLAC（解码最费 CPU 的内容），测 CPU 占用
long/     60 秒静音 96k/24 FLAC：真实设备输出时的 CPU 占用（不出声）
"""
import subprocess, sys
from pathlib import Path
import numpy as np

root = Path(__file__).resolve().parent / "testdata"
SR = 44100


def ffmpeg(*args):
    subprocess.run(["ffmpeg", "-v", "error", "-y", *map(str, args)], check=True)


def signal(seconds, sr=SR):
    t = np.arange(int(seconds * sr)) / sr
    sweep = np.sin(2 * np.pi * (100 * t + (2000 - 100) / (2 * seconds) * t * t))  # 100Hz → 2kHz 线性扫频
    left = 0.4 * sweep + 0.2 * np.sin(2 * np.pi * 441.7 * t)
    right = 0.4 * sweep + 0.2 * np.sin(2 * np.pi * 997.3 * t)
    return (np.stack([left, right], axis=1) * 32767).round().astype(np.int16)


def write_raw(path, pcm):
    path.write_bytes(pcm.tobytes())


def encode(raw, out, sr, extra):
    ffmpeg("-f", "s16le", "-ar", sr, "-ac", 2, "-i", raw, *extra, out)


def main():
    pcm = signal(20)
    cuts = [321_987, 654_321]  # 不是 MP3（1152）、FLAC（4096）帧长的整数倍
    parts = np.split(pcm, cuts)

    g = root / "gapless"; g.mkdir(parents=True, exist_ok=True)
    write_raw(g / "reference.s16le", pcm)
    for i, part in enumerate(parts, 1):
        raw = g / f"part{i}.s16le"
        write_raw(raw, part)
        encode(raw, g / f"part{i}.flac", SR, [])
        encode(raw, g / f"part{i}.mp3", SR, ["-c:a", "libmp3lame", "-b:a", "256k"])
        raw.unlink()

    c = root / "cue"; c.mkdir(parents=True, exist_ok=True)
    raw = c / "album.s16le"; write_raw(raw, pcm)
    encode(raw, c / "album.flac", SR, []); raw.unlink()
    (c / "album.cue").write_text(
        'PERFORMER "Test"\nTITLE "CUE Test"\nFILE "album.flac" WAVE\n'
        '  TRACK 01 AUDIO\n    TITLE "One"\n    INDEX 01 00:00:00\n'
        '  TRACK 02 AUDIO\n    TITLE "Two"\n    INDEX 01 00:06:00\n'      # 6.0 秒
        '  TRACK 03 AUDIO\n    TITLE "Three"\n    INDEX 01 00:13:30\n',   # 13.4 秒（30/75）
        encoding="utf-8")

    f = root / "formats"; f.mkdir(parents=True, exist_ok=True)
    for sr, bits in [(44100, 16), (48000, 24), (96000, 24), (192000, 24)]:
        fmt = ["-sample_fmt", "s16"] if bits == 16 else ["-sample_fmt", "s32", "-bits_per_raw_sample", "24"]
        ffmpeg("-f", "lavfi", "-i", f"anullsrc=r={sr}:cl=stereo", "-t", 3, *fmt, f / f"silence-{sr // 1000 if sr % 1000 == 0 else sr / 1000}k-{bits}.flac")

    l = root / "load"; l.mkdir(parents=True, exist_ok=True)
    ffmpeg("-f", "lavfi", "-i", "anoisesrc=r=96000:a=0.01:c=pink", "-ac", 2, "-t", 30,
           "-sample_fmt", "s32", "-bits_per_raw_sample", 24, l / "noise-96k-24.flac")

    lo = root / "long"; lo.mkdir(parents=True, exist_ok=True)
    ffmpeg("-f", "lavfi", "-i", "anullsrc=r=96000:cl=stereo", "-t", 60,
           "-sample_fmt", "s32", "-bits_per_raw_sample", 24, lo / "silence-96k-24-60s.flac")

    for p in sorted(root.rglob("*")):
        if p.is_file(): print(p.relative_to(root), p.stat().st_size)


if __name__ == "__main__":
    sys.exit(main())
