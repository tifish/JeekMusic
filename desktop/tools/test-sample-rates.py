"""Opt-in actual Windows WASAPI sample-rate checks with silent WAVs and isolated user data.

Changes the real endpoint mix format (and with --exclusive opens exclusive output).
Checks full original device/mix restoration at explicit pause and closes only its own test client.
Never restores an old snapshot over an unknown external format.
"""
import argparse
import csv
import importlib.util
import json
from pathlib import Path
import tempfile
import subprocess
import time
import wave

from mcp_client import DEFAULT_EXE, McpClient


def running_clients():
    result = subprocess.check_output(["tasklist", "/FI", "IMAGENAME eq JeekMusic.exe", "/FO", "CSV", "/NH"],
                                     text=True, encoding="utf-8", errors="replace")
    return [int(row[1]) for row in csv.reader(result.splitlines()) if row and row[0].casefold() == "jeekmusic.exe"]


def helper():
    spec = importlib.util.spec_from_file_location("sample_rate_mcp_helpers", Path(__file__).with_name("test-mcp.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, default=DEFAULT_EXE)
    parser.add_argument("--audio", action="store_true", help="Acknowledge actual endpoint format changes")
    parser.add_argument("--exclusive", action="store_true", help="Also exercise the single automatic exclusive mode")
    parser.add_argument("--results", type=Path, default=Path("desktop/artifacts/sample-rate-verification/wasapi.json"))
    args = parser.parse_args()
    if not args.audio:
        parser.error("--audio is required: silent files still occupy and modify the physical endpoint")
    active = running_clients()
    if active:
        parser.error(f"Close other JeekMusic clients before testing real formats (PIDs: {active}). No old-format restore is safe while another client is active.")
    helpers = helper()
    spec = importlib.util.spec_from_file_location("independent_windows_audio", Path(__file__).with_name("read-audio-format.py"))
    native = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(native)
    with native.AudioAccess() as access:
        original = access.capture()
    records = [{"independentBefore": native.public(original)}]
    player_pid = None
    client = None
    last_applied = None
    try:
        with tempfile.TemporaryDirectory(prefix="jeek-auto-format-") as temp:
            data, music = Path(temp) / "data", Path(temp) / "music"
            data.mkdir(); music.mkdir()
            (data / "settings.json").write_text(json.dumps({"closeAction": "Exit", "autoUpdateCheck": False, "muted": True}), encoding="utf-8")
            specs = [(48000, 16), (48000, 24), (44100, 24), (48000, 24), (44100, 16)]
            for index, (rate, bits) in enumerate(specs, 1):
                with wave.open(str(music / f"{index:02d}-{rate}-{bits}.wav"), "wb") as wav:
                    wav.setparams((2, bits // 8, rate, 0, "NONE", "not compressed"))
                    wav.writeframes(bytes(rate * 2 * (bits // 8) * 12))
            with McpClient(args.exe, data) as client:
                def call(name, **arguments):
                    return helpers.call(client, name, **arguments)
                def wait_format(rate, bits):
                    nonlocal last_applied
                    result = helpers.wait_for(client, "get_output", lambda value:
                        value["audioFormat"]["applied"] and value["audioFormat"]["actualDevice"] is not None and
                        value["audioFormat"]["actualDevice"]["sampleRate"] == rate and
                        value["audioFormat"]["actualDevice"]["validBits"] == bits)
                    with native.AudioAccess(original["endpointId"]) as access:
                        last_applied = access.capture()
                        independent = native.public(last_applied)
                    assert independent["device"]["sampleRate"] == rate and independent["device"]["validBits"] == bits, independent
                    assert independent["mix"]["sampleRate"] == rate, independent
                    assert result["format"]["deviceRate"] == rate, result
                    records.append({"mcp": result, "independent": independent})
                    return result
                def assert_baseline(reason):
                    output = call("get_output")
                    with native.AudioAccess(original["endpointId"]) as access:
                        current = access.capture()
                    assert current["device"] == original["device"] and current["mix"] == original["mix"], native.public(current)
                    assert not call("get_status")["playing"], output
                    assert not output["audioFormat"]["temporaryFormat"]["owners"], output
                    records.append({"baselineReason": reason, "independentBaseline": native.public(current), "mcp": output})
                player_pid = call("get_diagnostics")["processId"]
                try:
                    call("add_folder", path=str(music))
                    tracks = helpers.wait_for(client, "search_tracks", lambda value: value["total"] == len(specs))["items"]
                    ids = [track["id"] for track in sorted(tracks, key=lambda track: track["fileName"])]
                    call("set_output", exclusive=False, automaticFormat=True)
                    call("set_play_mode", shuffle=False, repeat="off")
                    call("play_tracks", trackIds=ids)
                    wait_format(48000, 16)
                    helpers.wait_for(client, "get_status", lambda value: value["playing"])
                    call("playback", action="pause")
                    paused = helpers.wait_for(client, "get_status", lambda value: not value["playing"])
                    assert_baseline("explicit-pause")
                    before = call("get_output")["audioFormat"]["actualDevice"]
                    call("set_output", automaticFormat=False)
                    assert call("get_output")["audioFormat"]["actualDevice"] == before
                    call("set_output", automaticFormat=True)
                    assert_baseline("auto-toggle-while-paused")
                    after = call("get_status")
                    assert not after["playing"] and abs(after["positionSeconds"] - paused["positionSeconds"]) < .3, (paused, after)
                    call("playback", action="resume")
                    resumed = helpers.wait_for(client, "get_status", lambda value: value["playing"])
                    assert abs(resumed["positionSeconds"] - paused["positionSeconds"]) < 1.5, (paused, resumed)
                    wait_format(48000, 16)
                    call("playback", action="seek", seconds=10)
                    helpers.wait_for(client, "get_status", lambda value: value["track"]["id"] == ids[1] and value["playing"])
                    wait_format(48000, 24)  # same rate, changed effective precision, natural EOF
                    for rate, bits in specs[2:]:
                        with native.AudioAccess(original["endpointId"]) as access:
                            exact_precision = access.supports(rate, bits) or bits == 24 and access.supports(rate, 24, 32)
                        expected_bits = bits if exact_precision else 16
                        call("playback", action="next")
                        result = wait_format(rate, expected_bits)
                        if expected_bits < bits:
                            assert "Precision conversion" in (result["audioFormat"]["conversion"] or ""), result
                    for _ in range(2):
                        call("playback", action="previous")  # restart current if >3s, then target fourth track
                        status = call("get_status")
                        if status["track"]["id"] == ids[4]:
                            call("playback", action="previous")
                        wait_format(48000, 24)
                        call("playback", action="next")
                        wait_format(44100, 16)
                    if args.exclusive:
                        call("set_output", exclusive=True)
                        time.sleep(.3)
                        call("playback", action="resume")
                        result = helpers.wait_for(client, "get_output", lambda value:
                            value["exclusive"] and value["audioFormat"]["applied"] and value["format"]["deviceRate"] == 44100)
                        assert result["audioFormat"]["actualDevice"]["validBits"] is None, result
                        assert result["audioFormat"]["automaticFormat"], result
                        records.append(result)
                        call("set_output", exclusive=False)
                        wait_format(44100, 16)
                    call("playback", action="seek", seconds=10)
                    helpers.wait_for(client, "get_status", lambda value: value["idle"] and not value["playing"])
                    assert_baseline("natural-queue-end")
                    call("playback", action="resume")
                    wait_format(44100, 16)
                    call("playback", action="pause")
                    assert_baseline("final-pause")
                    print("PASS: independent endpoint/mix readback; 48/16 -> 48/24 -> 44.1/source24 (driver-verified precision fallback) -> 48/24 -> 44.1/16; pause full-baseline restore/exact-position resume; paused auto toggle; repeated 44.1/48 transitions; natural end restoration; automatic exclusive/shared transition", flush=True)
                finally:
                    helpers.close_player(player_pid)
                    player_pid = None
            client = None
    finally:
        # Normal exit must restore itself. Only repair a failed test's still-owned exact
        # last application; never overwrite an unknown external edit with an old snapshot.
        if not running_clients():
            with native.AudioAccess(original["endpointId"]) as access:
                restored = access.capture()
                if (restored["device"], restored["mix"]) != (original["device"], original["mix"]):
                    if last_applied is not None and (restored["device"], restored["mix"]) == (last_applied["device"], last_applied["mix"]):
                        access.restore(original)
                        restored = access.capture()
                        records.append({"guardedFailureCleanup": True})
                    else:
                        records.append({"restoreSkipped": "Unknown external edit; refusing stale snapshot restoration.", "independentActual": native.public(restored)})
            records.append({"independentAfterExit": native.public(restored)})
        else:
            records.append({"restoreSkipped": "Another JeekMusic client is now active; refusing to overwrite its format."})
        args.results.parent.mkdir(parents=True, exist_ok=True)
        args.results.write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
