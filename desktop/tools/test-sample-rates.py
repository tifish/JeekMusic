"""Opt-in actual Windows WASAPI sample-rate checks with silent WAVs and isolated user data.

Changes the real endpoint mix format (and with --exclusive opens exclusive output).
Always attempts to restore the original mix rate and closes only its own test client.
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
    try:
        with tempfile.TemporaryDirectory(prefix="jeek-auto-format-") as temp:
            data, music = Path(temp) / "data", Path(temp) / "music"
            data.mkdir(); music.mkdir()
            (data / "settings.json").write_text(json.dumps({"closeAction": "Exit", "autoUpdateCheck": False, "muted": True}), encoding="utf-8")
            specs = [(48000, 16), (48000, 24), (44100, 24), (48000, 24), (44100, 16)]
            for index, (rate, bits) in enumerate(specs, 1):
                with wave.open(str(music / f"{index:02d}-{rate}-{bits}.wav"), "wb") as wav:
                    wav.setparams((2, bits // 8, rate, 0, "NONE", "not compressed"))
                    wav.writeframes(bytes(rate * 2 * (bits // 8) * 6))
            with McpClient(args.exe, data) as client:
                def call(name, **arguments):
                    return helpers.call(client, name, **arguments)
                def wait_format(rate, bits):
                    result = helpers.wait_for(client, "get_output", lambda value:
                        value["audioFormat"]["applied"] and value["audioFormat"]["actualDevice"] is not None and
                        value["audioFormat"]["actualDevice"]["sampleRate"] == rate and
                        value["audioFormat"]["actualDevice"]["validBits"] == bits)
                    with native.AudioAccess(original["endpointId"]) as access:
                        independent = native.public(access.capture())
                    assert independent["device"]["sampleRate"] == rate and independent["device"]["validBits"] == bits, independent
                    assert independent["mix"]["sampleRate"] == rate, independent
                    assert result["format"]["deviceRate"] == rate, result
                    records.append({"mcp": result, "independent": independent})
                    return result
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
                    before = call("get_output")["audioFormat"]["actualDevice"]
                    call("set_output", automaticFormat=False)
                    assert call("get_output")["audioFormat"]["actualDevice"] == before
                    call("set_output", automaticFormat=True)
                    wait_format(48000, 16)
                    after = call("get_status")
                    assert not after["playing"] and abs(after["positionSeconds"] - paused["positionSeconds"]) < .3, (paused, after)
                    call("playback", action="resume")
                    helpers.wait_for(client, "get_status", lambda value: value["playing"])
                    call("playback", action="seek", seconds=4)
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
                    print("PASS: independent endpoint/mix readback; 48/16 -> 48/24 -> 44.1/source24 (driver-verified precision fallback) -> 48/24 -> 44.1/16; pause/disable/resume; automatic exclusive/shared transition", flush=True)
                finally:
                    helpers.close_player(player_pid)
                    player_pid = None
            client = None
    finally:
        # Full restore only after closing our own stream, and never over a newly started client.
        if not running_clients():
            with native.AudioAccess(original["endpointId"]) as access:
                access.restore(original)
            with native.AudioAccess(original["endpointId"]) as access:
                restored = access.capture()
            assert restored["device"] == original["device"] and restored["mix"] == original["mix"], native.public(restored)
            records.append({"independentRestored": native.public(restored)})
        else:
            records.append({"restoreSkipped": "Another JeekMusic client is now active; refusing to overwrite its format."})
        args.results.parent.mkdir(parents=True, exist_ok=True)
        args.results.write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
