"""从真实曲库中挑一个有代表性的子集，复制到暂存目录，供推送到模拟器测试用。

优先挑选边界情况：有 .lrc 的歌曲、非 MP3 格式、无标签、多艺术家、内嵌歌词、疑似 GBK 乱码；
剩余空间用随机的普通歌曲填满。只读取源目录，不修改。

用法：python -I tools/pick_library_sample.py <源目录> <暂存目录> [上限 MB，默认 1200]
"""

import os
import random
import re
import shutil
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from scan_id3 import SEPARATORS, decode_text, read_id3  # noqa: E402


def classify(path):
    """返回这首歌命中的边界情况标签。"""
    tags = set()
    ext = os.path.splitext(path)[1].lower()
    if ext != ".mp3":
        tags.add("format")
        return tags
    major, frames = read_id3(path)
    if not frames.get("TIT2") and not frames.get("TPE1"):
        tags.add("untagged")
    for fid in ("TIT2", "TPE1", "TALB"):
        for enc, raw in frames.get(fid, []):
            if enc == 0 and any(b >= 0x80 for b in raw):
                tags.add("mojibake")
    for enc, raw in frames.get("TPE1", []):
        text = decode_text(enc, raw) or ""
        if "\0" in text or any(p.search(text) for p in SEPARATORS.values()):
            tags.add("multi-artist")
    if "USLT" in frames or "SYLT" in frames:
        tags.add("lyrics")
    return tags


def main():
    src, dst = sys.argv[1], sys.argv[2]
    limit = int(sys.argv[3]) * 1024 * 1024 if len(sys.argv) > 3 else 1200 * 1024 * 1024
    random.seed(42)

    files = []
    for dirpath, _, names in os.walk(src):
        for name in names:
            files.append(os.path.join(dirpath, name))
    audio_exts = {".mp3", ".flac", ".ogg", ".m4a", ".wav", ".wma", ".ape", ".opus"}
    audio = [f for f in files if os.path.splitext(f)[1].lower() in audio_exts]
    lrc_stems = {os.path.splitext(f)[0] for f in files if f.lower().endswith(".lrc")}

    # 多艺术家的歌曲很多，单独这一类就可能超出上限，所以限制数量
    caps = {"multi-artist": 60}
    taken = {k: 0 for k in caps}
    picked, rest = [], []
    random.shuffle(audio)
    for f in audio:
        tags = classify(f)
        if os.path.splitext(f)[0] in lrc_stems:
            tags.add("lrc")
        capped = tags and all(t in caps and taken[t] >= caps[t] for t in tags)
        if tags and not capped:
            picked.append(f)
            for t in tags & caps.keys():
                taken[t] += 1
        else:
            rest.append(f)

    total = sum(os.path.getsize(f) for f in picked)
    for f in rest:
        size = os.path.getsize(f)
        if total + size > limit:
            continue
        picked.append(f)
        total += size

    if os.path.exists(dst):
        shutil.rmtree(dst)
    os.makedirs(dst)
    copied = 0
    for f in picked:
        rel = os.path.relpath(f, src)
        target = os.path.join(dst, rel)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        shutil.copy2(f, target)
        copied += 1
        stem = os.path.splitext(f)[0]
        if stem in lrc_stems:
            shutil.copy2(stem + ".lrc", os.path.splitext(target)[0] + ".lrc")
            copied += 1
    print(f"已复制 {copied} 个文件，共 {total / 1024 / 1024:.0f} MB → {dst}")


if __name__ == "__main__":
    main()
