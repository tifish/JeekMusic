"""Run actual Android tag tests and independently verify fixed synthetic outputs.

Build/install the Debug APKs, allow all-files access, and grant Music/JeekTaglibNative
in the real folder picker first. Only this task's JeekTaglibNative35 emulator and Debug
app are used. The permission test temporarily denies/restores its all-files app-op.
Never installs/uninstalls, clears data, starts/stops devices or reads user music.
Requires adb, ffmpeg and ffprobe on PATH.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys


def command(args, timeout=30):
    return subprocess.run(args, check=True, capture_output=True, timeout=timeout).stdout


def digest(data): return hashlib.sha256(data).hexdigest()


def id3_protected(data, cover, lyrics):
    """Independent raw frame/tail snapshot, including unknown frames and exact ID3v1 bytes."""
    version, end, frames = 4, 0, []
    if data[:3] == b"ID3":
        version = data[3]
        end = 10 + sum(v << (21 - 7 * i) for i, v in enumerate(data[6:10]))
        p = 10
        while p < end and data[p]:
            raw_size = data[p + 4:p + 8]
            n = sum(v << (21 - 7 * i) for i, v in enumerate(raw_size)) if version == 4 else int.from_bytes(raw_size, "big")
            raw = data[p:p + 10 + n]
            assert len(raw) == 10 + n and p + len(raw) <= end
            key, body = raw[:4], raw[10:]
            target = lyrics and key == b"USLT"
            if cover and key == b"APIC":
                mime_end = body.index(0, 1)
                target = body[mime_end + 1] in (0, 3)
            if not target: frames.append((key.hex(), digest(raw)))
            p += len(raw)
    return version, frames, digest(data[end:])


def check_ogg_pages(data):
    """Check output CRCs and packet-completion granules independently of the Kotlin writer."""
    p, incomplete = 0, 0
    while p < len(data):
        assert data[p:p + 4] == b"OggS"
        count = data[p + 26]
        lacing = data[p + 27:p + 27 + count]
        end = p + 27 + count + sum(lacing)
        raw = bytearray(data[p:end])
        expected = raw[22:26]
        raw[22:26] = b"\0" * 4
        crc = 0
        for byte in raw:
            crc ^= byte << 24
            for _ in range(8): crc = ((crc << 1) ^ (0x04c11db7 if crc & 0x80000000 else 0)) & 0xffffffff
        assert crc.to_bytes(4, "little") == expected
        if lacing and all(v == 255 for v in lacing):
            assert data[p + 6:p + 14] == b"\xff" * 8, "Incomplete packet page granule must be -1"
            incomplete += 1
        p = end
    assert p == len(data)
    return incomplete


def probe(path):
    data = json.loads(command(["ffprobe", "-v", "error", "-show_entries", "format_tags:stream_tags:stream_disposition=attached_pic:stream=codec_type", "-of", "json", str(path)]))
    tags = {}
    for item in [data.get("format", {}), *[s for s in data.get("streams", []) if s.get("codec_type") == "audio"]]:
        tags.update({key.upper(): value for key, value in item.get("tags", {}).items()})
    if "COMMENT" not in tags and "DESCRIPTION" in tags: tags["COMMENT"] = tags["DESCRIPTION"]
    return tags, any(s.get("disposition", {}).get("attached_pic") for s in data.get("streams", []))


def audio(path, pcm=False):
    key = ["-decryption_key", "00" * 16] if "unsafe-cenc" in path.name else []
    return command(["ffmpeg", "-v", "error", *key, "-i", str(path), "-map", "0:a:0", "-c:a", "pcm_s16le" if pcm else "copy", "-f", "s16le" if pcm else "data", "-"])


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--collect-only", action="store_true")
    parser.add_argument("--contract-only", action="store_true", help="Run affected production/transaction/review tests, without repeating raw JNI characterization")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    mobile = Path(__file__).resolve().parents[1]
    assets = mobile / "app/src/androidTest/assets"
    output = (args.output or mobile / "build/android-taglib-validation").resolve()
    output.mkdir(parents=True, exist_ok=True)
    if not args.serial.startswith("emulator-"): raise SystemExit("A private emulator is required")
    adb = ["adb", "-s", args.serial]
    if command(adb + ["shell", "getprop", "ro.kernel.qemu"]).strip() != b"1": raise SystemExit("Not an emulator")
    if command(adb + ["emu", "avd", "name"]).splitlines()[0].strip() != b"JeekTaglibNative35": raise SystemExit("Select this task's isolated AVD")
    package = "com.tinyfish.jeekmusic.debug"
    runner = package + ".test/androidx.test.runner.AndroidJUnitRunner"
    def success(result, expected):
        return f"OK ({expected} {'test' if expected == 1 else 'tests'})".encode() in result and b"FAILURES!!!" not in result
    def instrument(classes, name, expected):
        try:
            result = command(adb + ["shell", "am", "instrument", "-w", "-r", "-e", "class", classes, runner], timeout=300)
        except subprocess.TimeoutExpired as error:
            (output / name).write_bytes(error.stdout or b"")
            raise
        (output / name).write_bytes(result)
        if not success(result, expected): raise SystemExit(f"Instrumentation failed: {name}")
    if not args.collect_only:
        classes = "com.tinyfish.jeekmusic.data.scanner.NativeTagContractTest,com.tinyfish.jeekmusic.data.scanner.NativeTagReviewTest"
        if not args.contract_only: classes = "com.tinyfish.jeekmusic.data.scanner.NativeTagWriteTest," + classes
        instrument(classes, "instrumentation.txt", 9 if args.contract_only else 13)
        if not args.contract_only:
            try:
                command(adb + ["shell", "appops", "set", "--uid", package, "MANAGE_EXTERNAL_STORAGE", "deny"])
                instrument("com.tinyfish.jeekmusic.data.scanner.NativeTagPermissionTest", "permission-instrumentation.txt", 1)
            finally:
                command(adb + ["shell", "appops", "set", "--uid", package, "MANAGE_EXTERNAL_STORAGE", "allow"])
    logs = [("instrumentation.txt", 9 if args.contract_only else 13)]
    if not args.contract_only: logs.append(("permission-instrumentation.txt", 1))
    for name, expected in logs:
        if not success((output / name).read_bytes(), expected): raise SystemExit(f"No complete successful instrumentation log: {name}")
    private = f"/sdcard/Android/data/{package}/files/native-tag-validation"
    reports = [("contract.json", "contract.json"), ("transactions.json", "transactions.json"), ("rename-permission.json", "rename-permission.json"), ("external-lock.json", "external-lock.json")]
    review = [("review-candidate.json", 9), ("review-mp4.json", 18), ("review-id3.json", 6), ("review-repository-sidecars.json", 3)]
    reports += [(name, name) for name, _ in review] + [("review-candidate-guard.json", "review-candidate-guard.json")]
    if not args.contract_only:
        reports += [("environment.json", "environment.json"), ("raw-false.json", "raw-bmp.json"), ("raw-true.json", "raw-supplementary.json"), ("production.json", "production-fixed.json")]
    for remote, name in reports:
        command(adb + ["pull", f"{private}/{remote}", str(output / name)])
    rows = [] if args.contract_only else json.loads((output / "production-fixed.json").read_text(encoding="utf-8"))
    contract = json.loads((output / "contract.json").read_text(encoding="utf-8"))
    if len(rows) != (0 if args.contract_only else 136) or len(contract) != 152 or any(r.get("exception") or r.get("mismatches") != [] for r in rows + contract): raise SystemExit("Production matrix incomplete/failed")
    for name, count in review:
        cases = json.loads((output / name).read_text(encoding="utf-8"))
        if len(cases) != count or not all(row["passed"] for row in cases): raise SystemExit(f"Review regression failed: {name}")
    if not json.loads((output / "review-candidate-guard.json").read_text())["passed"]: raise SystemExit("Candidate guard failed")
    transactions = json.loads((output / "transactions.json").read_text(encoding="utf-8"))
    if len(transactions) != 6 or not all(r["passed"] for r in transactions): raise SystemExit("Transaction checks incomplete")
    if not json.loads((output / "rename-permission.json").read_text())["passed"]: raise SystemExit("Rename permission test failed")
    if not json.loads((output / "external-lock.json").read_text())["passed"]: raise SystemExit("External process lock test failed")
    fixtures = {"corpus/" + f["path"] for f in json.loads((assets / "corpus/manifest.json").read_text(encoding="utf-8"))["files"]}
    fixtures |= {"boundaries/" + f["path"] for f in json.loads((assets / "boundaries/manifest.json").read_text())}
    fixtures |= {"review-boundaries/" + f["path"] for f in json.loads((assets / "review-boundaries/manifest.json").read_text())}
    files = output / "containers"
    files.mkdir(exist_ok=True)
    text = "[offset:-123]\r\n\r\n[00:01.234]合成歌词? Café Ελληνικά العربية 𠮷😀\r\n[00:01.234]Synthetic translation\r\n"
    cases = []
    for row in rows:
        if row["case"].startswith("container-"):
            _, ext, operation = row["case"].split("-")
            cases.append((row["case"] + "." + ext, assets / f"native-unicode.{ext}", operation != "lyrics" and ext != "wav", operation != "cover" and ext != "wav", text))
    for row in contract:
        fixture = row["fixture"] if "/" in row["fixture"] else "corpus/" + row["fixture"]
        if fixture not in fixtures: raise SystemExit("Unexpected synthetic fixture")
        target_text = "L" * 70000 if "/large." in fixture else (assets / "corpus/lyrics-after.lrc").read_bytes().decode("utf-8")
        cases.append((row["file"], assets / fixture, row["coverEmbedded"], row["lyricsEmbedded"], target_text))
    independent = []
    markers = [(assets / "corpus/opaque-marker.bin").read_bytes(), b"private unknown movie atom", b"\x00\xffunknown-block", b"JEEK\x00\xffprivate application", b"unknown\x00\xff\x81private", b"\x01jeek.private.extension\x00\xff"]
    for name, source, cover, lyrics, expected_text in cases:
        if not re.fullmatch(r"[a-zA-Z0-9._-]+", name): raise SystemExit("Unexpected output filename")
        destination = files / name
        command(adb + ["pull", f"/sdcard/Music/JeekTaglibNative/{name}", str(destination)])
        original_bytes, output_bytes = source.read_bytes(), destination.read_bytes()
        marker_counts = [original_bytes.count(marker) for marker in markers]
        if marker_counts != [output_bytes.count(marker) for marker in markers]: raise SystemExit(f"Opaque private binary record changed: {name}")
        if source.suffix == ".mp3" and id3_protected(original_bytes, cover, lyrics) != id3_protected(output_bytes, cover, lyrics): raise SystemExit(f"Raw non-target ID3 frame/audio/footer changed: {name}")
        incomplete = check_ogg_pages(output_bytes) if source.suffix in {".ogg", ".opus"} and "unsafe-chained" not in name else None
        actual = (digest(audio(destination)), digest(audio(destination, pcm=True)))
        baseline = (digest(audio(source)), digest(audio(source, pcm=True)))
        if actual != baseline: raise SystemExit(f"Encoded packets or PCM changed: {name}")
        old_tags, _ = probe(source)
        tags, picture = probe(destination)
        for key, value in old_tags.items():
            if lyrics and (key.startswith("LYRICS") or key in {"USLT", "UNSYNCEDLYRICS"}): continue
            if tags.get(key) != value: raise SystemExit(f"Independent metadata mismatch: {name} {key}: {tags.get(key)!r} != {value!r}")
        if lyrics and expected_text not in [v for k, v in tags.items() if k.startswith("LYRICS") or k in {"USLT", "UNSYNCEDLYRICS"}]: raise SystemExit(f"Exact lyric mismatch: {name}")
        if cover and not picture: raise SystemExit(f"Cover missing: {name}")
        if not cover and not lyrics and destination.read_bytes() != source.read_bytes(): raise SystemExit(f"Refused source changed: {name}")
        independent.append(dict(file=name, audioPacketsSha256=actual[0], pcmS16Sha256=actual[1], coverEmbedded=cover, lyricsEmbedded=lyrics, tags=tags, opaqueMarkerCounts=marker_counts, incompletePacketPages=incomplete))
    (output / "independent-containers.json").write_text(json.dumps(independent, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{len(rows) + len(contract)} production cases, {9 if args.contract_only else 13} Android methods" + ("" if args.contract_only else " + 1 permission method") + f", {len(independent)} independent FFmpeg packet/PCM checks passed. Artifacts: {output}")
    print("Raw JNI characterization was not rerun in --contract-only mode." if args.contract_only else "Raw 1.0.6 reports intentionally retain known footer/picture-description defects; production avoids those setters.")


if __name__ == "__main__": main()
