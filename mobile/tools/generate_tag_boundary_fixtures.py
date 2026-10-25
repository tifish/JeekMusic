"""Extend the copied shared synthetic corpus. Reads/writes only androidTest/assets.

Requires FFmpeg. No real music inputs or network calls. Run after copying the shared corpus.
"""
from pathlib import Path
import base64
import hashlib
import json
import struct
import subprocess

ASSETS = Path(__file__).resolve().parents[1] / "app/src/androidTest/assets"
CORPUS = ASSETS / "corpus"
OUT = ASSETS / "boundaries"
OUT.mkdir(exist_ok=True)


def be(n): return struct.pack(">I", n)
def le(n): return struct.pack("<I", n)
def sync(n): return bytes([n >> 21 & 127, n >> 14 & 127, n >> 7 & 127, n & 127])
def size(b): return sum(v << (21 - i * 7) for i, v in enumerate(b))
def frame(key, body, version): return key.encode() + (sync(len(body)) if version == 4 else be(len(body))) + b"\0\0" + body
def picture(role=4):
    mime = b"image/jpeg"
    description = "背面 Café Ελληνικά العربية 𠮷😀?".encode()
    data = (CORPUS / "back.jpg").read_bytes()
    return be(role) + be(len(mime)) + mime + be(len(description)) + description + be(12) + be(12) + be(24) + be(0) + be(len(data)) + data


for version in (3, 4):
    source = (CORPUS / f"id3v2{version}.mp3").read_bytes()
    end = 10 + size(source[6:10])
    def text(value): return b"\x01\xff\xfe" + value.encode("utf-16-le") if version == 3 else b"\x03" + value.encode()
    extra = frame("XABC", b"unknown\x00\xff\x81private", version)
    extra += frame("GEOB", b"\0application/octet-stream\0private.bin\0keep\0\xff\0\x81", version)
    for key, value in ([("TDAT", "2510"), ("TIME", "1234"), ("TORY", "1999"), ("IPLS", "producer\0制作人")] if version == 3 else [("TDRC", "2001-10-25T12:34:56"), ("TDOR", "1999-12")]):
        extra += frame(key, text(value), version)
    for role in (0, 3, 99):
        encoded = text("角色描述 Café 𠮷😀?")
        extra += frame("APIC", encoded[:1] + b"image/jpeg\0" + bytes([role]) + encoded[1:] + (b"\0\0" if version == 3 else b"\0") + (CORPUS / "back.jpg").read_bytes(), version)
    body = source[10:end] + extra
    (OUT / f"private-v2{version}.mp3").write_bytes(source[:6] + sync(len(body)) + body + source[end:])
    if version == 4:
        # Minimal synchronized lyrics, deliberately ambiguous with the existing USLT slot.
        sylt = frame("SYLT", b"\x03eng\x02\x01\0Synthetic\0" + be(123), 4)
        (OUT / "ambiguous-sylt.mp3").write_bytes(source[:6] + sync(len(body + sylt)) + body + sylt + source[end:])
        toc = frame("CTOC", b"toc\0\x03\x01chapter\0" + frame("TIT2", b"\x03Synthetic table", 4), 4)
        nested = frame("TIT2", b"\x03Chapter caf\xc3\xa9", 4)
        placeholder = frame("CHAP", b"chapter\0" + be(0) + be(300) + be(0) + be(0) + nested, 4)
        new_end = 10 + len(body + toc + placeholder)
        chapter = frame("CHAP", b"chapter\0" + be(0) + be(300) + be(new_end) + be(new_end + len(source[end:]) - 128) + nested, 4)
        (OUT / "unsafe-chapters.mp3").write_bytes(source[:6] + sync(len(body + toc + chapter)) + body + toc + chapter + source[end:])


