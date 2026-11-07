"""Verify external opening against the real Windows client, isolated silent audio and MCP.

--ui also selects and plays a sibling through Windows UI Automation and captures the
real window. Uses shared output with app mute and automatic format writes disabled.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import time
import wave

from mcp_client import DEFAULT_EXE, McpClient


def call(client, name, **arguments):
    result = client.call(name, arguments)
    assert not result.get("isError"), (name, result)
    return result["structuredContent"]


def wait(client, name, predicate, **arguments):
    end = time.monotonic() + 25
    while time.monotonic() < end:
        result = call(client, name, **arguments)
        if predicate(result):
            return result
        time.sleep(0.05)
    raise AssertionError((name, result))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, default=DEFAULT_EXE)
    parser.add_argument("--report", type=Path, required=True, help="New fixture/report directory")
    parser.add_argument("--ui", action="store_true")
    args = parser.parse_args()
    root = args.report.resolve()
    root.mkdir(parents=True, exist_ok=False)
    data = root / "data"
    data.mkdir()
    (data / "settings.json").write_text(json.dumps({
        "closeAction": "Exit", "autoUpdateCheck": False, "muted": True,
        "automaticAudioFormat": False, "exclusive": False,
        "songSort": "FileName", "sortDescending": False,
    }), encoding="utf-8")
    # Full absolute Unicode paths exercise the same argument transport as Explorer.
    music = root / "同目录音乐"
    music.mkdir()
    (music / "subfolder").mkdir()
    for path in [music / "01-关联打开.wav", music / "02-同目录歌曲.wav", music / "subfolder" / "03-不应出现.wav"]:
        with wave.open(str(path), "wb") as output:
            output.setparams((2, 2, 48000, 0, "NONE", "not compressed"))
            output.writeframes(bytes(48000 * 4 * 120))
    (music / "notes.txt").write_text("not audio", encoding="utf-8")
    env = {**os.environ, "JEEKMUSIC_DATA": str(data)}
    exe = args.exe.resolve()
    player = subprocess.Popen([str(exe), str(music / "01-关联打开.wav")], env=env)
    report = {}

    def ui(action, **kwargs):
        command = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                   str(Path(__file__).with_name("preference-ui.ps1")), "-AppProcess", str(player.pid), "-Action", action]
        for name, value in kwargs.items():
            command += ["-" + name, str(value)]
        result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
        assert result.returncode == 0, result.stdout + result.stderr
        return result.stdout

    try:
        with McpClient(exe, data) as client:
            tools = client.request("tools/list")["tools"]
            assert len(tools) == 42
            diagnostics = wait(client, "get_diagnostics", lambda d: d["externalFiles"]["state"] == "ready")
            report["startup"] = diagnostics["externalFiles"]
            assert diagnostics["externalFiles"]["trackCount"] == 2
            assert call(client, "list_folders")["folders"] == []
            rows = wait(client, "song_filter", lambda d: d["ready"] and d["rowCount"] == 2, action="status")
            assert {item["fileName"] for item in rows["items"]} == {"01-关联打开.wav", "02-同目录歌曲.wav"}
            playback = wait(client, "get_status", lambda s: s["playing"])
            assert playback["track"]["fileName"] == "01-关联打开.wav"
            assert playback["queueCount"] == 1
            report["startupPlayback"] = playback
            if args.ui:
                call(client, "show_window")
                report["uiBefore"] = ui("Dump", Shot=root / "01-associated-open.png")
                ui("Click", Name="02-同目录歌曲", Type="Text")
                ui("Right", Name="02-同目录歌曲", Type="Text", Shot=root / "01b-song-menu.png")
                ui("Invoke", Name="播放", Type="MenuItem")
                playback = wait(client, "get_status", lambda s: s["playing"] and s["track"]["fileName"] == "02-同目录歌曲.wav")
                report["uiPlayedSibling"] = ui("Dump", Shot=root / "02-sibling-playing.png")
                assert playback["queueCount"] == 2
                report["siblingPlayback"] = playback
                # The column header is the first matching Text element by screen Y;
                # the navigation entry is the second. Verify browsing really changed.
                ui("Click", Name="艺术家", Type="Text", Index=1)
                wait(client, "song_filter", lambda d: d["ready"] and d["source"] is None, action="status")
                ui("Focus", Id="NavCollapse", Type="Button")
                ui("Key", Key=76, Modifier=17)  # Ctrl+L is available outside song views too.
                wait(client, "song_filter", lambda d: d["ready"] and d["rowCount"] == 2 and d["source"].startswith("temporary-folder"), action="status")
                report["uiLocateReturn"] = ui("Dump", Shot=root / "02b-locate-return.png")
            # Existing-instance argument forwarding also takes the shared service.
            subprocess.run([str(exe), str(music / "01-关联打开.wav"), "--background"], env=env, timeout=10, check=True)
            wait(client, "get_status", lambda s: s["playing"] and s["track"]["fileName"] == "01-关联打开.wav")
            wait(client, "get_diagnostics", lambda d: d["externalFiles"]["generation"] >= 2 and d["externalFiles"]["state"] == "ready")
            bad = client.call("open_files", {"paths": [str(music)]})
            assert bad["isError"]
            call(client, "open_files", paths=[str(music / "02-同目录歌曲.wav")])
            final = wait(client, "get_diagnostics", lambda d: d["externalFiles"]["generation"] >= 3 and d["externalFiles"]["state"] == "ready")
            wait(client, "get_status", lambda s: s["playing"] and s["track"]["fileName"] == "02-同目录歌曲.wav")
            assert call(client, "list_folders")["folders"] == []
            assert final["status"]["trackCount"] == 0
            report["final"] = final["externalFiles"]
            call(client, "checkpoint_client")
        player.terminate()
        player.wait(timeout=10)
        player = subprocess.Popen([str(exe)], env=env)
        with McpClient(exe, data) as client:
            restarted = call(client, "get_diagnostics")
            assert restarted["externalFiles"]["state"] == "idle"
            assert call(client, "list_folders")["folders"] == []
            report["afterRestart"] = restarted["externalFiles"]
            if args.ui:
                report["uiAfterRestart"] = ui("Dump", Shot=root / "03-session-cleared.png")
        report["passed"] = True
    finally:
        if player.poll() is None:
            player.terminate()
            player.wait(timeout=10)
        (root / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print("PASS: startup/forwarded/MCP opens, playback, immediate siblings, no registration, session-only lifecycle" + (", real UI playback/locate and screenshots" if args.ui else ""))


if __name__ == "__main__":
    main()
