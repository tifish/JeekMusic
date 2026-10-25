"""Independently inspect captured, synthetic pre-fix Android outputs (no device writes)."""
import argparse
import json
from pathlib import Path
from validate_native_tags import audio, digest

def atoms(data, start=0):
    p = start
    while p < len(data):
        n = int.from_bytes(data[p:p + 4], "big")
        assert 8 <= n <= len(data) - p
        yield data[p + 4:p + 8], data[p + 8:p + n]
        p += n
    assert p == len(data)

def locations(data):
    result = []
    for kind, body in atoms(data):
        if kind == b"iloc":
            assert body[:14] == bytes(4) + b"\x44\x00\x00\x01\x00\x01\x00\x00\x00\x01"
            result.append((int.from_bytes(body[14:18], "big"), int.from_bytes(body[18:22], "big")))
        elif kind in {b"moov", b"udta"}: result.extend(locations(body))
        elif kind == b"meta": result.extend(locations(body[4:]))
    return result

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--captured", required=True, type=Path)
    args = parser.parse_args()
    assets = Path(__file__).resolve().parents[1] / "app/src/androidTest/assets/review-boundaries"
    report = []
    for version in (3, 4):
        source = assets / f"unsafe-alter-v2{version}.mp3"
        raw = source.read_bytes()
        for operation in ("cover", "lyrics", "combined"):
            path = args.captured / f"review-{operation}-{source.name}"
            after = path.read_bytes()
            end = 10 + sum(v << (21 - 7 * i) for i, v in enumerate(after[6:10]))
            assert after[10:24] == raw[10:24]  # cached MPEG start 24 and invalidation flag unchanged
            assert int.from_bytes(after[20:24], "big") == 24 and end != 24
            assert after[end:] == raw[24:]
            assert digest(audio(path)) == digest(audio(source)) and digest(audio(path, True)) == digest(audio(source, True))
            report.append(dict(file=path.name, retainedStatus=after[18], cachedAudioStart=24, actualAudioStart=end,
                               sourceSha256=digest(raw), afterSha256=digest(after), audioAndPcmUnchanged=True))
    marker = b"NON_TARGET_ITEM_PAYLOAD_12345678"
    for operation in ("cover", "lyrics", "combined"):
        source = assets / "unsafe-iloc-udta.m4a"
        path = args.captured / f"review-{operation}-{source.name}"
        raw, after = source.read_bytes(), path.read_bytes()
        offset, length = locations(raw)[0]
        assert raw[offset:offset + length] == marker
        assert locations(after) == locations(raw) and after.count(marker) == 1
        assert after[offset:offset + length] != marker
        assert digest(audio(path)) == digest(audio(source)) and digest(audio(path, True)) == digest(audio(source, True))
        report.append(dict(file=path.name, itemOffset=offset, originalPayloadOffset=raw.index(marker),
                           afterPayloadOffset=after.index(marker), outputBytes=len(after), offsetStillResolves=False,
                           sourceSha256=digest(raw), afterSha256=digest(after), audioAndPcmUnchanged=True))
    (args.captured.parent / "review-independent-baseline.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"{len(report)} pre-fix Android outputs independently confirm invalid ID3/MP4 semantics despite unchanged audio/PCM")

if __name__ == "__main__": main()