def comments_extend(data, tail=False, pictures=True):
    vendor_length = struct.unpack_from("<I", data)[0]
    p = 4 + vendor_length
    count = struct.unpack_from("<I", data, p)[0]
    q = p + 4
    for _ in range(count):
        n = struct.unpack_from("<I", data, q)[0]
        q += 4 + n
    assert q == len(data)
    fields = ["ARTIST=Artist 二", "ARTIST=", "X_PRIVATE=保留原字段? 𠮷😀", "X_EMPTY=", "DATE=2001-10-25T12:34:56"]
    if pictures: fields += ["METADATA_BLOCK_PICTURE=" + base64.b64encode(picture(role)).decode() for role in (0, 3, 4, 99)]
    values = [v.encode() for v in fields]
    return data[:p] + le(count + len(values)) + data[p + 4:] + b"".join(le(len(v)) + v for v in values) + (b"\x01jeek.private.extension\x00\xff" if tail else b"")


source = (CORPUS / "native.flac").read_bytes()
blocks, p, last = [], 4, False
while not last:
    kind = source[p] & 127
    last = bool(source[p] & 128)
    n = int.from_bytes(source[p + 1:p + 4], "big")
    data = source[p + 4:p + 4 + n]
    if kind == 4:
        # FLAC pictures are native blocks, not comment fields.
        data = comments_extend(data, pictures=False)
    blocks.append((kind, data))
    p += 4 + n
blocks += [(2, b"JEEK\0\xffprivate application"), (42, b"\x00\xffunknown-block"), (6, picture(4)), (6, picture(99)), (6, picture(0))]
output = b"fLaC" + b"".join(bytes([kind | (128 if i == len(blocks) - 1 else 0)]) + len(data).to_bytes(3, "big") + data for i, (kind, data) in enumerate(blocks)) + source[p:]
(OUT / "private.flac").write_bytes(output)


def crc_page(raw):
    raw = bytearray(raw)
    raw[22:26] = b"\0" * 4
    crc = 0
    for v in raw:
        crc ^= v << 24
        for _ in range(8):
            crc = ((crc << 1) ^ (0x04C11DB7 if crc & 0x80000000 else 0)) & 0xffffffff
    raw[22:26] = le(crc)
    return bytes(raw)


