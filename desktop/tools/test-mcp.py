"""Real EXE/MCP startup diagnostics; launches a temporary client even by default.

--audio explicitly enables real shared WASAPI playback using silent WAVs.
--audio --exclusive additionally opens the real endpoint exclusively: other apps
can lose sound despite digital silence / application mute. Do not run while gaming.
--sdk additionally verifies the official Python SDK (pip install mcp).
"""
import argparse
import asyncio
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import wave

from mcp_client import DEFAULT_EXE, McpClient


def call(client, tool, **arguments):
    result = client.call(tool, arguments)
    assert not result.get("isError"), (tool, result)
    return result["structuredContent"]


def wait_for(client, tool, predicate, **arguments):
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        result = call(client, tool, **arguments)
        if predicate(result):
            return result
        time.sleep(0.1)
    raise AssertionError(("Timed out", tool, result))


def close_player(player_pid):
    """Post WM_CLOSE to this fixture's main window; settings opt into Exit instead of Tray."""
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user32.EnumWindows.argtypes = [callback_type, wintypes.LPARAM]
    user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    found = []

    @callback_type
    def visit(hwnd, _):
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        title = ctypes.create_unicode_buffer(256)
        user32.GetWindowTextW(hwnd, title, len(title))
        if pid.value == player_pid and title.value == "Jeek Music":
            found.append(hwnd)
        return True

    user32.EnumWindows(visit, 0)
    assert found, "No main window for test player"
    handle = kernel32.OpenProcess(0x00100000, False, player_pid)  # SYNCHRONIZE
    assert handle
    try:
        user32.PostMessageW(found[0], 0x0010, 0, 0)  # WM_CLOSE
        assert kernel32.WaitForSingleObject(handle, 5000) == 0, "Player did not exit gracefully with active MCP clients"
    finally:
        kernel32.CloseHandle(handle)


