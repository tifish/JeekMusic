"""Opt-in actual Windows WASAPI sample-rate checks with silent WAVs and isolated user data.

Changes the real endpoint mix format (and with --exclusive opens exclusive output).
Always attempts to restore the original mix rate and closes only its own test client.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import tempfile
import time
import wave

from mcp_client import DEFAULT_EXE, McpClient


def helper():
    spec = importlib.util.spec_from_file_location("sample_rate_mcp_helpers", Path(__file__).with_name("test-mcp.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, default=DEFAULT_EXE)
    parser.add_argument("--audio", action="store_true", help="Required acknowledgement of real endpoint format changes")
    parser.add_argument("--exclusive", action="store_true", help="Also open actual exclusive output")
    parser.add_argument("--results", type=Path, default=Path("desktop/artifacts/sample-rate-verification/wasapi.json"))
    args = parser.parse_args()
    if not args.audio:
        parser.error("--audio is required: silent WAVs still change and occupy real audio endpoints")
    helpers = helper()
    records = []
    player_pid = None
    with tempfile.TemporaryDirectory(prefix="jeek-sample-rate-") as temp:
        data, music = Path(temp) / "data", Path(temp) / "music"
        data.mkdir()
        music.mkdir()
        (data / "settings.json").write_text(json.dumps({"closeAction": "Exit", "autoUpdateCheck": False, "muted": True}), encoding="utf-8")
        with McpClient(args.exe, data) as client:
            def call(name, **arguments):
                return helpers.call(client, name, **arguments)

            def wait_output(rate):
                result = helpers.wait_for(client, "get_output", lambda value:
                                          value["sampleRate"]["actualRate"] == rate and value["sampleRate"]["applied"])
                records.append(result)
                return result

            initial = call("get_output")
            original_rate = initial["sampleRate"]["mixRate"]
            records.append(initial)
            player_pid = call("get_diagnostics")["processId"]
            try:
                supported = initial["sampleRate"]["sharedRates"]
                assert 44100 in supported and 48000 in supported, initial
                second_rate = 96000 if 96000 in supported else 48000
                for index, rate in enumerate((44100, second_rate), 1):
                    with wave.open(str(music / f"{index:02d}-{rate}.wav"), "wb") as wav:
                        wav.setparams((2, 2, rate, 0, "NONE", "not compressed"))
                        wav.writeframes(bytes(rate * 4 * 8))
                call("add_folder", path=str(music))
                tracks = helpers.wait_for(client, "search_tracks", lambda value: value["total"] == 2)["items"]
                ids = [track["id"] for track in sorted(tracks, key=lambda track: track["fileName"])]
                call("set_output", exclusive=False, sampleRateMode="auto")
                call("play_tracks", trackIds=ids)
                wait_output(44100)
                helpers.wait_for(client, "get_status", lambda value: value["playing"])
                call("playback", action="pause")
                paused = helpers.wait_for(client, "get_status", lambda value: not value["playing"])
                call("set_output", sampleRate=48000)
                wait_output(48000)
                after = call("get_status")
                assert not after["playing"] and abs(after["positionSeconds"] - paused["positionSeconds"]) < 0.3, (paused, after)
                call("set_output", sampleRateMode="auto")
                wait_output(44100)
                call("playback", action="resume")
                helpers.wait_for(client, "get_status", lambda value: value["playing"])
                call("playback", action="seek", seconds=6)
                helpers.wait_for(client, "get_status", lambda value: value["track"]["id"] == ids[1] and value["playing"])
                wait_output(second_rate)
                if args.exclusive:
                    call("set_output", exclusive=True, sampleRateMode="manual", sampleRate=48000)
                    # Only zero-valued PCM is ever decoded; this exercises the normal explicit resume gate.
                    time.sleep(0.3)
                    call("playback", action="resume")
                    result = wait_output(48000)
                    assert result["exclusive"] and result["format"]["deviceRate"] == 48000, result
                print("PASS: real WASAPI shared auto 44100 -> %s Hz, paused manual 48000 Hz with position retained%s" %
                      (second_rate, ", exclusive manual 48000 Hz" if args.exclusive else ""), flush=True)
            finally:
                try:
                    call("playback", action="pause")
                    call("set_output", exclusive=False, sampleRate=original_rate)
                    call("set_output", sampleRateMode="system")
                    restored = call("get_output")
                    records.append(restored)
                    assert restored["sampleRate"]["mixRate"] == original_rate, restored
                finally:
                    args.results.parent.mkdir(parents=True, exist_ok=True)
                    args.results.write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding="utf-8")
                    helpers.close_player(player_pid)
                    player_pid = None


if __name__ == "__main__":
    main()