for extension in ("ogg", "opus"):
    source = (CORPUS / f"native.{extension}").read_bytes()
    header_count = 3 if extension == "ogg" else 2
    pages, packets, partial, p = [], [], bytearray(), 0
    while len(packets) < header_count:
        count = source[p + 26]
        lacing = source[p + 27:p + 27 + count]
        q = p + 27 + count
        for n in lacing:
            partial.extend(source[q:q + n]); q += n
            if n < 255:
                packets.append(bytes(partial)); partial.clear()
        pages.append(source[p:q]); p = q
    assert not partial and len(packets) == header_count
    prefix = 7 if extension == "ogg" else 8
    comment = packets[1][prefix:len(packets[1]) - (1 if extension == "ogg" else 0)]
    packets[1] = packets[1][:prefix] + comments_extend(comment, tail=extension == "opus") + (b"\x01" if extension == "ogg" else b"")
    serial = pages[0][14:18]
    def render_ogg(header_packets):
        rendered = []
        for i, packet in enumerate(header_packets):
            lacing = bytes([255] * (len(packet) // 255) + [len(packet) % 255])
            chunks = [lacing[j:j + 255] for j in range(0, len(lacing), 255)]
            position = 0
            for part, segments in enumerate(chunks):
                granule = b"\0" * 8 if part == len(chunks) - 1 else b"\xff" * 8
                flags = (2 if i == 0 and part == 0 else 0) | (1 if part else 0)
                header = b"OggS\0" + bytes([flags]) + granule + serial + le(len(rendered)) + b"\0" * 4 + bytes([len(segments)]) + segments
                n = sum(segments)
                rendered.append(crc_page(header + packet[position:position + n])); position += n
        at = p
        while at < len(source):
            count = source[at + 26]
            end = at + 27 + count + sum(source[at + 27:at + 27 + count])
            page = bytearray(source[at:end]); page[18:22] = le(len(rendered))
            rendered.append(crc_page(page)); at = end
        return b"".join(rendered)
    (OUT / f"private.{extension}").write_bytes(render_ogg(packets))
    large = packets.copy()
    data = large[1][prefix:len(large[1]) - (1 if extension == "ogg" else 0)]
    vendor_size = struct.unpack_from("<I", data)[0]
    q = 4 + vendor_size
    count = struct.unpack_from("<I", data, q)[0]; q += 4
    fields = []
    for _ in range(count):
        n = struct.unpack_from("<I", data, q)[0]; q += 4
        field = data[q:q + n]; q += n
        fields.append(b"LYRICS=" + b"A" * 70000 if field.upper().startswith(b"LYRICS=") else field)
    large[1] = packets[1][:prefix] + data[:4 + vendor_size] + le(count) + b"".join(le(len(f)) + f for f in fields) + data[q:] + (b"\x01" if extension == "ogg" else b"")
    (OUT / f"large.{extension}").write_bytes(render_ogg(large))
(OUT / "unsafe-chained.ogg").write_bytes((CORPUS / "native.ogg").read_bytes() * 2)


def atom(key, data): return be(len(data) + 8) + key.encode("latin1") + data
def atoms(data):
    p = 0
    while p < len(data):
        n = int.from_bytes(data[p:p + 4], "big")
        assert n >= 8
        yield data[p + 4:p + 8].decode("latin1"), data[p + 8:p + n]
        p += n
    assert p == len(data)
def modify_atoms(data, path, extra):
    return b"".join(atom(key, modify_atoms(body, path[1:], extra) if path and key == path[0] else body) for key, body in atoms(data)) + (extra if not path else b"")


source = (CORPUS / "native.m4a").read_bytes()
private = atom("----", atom("mean", b"\0" * 4 + b"org.jeekmusic.fixture") + atom("name", b"\0" * 4 + b"PRIVATE") + atom("data", be(1) + be(0) + "未知字段 𠮷😀?".encode()))
def edit_movie(data):
    result = []
    for key, body in atoms(data):
        if key == "udta":
            children = []
            for k, b in atoms(body):
                if k == "meta": b = b[:4] + modify_atoms(b[4:], ["ilst"], private + atom("Xabc", atom("data", be(0) + be(0) + b"\0\xffunknown item")))
                children.append(atom(k, b))
            body = b"".join(children)
        result.append(atom(key, body))
    return b"".join(result) + atom("uuid", bytes(range(16)) + b"private unknown movie atom")
(OUT / "private.m4a").write_bytes(b"".join(atom(key, edit_movie(body) if key == "moov" else body) for key, body in atoms(source)))
(OUT / "unsafe-saio.m4a").write_bytes(modify_atoms(source, ["moov", "trak", "mdia", "minf", "stbl"], atom("saio", be(0) + be(1) + be(64))))
for name, args in [("faststart-padded.m4a", ["-moov_size", "16384"]), ("unsafe-faststart.m4a", ["-movflags", "+faststart"])]:
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(CORPUS / "native.m4a"), "-map", "0", "-c", "copy", *args, str(OUT / name)], check=True, timeout=30)
subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(CORPUS / "native.m4a"), "-map", "0", "-c", "copy", "-encryption_scheme", "cenc-aes-ctr", "-encryption_key", "00" * 16, "-encryption_kid", "11" * 16, str(OUT / "unsafe-cenc.m4a")], check=True, timeout=30)

manifest = []
for path in sorted(OUT.iterdir()):
    if path.suffix not in {".mp3", ".flac", ".ogg", ".opus", ".m4a"}: continue
    # Validate generated envelopes independently before the Android tests receive them.
    key = ["-decryption_key", "00" * 16] if path.name == "unsafe-cenc.m4a" else []
    subprocess.run(["ffmpeg", "-v", "error", *key, "-i", str(path), "-map", "0:a:0", "-f", "null", "-"], check=True, timeout=30, capture_output=True)
    manifest.append(dict(path=path.name, sha256=hashlib.sha256(path.read_bytes()).hexdigest(), bytes=path.stat().st_size))
(OUT / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")
print(f"Generated {len(manifest)} independent synthetic boundary fixtures")