async def sdk_check(exe, data, player_pid):
    from mcp import ClientSession, StdioServerParameters
    from mcp.client.stdio import stdio_client
    from jsonschema import Draft202012Validator

    parameters = StdioServerParameters(command=str(exe), args=["--mcp"], env={**os.environ, "JEEKMUSIC_DATA": str(data)})
    async with stdio_client(parameters) as (read, write):
        async with ClientSession(read, write) as session:
            info = await session.initialize()
            assert info.serverInfo.name == "JeekMusic"
            catalog = await session.list_tools()
            assert len(catalog.tools) == 36
            for tool in catalog.tools:
                Draft202012Validator.check_schema(tool.inputSchema)
            diagnostics = await session.call_tool("get_diagnostics", {})
            assert not diagnostics.isError
            assert diagnostics.structuredContent["processId"] == player_pid
            failure = await session.call_tool("set_volume", {"volume": 101})
            assert failure.isError
            await session.send_ping()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, default=DEFAULT_EXE)
    parser.add_argument("--sdk", action="store_true")
    parser.add_argument("--startup-only", action="store_true", help="Explicit alias for the default startup/diagnostics checks; still launches a client")
    parser.add_argument("--audio", action="store_true", help="Opt into real shared WASAPI playback/device tests (silent audio still opens an endpoint)")
    parser.add_argument("--exclusive", action="store_true", help="With --audio, opt into exclusive endpoint tests that can silence other apps")
    parser.add_argument("--catalog", type=Path, help="Write the actual tool catalog for build parity checks")
    args = parser.parse_args()
    if args.exclusive and not args.audio:
        parser.error("--exclusive requires --audio; exclusive access can interrupt other apps")
    if args.startup_only and args.audio:
        parser.error("--startup-only cannot be combined with --audio")
    exe = args.exe.resolve()
    player_pid = None
    with tempfile.TemporaryDirectory(prefix="jeekmusic-mcp-") as temp:
        data = Path(temp) / "data"
        data.mkdir()
        (data / "settings.json").write_text(json.dumps({"closeAction": "Exit", "autoUpdateCheck": False}), encoding="utf-8")
        music = Path(temp) / "music"
        music.mkdir()
        for name in (("01-测试歌曲", "02-Silence", "03-Last") if args.audio else ()):
            with wave.open(str(music / (name + ".wav")), "wb") as wav:
                wav.setparams((2, 2, 44100, 0, "NONE", "not compressed"))
                wav.writeframes(bytes(44100 * 4 * 30))
        try:
            # Cold start must return promptly and disconnect without owning the GUI's standard streams.
            with McpClient(exe, data) as first:
                diagnostics = call(first, "get_diagnostics")
                player_pid = diagnostics["processId"]
                # McpClient resolves the supplied directory. Hosted Windows can use
                # short TEMP paths / junction aliases; compare filesystem identity.
                assert Path(diagnostics["dataDirectory"]).samefile(data), (diagnostics["dataDirectory"], str(data))
                assert Path(diagnostics["processPath"]).samefile(exe)
                assert diagnostics["processArchitecture"] == "X64"
                assert isinstance(diagnostics["dynamicCodeSupported"], bool)
                expected_dynamic = os.environ.get("JEEK_EXPECT_DYNAMIC_CODE")
                if expected_dynamic:
                    assert diagnostics["dynamicCodeSupported"] == (expected_dynamic == "true"), diagnostics
                assert diagnostics["frameworkDescription"].startswith(".NET")
                typography = diagnostics["typography"]
                assert typography["initialized"]
                assert typography["effectiveDefaultFamily"] == typography["defaultFamilyChain"] == "Microsoft YaHei, Segoe UI"
                assert typography["normalFamilies"] and typography["boldFamilies"]
                if typography["microsoftYaHeiAvailable"]:
                    assert typography["normalFamilies"] == ["Microsoft YaHei"], typography
                    assert typography["boldFamilies"] == ["Microsoft YaHei"], typography
                print("Typography: " + json.dumps(typography, ensure_ascii=True), flush=True)
                assert call(first, "get_status")["trackCount"] == 0
                catalog = first.request("tools/list")
                assert len(catalog["tools"]) == 36
                schema = next(t for t in catalog["tools"] if t["name"] == "get_diagnostics")["inputSchema"]
                assert schema["properties"] == {} and schema["additionalProperties"] is False
                if args.catalog:
                    args.catalog.write_text(json.dumps(catalog, ensure_ascii=False, indent=2), encoding="utf-8")
                print("Runtime: " + json.dumps({k: diagnostics[k] for k in (
                    "processPath", "frameworkDescription", "processArchitecture", "dynamicCodeSupported")}), flush=True)
                if not args.audio:
                    if args.sdk:
                        asyncio.run(sdk_check(exe, data, player_pid))
                    result = subprocess.run([str(exe), "--mcp"], input="", capture_output=True,
                                            text=True, timeout=40,
                                            env={**os.environ, "JEEKMUSIC_DATA": str(data),
                                                 "DOTNET_DISABLE_GUI_ERRORS": "1"})
                    assert result.returncode == 0 and not result.stdout and not result.stderr, (
                        result.returncode, result.stdout, result.stderr)
                    close_player(player_pid)
                    player_pid = None
                    print("PASS: actual EXE/MCP cold start, 36 tools, diagnostics, graceful GUI exit, bridge exit=0 stdout/stderr empty (no audio endpoint required)")
                    return
            # Both clients share the same GUI, but each negotiates its own MCP session.
            with McpClient(exe, data) as client, McpClient(exe, data) as other:
                assert call(other, "get_diagnostics")["processId"] == player_pid
                assert len(client.request("tools/list")["tools"]) == 36
                assert call(client, "add_folder", path=str(music))["queued"]
                tracks = wait_for(other, "search_tracks", lambda r: r["total"] == 3)["items"]
                tracks.sort(key=lambda t: t["fileName"])
                ids = [t["id"] for t in tracks]
                assert call(client, "search_tracks", query="测试")["total"] == 1
                assert call(client, "get_track", trackId=ids[0])["path"] == str(music / "01-测试歌曲.wav")
                page = call(client, "search_tracks", limit=1)
                assert len(page["items"]) == 1 and page["nextOffset"] == 1
                assert call(client, "search_tracks", offset=3)["items"] == []

                playlist = call(client, "edit_playlist", action="create", name="MCP 测试")
                pid = playlist["id"]
                call(client, "edit_playlist", action="add", playlistId=pid, trackIds=ids)
                call(client, "edit_playlist", action="move", playlistId=pid, positions=[2], target=0)
                entries = call(other, "get_playlist", playlistId=pid)["items"]
                assert [e["track"]["id"] for e in entries] == [ids[2], ids[0], ids[1]]
                call(client, "edit_playlist", action="remove", playlistId=pid, positions=[1])
                assert call(client, "get_playlist", playlistId=pid)["total"] == 2
                call(client, "edit_playlist", action="rename", playlistId=pid, name="重命名")
                assert call(other, "list_playlists")["items"][0]["name"] == "重命名"

                call(client, "set_output", device="auto", exclusive=False)
                initial_output = call(client, "get_output")["volumeState"]
                assert initial_output["semantics"] == "endpoint-master"
                assert initial_output["actualDeviceId"]
                assert initial_output["volumeAvailable"]
                initial_volume = initial_output["endpointVolume"]
                # Never raise or restore the real user's endpoint volume in smoke tests.
                call(client, "set_volume", muted=True)
                assert call(client, "get_output")["volumeState"]["endpointMuted"] == initial_output["endpointMuted"]
                call(client, "set_play_mode", shuffle=False, repeat="all")
                call(client, "play_tracks", trackIds=ids)
                wait_for(other, "get_status", lambda s: s["playing"] and s["track"]["id"] == ids[0])
                # Bind explicitly to the same real endpoint, then back to auto. Only silent
                # WAVs play and application mute remains on; do not touch endpoint volume.
                output = call(client, "get_output")
                endpoint_id = output["volumeState"]["actualDeviceId"]
                device = next(d["id"] for d in output["devices"]
                              if d["id"] != "auto" and endpoint_id.endswith(d["id"].removeprefix("wasapi/")))
                call(client, "set_output", device=device)
                wait_for(other, "get_status", lambda s: s["playing"])
                assert call(client, "get_output")["volumeState"]["actualDeviceId"] == endpoint_id
                call(client, "set_output", device="auto")
                wait_for(other, "get_status", lambda s: s["playing"])
                if args.exclusive:
                    output = call(client, "get_output")["volumeState"]
                    exclusive = client.call("set_output", {"exclusive": True})
                    if output["sessionMuted"] or output["sessionVolume"] is None or output["sessionVolume"] < 99.999:
                        assert exclusive.get("isError"), exclusive
                        assert call(client, "get_output")["exclusive"] is False
                        print("PASS: real mixer attenuation/mute blocks exclusive without overriding it")
                    else:
                        assert not exclusive.get("isError"), exclusive
                        state = wait_for(other, "get_status", lambda s: s["playing"] or s["output"]["blockReason"])
                        if state["output"]["hardwareVolumeConfirmationRequired"]:
                            # Explicit --exclusive authorizes real device ownership, even for silence.
                            call(client, "playback", action="resume")
                            wait_for(other, "get_status", lambda s: s["playing"])
                        else:
                            assert state["playing"], state
                        call(client, "set_output", exclusive=False)
                        wait_for(other, "get_status", lambda s: s["playing"])
                        print("PASS: real shared/exclusive/shared decoder rebuild")
                current_output = call(client, "get_output")["volumeState"]
                assert abs(current_output["endpointVolume"] - initial_volume) < 0.5
                assert current_output["endpointMuted"] == initial_output["endpointMuted"]
                assert call(client, "get_status")["muted"]
                assert call(client, "get_queue")["total"] == 3
                call(client, "playback", action="pause")
                wait_for(other, "get_status", lambda s: not s["playing"])
                call(client, "playback", action="seek", seconds=5)
                wait_for(other, "get_status", lambda s: abs(s["positionSeconds"] - 5) < 0.3)
                # Shared seek service clamps EOF without advancing a paused queue.
                call(client, "playback", action="seek", seconds=30)
                time.sleep(2.2)  # Exceed the optimistic seek target timeout; check real decoder position.
                end = call(other, "get_status")
                assert abs(end["positionSeconds"] - 29) < 0.05 and not end["playing"], end
                assert end["track"]["id"] == ids[0] and end["queueIndex"] == 0, end
                assert client.call("playback", {"action": "seek", "seconds": 31})["isError"]
                assert client.call("playback", {"action": "seek", "seconds": -1})["isError"]
                call(client, "playback", action="seek", seconds=0)
                time.sleep(0.2)
                start = call(other, "get_status")
                assert start["positionSeconds"] < 0.05 and not start["playing"], start
                call(client, "playback", action="seek", seconds=5)
                call(client, "playback", action="resume")
                wait_for(other, "get_status", lambda s: s["playing"])
                call(client, "playback", action="next")
                wait_for(other, "get_status", lambda s: s["track"]["id"] == ids[1])
                call(client, "edit_queue", action="next", trackIds=[ids[2]])
                assert call(other, "get_queue")["items"][2]["manual"]
                call(client, "edit_queue", action="move", index=2, toIndex=3)
                call(client, "edit_queue", action="remove", index=3)
                call(client, "edit_queue", action="jump", index=0)
                call(client, "edit_queue", action="clear")
                wait_for(other, "get_status", lambda s: s["queueCount"] == 0 and s["idle"])
                call(client, "edit_queue", action="add", trackIds=[ids[0]])
                wait_for(other, "get_status", lambda s: s["queueCount"] == 1 and s["playing"])
                call(client, "edit_queue", action="clear")

                for tool, arguments in (
                    ("set_volume", {"volume": 101}),
                    ("play_tracks", {"trackIds": [ids[0], 99999999]}),
                    ("playback", {"action": "seek"}),
                    ("edit_queue", {"action": "jump", "index": 100}),
                    ("edit_playlist", {"action": "rename", "playlistId": pid}),
                    ("get_status", {"unexpected": 1}),
                ):
                    assert client.call(tool, arguments)["isError"], tool
                assert call(other, "get_queue")["total"] == 0
                assert call(client, "get_output")["exclusive"] is False
                assert call(client, "get_logs", kind="app")["text"]
                assert call(client, "get_logs", kind="playback")["text"]
                assert call(client, "rescan_library", full=True)["queued"]
                if args.sdk:
                    asyncio.run(sdk_check(exe, data, player_pid))
                call(client, "edit_playlist", action="delete", playlistId=pid)
                assert call(other, "list_playlists")["total"] == 0
                folder = call(client, "list_folders")["folders"][0]
                call(client, "remove_folder", folderId=folder["id"])
                wait_for(other, "search_tracks", lambda r: r["total"] == 0)
                assert len(list(music.glob("*.wav"))) == 3
                close_player(player_pid)
                player_pid = None
            with McpClient(exe, data) as restarted:
                restored = call(restarted, "get_diagnostics")
                player_pid = restored["processId"]
                assert abs(restored["status"]["volume"] - initial_volume) < 0.5 and restored["status"]["muted"]
                assert restored["output"]["volumeState"]["actualDeviceId"] == initial_output["actualDeviceId"]
                close_player(player_pid)
                player_pid = None
            print("PASS: cold start, stdio shutdown, shared multi-client state, Unicode, pagination, library, playlists, playback, queue, validation, logs, graceful exit with active clients, settings persistence" + (", official SDK" if args.sdk else ""))
            # Also capture the real apphost's stdio and exit code after stdin EOF.
            # Use an already-running isolated player so this probe owns only a bridge.
            with McpClient(exe, data) as final:
                player_pid = call(final, "get_diagnostics")["processId"]
                result = subprocess.run([str(exe), "--mcp"], input="", capture_output=True,
                                        text=True, timeout=40,
                                        env={**os.environ, "JEEKMUSIC_DATA": str(data),
                                             "DOTNET_DISABLE_GUI_ERRORS": "1"})
                assert result.returncode == 0, (result.returncode, result.stdout, result.stderr)
                assert not result.stdout and not result.stderr, (result.stdout, result.stderr)
                close_player(player_pid)
                player_pid = None
            print("PASS: real EXE stdio EOF exit=0, stdout/stderr empty")
        finally:
            if player_pid:
                try:
                    os.kill(player_pid, signal.SIGTERM)
                except ProcessLookupError:
                    pass
                time.sleep(0.5)  # Windows releases SQLite/log file handles asynchronously after termination.


if __name__ == "__main__":
    main()
