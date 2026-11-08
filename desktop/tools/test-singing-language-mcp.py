"""Actual vocal-language filter regression through the real client/MCP; optional native UI acceptance.

Uses a fresh isolated synthetic library and silence only. No audio playback, network,
learning answers, or writes to user media. UI actions reuse Windows UI Automation.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import sqlite3
import subprocess
import wave

from mcp_client import McpClient

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("startup_checks", Path(__file__).with_name("test-mcp.py"))
startup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(startup)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, default=ROOT / "bin/JeekMusic.exe")
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--language", choices=["zh-Hans", "en"], default="zh-Hans")
    parser.add_argument("--ui", action="store_true")
    args = parser.parse_args()
    data = args.data.resolve()
    assert data.is_relative_to(ROOT / "desktop/artifacts") and data.name.endswith("fixture")
    assert not data.exists(), "Use a fresh isolated fixture"
    data.mkdir(parents=True)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    (data / "settings.json").write_text(json.dumps(dict(closeAction="Exit", autoUpdateCheck=False,
        analysisMode="Manual", onlineLyrics=False, onlineCovers=False, songSort="FileName",
        sortDescending=False, language=args.language)), encoding="utf-8")
    music = data / "music"
    music.mkdir()
    titles = ["Hello", "晴天", "123", "かな", "中文 English instrumental", "Hello unknown", "晴天 missing", "Sunny"]
    player = None
    report = dict(language=args.language, actualVocalLanguagePolicy=True, uiAccepted=False)
    try:
        with McpClient(args.exe, data) as client:
            player = startup.call(client, "get_diagnostics")["processId"]
            startup.close_player(player)
            player = None
        with sqlite3.connect(data / "library.db") as db:
            folder = str(music) + "\\"
            db.execute("INSERT INTO library_folder(id,volume_id,path_in_volume,last_root,display_name,available,added_at) VALUES(1,'path',?,?,'Vocal language fixture',1,1)", (folder, folder))
            for i, title in enumerate(titles, 1):
                file = music / f"{i:02}.wav"
                with wave.open(str(file), "wb") as output:
                    output.setnchannels(1); output.setsampwidth(2); output.setframerate(8000)
                    output.writeframes(b"\0\0" * 800)
                ticks = file.stat().st_mtime_ns // 100 + 621355968000000000
                db.execute("""INSERT INTO track(id,track_key,folder_id,dir,file_name,title,artist_display,artist_inferred,artists,
                    duration_ms,has_lyrics,has_cover,mtime,size,sort_title,sort_file,sort_artist,genre)
                    VALUES(?,?,1,'',?,?,'中文 English artist',0,'中文 English artist',100,0,0,?,?,?,?,'fixture','')""",
                    (i, f"title{i}", file.name, title, ticks, file.stat().st_size, title, file.name))
        with McpClient(args.exe, data) as client:
            call = lambda name, **kw: startup.call(client, name, **kw)
            diag = call("get_diagnostics")
            player = diag["processId"]
            report["nativeAot"] = not diag["dynamicCodeSupported"]
            initial = call("get_status")
            catalog = client.request("tools/list")
            args.report.with_suffix(".catalog.json").write_text(json.dumps(catalog, indent=2), encoding="utf-8")
            for name in ("song_filter", "search_tracks"):
                tool = next(t for t in catalog["tools"] if t["name"] == name)
                schema = tool["inputSchema"]["properties"]["tags"]
                assert schema["maxItems"] == 6
                assert set(("CHINESE", "NON_CHINESE", "ENGLISH", "NON_ENGLISH")).issubset(schema["items"]["enum"])
            def view(expected):
                value = startup.wait_for(client, "song_filter", lambda x: x["ready"] and x["total"] == len(expected), action="status")
                assert [x["id"] for x in value["items"]] == expected, value
                return value
            all_ids = list(range(1, 9))
            view(all_ids)
            cases = [("CHINESE", [1, 3, 8]), ("NON_CHINESE", [2, 4]),
                     ("ENGLISH", [2, 3]), ("NON_ENGLISH", [1, 4, 8])]
            call("song_filter", action="set", tags=["CHINESE"]); view([])
            for value, ids in (("CHINESE", [1, 8]), ("ENGLISH", [2]), ("CHINESE_ENGLISH", [3]),
                               ("OTHER", [4]), ("INSTRUMENTAL", [5]), ("UNKNOWN", [6])):
                call("singing_language", action="set", trackIds=ids, value=value)
            for token, ids in cases:
                call("song_filter", action="set", tags=[token]); view(ids)
                found = call("search_tracks", tags=[token], sort="FileName")
                assert [x["id"] for x in found["items"]] == ids
            call("song_filter", action="set", tags=["CHINESE", "ENGLISH"]); view([3])
            call("song_filter", action="set", annotation="unannotated"); view([3])
            assert call("song_filter", action="status")["tags"] == ["CHINESE", "ENGLISH"]
            assert call("search_tracks", query="Hello", tags=["CHINESE"], folderId=1)["total"] == 1
            assert call("search_tracks", tags=["NON_CHINESE", "NON_ENGLISH"])["total"] == 1
            six = ["SPORT", "VOCAL", "CLASSICAL", "CALM", "CHINESE", "ENGLISH"]
            assert call("search_tracks", tags=six)["total"] == 0
            for name in ("song_filter", "search_tracks"):
                for pair in (["CHINESE", "NON_CHINESE"], ["ENGLISH", "NON_ENGLISH"]):
                    params = dict(tags=pair)
                    if name == "song_filter": params["action"] = "set"
                    assert client.call(name, params).get("isError"), (name, pair)
            assert call("song_filter", action="status")["tags"] == ["CHINESE", "ENGLISH"]
            assert "audio-vocal" in call("get_diagnostics")["songFilter"]["singingLanguagePolicy"]
            call("song_filter", action="set", tags=[], annotation="all"); view(all_ids)

            if args.ui:
                call("show_window")
                ui_log = []
                def ui(action, name=None, shot=None, **extra):
                    command = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                        str(Path(__file__).with_name("preference-ui.ps1")), "-AppProcess", str(player), "-Action", action]
                    if name: command += ["-Name", name]
                    if shot: command += ["-Shot", str(args.report.parent / (shot + ".png"))]
                    for key, value in extra.items(): command += ["-" + key, str(value)]
                    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
                    ui_log.append(dict(action=action, name=name, stdout=result.stdout, stderr=result.stderr, exitCode=result.returncode))
                    args.report.with_suffix(".ui.json").write_text(json.dumps(ui_log, indent=2, ensure_ascii=False), encoding="utf-8")
                    assert result.returncode == 0, ui_log[-1]
                    return result.stdout
                label = dict(CHINESE="Chinese", NON_CHINESE="Non-Chinese", ENGLISH="English", NON_ENGLISH="Non-English") if args.language == "en" else dict(CHINESE="中文", NON_CHINESE="非中文", ENGLISH="英文", NON_ENGLISH="非英文")
                def menu(): ui("Invoke", Id="FilterButton", Type="Button")
                menu()
                dump = ui("Dump", shot="menu-all-options")
                for text in label.values(): assert text in dump, dump
                ui("Click", "    " + label["CHINESE"], shot="chinese-selected", Type="MenuItem"); view([1, 3, 8])
                menu(); ui("Click", "    " + label["ENGLISH"], shot="mixed-title", Type="MenuItem"); view([3])
                menu(); ui("Dump", shot="menu-selected-pairs"); ui("Key", Key=27)
                menu(); ui("Click", "    " + label["NON_CHINESE"], shot="non-chinese-english", Type="MenuItem"); view([2])
                menu(); ui("Click", "    " + label["NON_ENGLISH"], shot="neither-language", Type="MenuItem"); view([4])
                menu(); ui("Click", "    " + label["NON_ENGLISH"], Type="MenuItem"); view([2, 4])
                menu(); ui("Click", "Clear filters" if args.language == "en" else "清除筛选", shot="cleared", Type="MenuItem"); view(all_ids)
                # Correct a deliberately misleading English title through the real context submenu.
                ui("Right", "Hello", Type="Text")
                ui("Expand", Id="SingingLanguageMenu", Type="MenuItem")
                ui("Dump", shot="vocal-language-correction")
                ui("Click", Id="SingingLanguageCHINESE_ENGLISH", Type="MenuItem")
                status = call("singing_language", action="status", trackIds=[1])
                assert set(status["tracks"]["items"][0]["language"]["languages"]) == {"zh", "en"}
                call("singing_language", action="set", trackIds=[1], value="CHINESE")
                # Exercise the first-download confirmation and cancel through the native UI.
                menu(); ui("Click", Id="AnalyzeSingingLanguages", Type="MenuItem")
                ui("Dump", shot="local-model-confirmation")
                ui("Click", "Cancel" if args.language == "en" else "取消", Type="Button")
                assert call("singing_language", action="status", limit=1)["state"] == "idle"
                report["downloadDialogCancelled"] = True
                report["uiAccepted"] = True
            call("song_filter", action="set", tags=["CHINESE", "NON_ENGLISH"]); view([1, 8])
            call("checkpoint_client")
            saved = json.loads((data / "settings.json").read_text(encoding="utf-8-sig"))
            assert saved["tagFilter"] == ["CHINESE", "NON_ENGLISH"]
            final = call("get_status")
            assert final["playing"] == initial["playing"] and final["queueCount"] == initial["queueCount"]
            report.update(fourFilters=True, mixedAndNeither=True, sixDimensions=True, contradictionsRejected=True,
                searchDoesNotPersist=True, titlesAndArtistIgnored=True, unknownAndInstrumentalExcluded=True, playbackUnchanged=True)
        startup.close_player(player); player = None
        with McpClient(args.exe, data) as client:
            player = startup.call(client, "get_diagnostics")["processId"]
            saved = startup.wait_for(client, "song_filter", lambda x: x["ready"] and x["total"] == 2, action="status")
            assert saved["tags"] == ["CHINESE", "NON_ENGLISH"]
            assert [x["id"] for x in saved["items"]] == [1, 8]
            report["restartPersistence"] = True
    finally:
        if player: startup.close_player(player)
        args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report))


if __name__ == "__main__":
    main()
