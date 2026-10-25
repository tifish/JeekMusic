"""Deterministic review regressions, using only the committed synthetic corpus."""
from pathlib import Path
import hashlib
import json
import struct

ASSETS = Path(__file__).resolve().parents[1] / "app/src/androidTest/assets"
OUT = ASSETS / "review-boundaries"
OUT.mkdir(exist_ok=True)
be = lambda n: struct.pack(">I", n)
sync = lambda n: bytes((n >> shift & 127 for shift in (21, 14, 7, 0)))
def atom(key, body): return be(len(body) + 8) + key.encode("latin1") + body
def atoms(data):
    p = 0
    while p < len(data):
        n = int.from_bytes(data[p:p + 4], "big")
        assert 8 <= n <= len(data) - p
        yield data[p + 4:p + 8].decode("latin1"), data[p + 8:p + n]
        p += n
    assert p == len(data)

source = (ASSETS / "corpus/native.m4a").read_bytes()
marker = b"NON_TARGET_ITEM_PAYLOAD_12345678"
def location(offset):
    # ISO item location v0: file offset, one item and one extent, 4-byte lengths.
    return atom("iloc", bytes(4) + b"\x44\x00\x00\x01\x00\x01\x00\x00\x00\x01" + be(offset) + be(len(marker)))
def build(path, offset, uuid=False):
    def descend(data, remaining):
        if not remaining:
            return data + (atom("uuid", bytes(range(16)) + marker) if uuid else location(offset) + atom("Xraw", marker))
        found = False
        result = []
        for key, body in atoms(data):
            if key == remaining[0]:
                found = True
                prefix = body[:4] if key == "meta" else b""
                body = prefix + descend(body[len(prefix):], remaining[1:])
            result.append(atom(key, body))
        if not found:
            assert remaining == ["meta"]
            result.append(atom("meta", bytes(4) + descend(b"", [])))
        return b"".join(result)
    return descend(source, path)

for name, path in [("udta", ["moov", "udta", "meta"]), ("movie", ["moov", "meta"]), ("top", ["meta"])]:
    raw = build(path, 0)
    raw = build(path, raw.index(marker))
    (OUT / f"unsafe-iloc-{name}.m4a").write_bytes(raw)
    (OUT / f"unsafe-uuid-{name}.m4a").write_bytes(build(path, 0, uuid=True))
# Names inside an opaque data payload are NOT boxes and must not trigger rejection.
raw = build(["moov", "udta", "meta"], 0, uuid=True)
# Replace the actual uuid box by a same-size unknown leaf, preserving fake names in payload.
raw = raw.replace(b"uuid" + bytes(range(16)), b"Xraw" + b"ilocsaioencauuid")
(OUT / "safe-opaque.m4a").write_bytes(raw)

audio = (ASSETS / "corpus/untagged.mp3").read_bytes()
for version, flag in [(3, 0x80), (4, 0x40)]:
    frame = b"XPOS" + (sync(4) if version == 4 else be(4)) + bytes([flag, 0]) + be(24)
    (OUT / f"unsafe-alter-v2{version}.mp3").write_bytes(b"ID3" + bytes([version, 0, 0]) + sync(len(frame)) + frame + audio)

manifest = [dict(path=p.name, sha256=hashlib.sha256(p.read_bytes()).hexdigest(), bytes=p.stat().st_size)
            for p in sorted(OUT.iterdir()) if p.suffix in {".m4a", ".mp3"}]
(OUT / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")
print(f"Generated {len(manifest)} review fixtures")
