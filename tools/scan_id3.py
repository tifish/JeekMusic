"""统计一个目录下 MP3 的 ID3 标签情况，用来挑选测试素材、校准设计中的假设。

只读文件，不修改。只依赖标准库。

用法：python -I tools/scan_id3.py <目录> [--list mojibake|separator|lyrics|untagged]
"""

import collections
import os
import re
import sys

FRAMES = {"TIT2", "TPE1", "TPE2", "TALB", "USLT", "SYLT"}
SEPARATORS = {
    ";": re.compile(r";"),
    "/": re.compile(r"/"),
    "、": re.compile(r"、"),
    "&": re.compile(r"&"),
    ",": re.compile(r","),
    "，": re.compile(r"，"),
    " feat.": re.compile(r"\s(feat|ft)\.?\s", re.I),
    " x ": re.compile(r"\sx\s", re.I),
}


def synchsafe(b):
    return (b[0] << 21) | (b[1] << 14) | (b[2] << 7) | b[3]


def read_id3(path):
    """返回 (版本, {帧ID: [(编码字节, 原始数据)]})；没有 ID3v2 时版本为 None。"""
    with open(path, "rb") as f:
        header = f.read(10)
        if len(header) < 10 or header[:3] != b"ID3":
            return None, {}
        major = header[3]
        size = synchsafe(header[6:10])
        data = f.read(size)
    frames = collections.defaultdict(list)
    pos = 0
    while pos + 10 <= len(data):
        fid = data[pos : pos + 4]
        if not re.fullmatch(rb"[A-Z0-9]{4}", fid):
            break
        raw = data[pos + 4 : pos + 8]
        fsize = synchsafe(raw) if major == 4 else int.from_bytes(raw, "big")
        body = data[pos + 10 : pos + 10 + fsize]
        pos += 10 + fsize
        name = fid.decode()
        if name in FRAMES and body:
            frames[name].append((body[0], body[1:]))
    return major, frames


def decode_text(enc, raw):
    try:
        if enc == 0:
            return raw.decode("latin-1").rstrip("\0")
        if enc == 1:
            return raw.decode("utf-16").rstrip("\0")
        if enc == 2:
            return raw.decode("utf-16-be").rstrip("\0")
        return raw.decode("utf-8").rstrip("\0")
    except UnicodeDecodeError:
        return None


def main():
    root = sys.argv[1]
    list_kind = sys.argv[3] if len(sys.argv) > 3 and sys.argv[2] == "--list" else None
    stats = collections.Counter()
    separators = collections.Counter()
    listed = []

    for dirpath, _, files in os.walk(root):
        for name in files:
            if not name.lower().endswith(".mp3"):
                continue
            path = os.path.join(dirpath, name)
            stats["mp3"] += 1
            major, frames = read_id3(path)
            stats[f"ID3v2.{major}" if major else "无 ID3v2"] += 1

            title = frames.get("TIT2")
            artist = frames.get("TPE1")
            if not title and not artist:
                stats["无标题和艺术家"] += 1
                if list_kind == "untagged":
                    listed.append(name)

            mojibake = False
            for fid in ("TIT2", "TPE1", "TALB"):
                for enc, raw in frames.get(fid, []):
                    stats[f"{fid} 编码 {enc}"] += 1
                    if enc == 0 and any(b >= 0x80 for b in raw):
                        mojibake = True
            if mojibake:
                stats["Latin-1 帧含高字节（疑似 GBK）"] += 1
                if list_kind == "mojibake":
                    listed.append(name)

            for enc, raw in artist or []:
                text = decode_text(enc, raw)
                if text is None:
                    continue
                if "\0" in text:
                    stats["多值 TPE1"] += 1
                hit = [k for k, p in SEPARATORS.items() if p.search(text)]
                for k in hit:
                    separators[k] += 1
                if hit and list_kind == "separator":
                    listed.append(f"{text}    ({name})")

            if "USLT" in frames or "SYLT" in frames:
                stats["内嵌歌词"] += 1
                if list_kind == "lyrics":
                    listed.append(name)

    for key, value in sorted(stats.items()):
        print(f"{key}: {value}")
    print("TPE1 中出现的分隔符：", dict(separators.most_common()))
    for item in listed:
        print("  ", item)


if __name__ == "__main__":
    main()
