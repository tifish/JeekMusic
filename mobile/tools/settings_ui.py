"""Read/operate the actual settings UI on an explicitly selected emulator.

No installs, data resets, music writes or destructive actions. Start JeekMusic and open
Settings before running. `verify` visits every category and restores auto-update state.
"""
import argparse
import json
import re
import subprocess
import time
from pathlib import Path
from xml.etree import ElementTree as ET

P = argparse.ArgumentParser(description=__doc__)
P.add_argument("--serial", required=True)
P.add_argument("--package", default="com.tinyfish.jeekmusic.debug")
P.add_argument("--output", type=Path, default=Path("output/settings-ui"))
P.add_argument("action", choices=["show", "tap", "shot", "verify", "down", "up", "back"])
P.add_argument("value", nargs="?")
A = P.parse_args()
assert A.serial.startswith("emulator-"), "This regression tool is restricted to an explicitly allocated emulator"
ADB = ["adb", "-s", A.serial]

def command(*args):
    return subprocess.check_output(ADB + list(args))

def dump():
    assert b"dumped to" in command("shell", "uiautomator", "dump", "/sdcard/settings-ui.xml")
    root = ET.fromstring(command("exec-out", "cat", "/sdcard/settings-ui.xml"))
    assert any(n.get("package") == A.package for n in root.iter("node")), "JeekMusic is not visible"
    return root

def bounds(n):
    return list(map(int, re.findall(r"\d+", n.get("bounds"))))

def nodes(value, root=None):
    return [n for n in (dump() if root is None else root).iter("node")
            if value in (n.get("resource-id"), n.get("text"), n.get("content-desc"))]

def tap(value):
    found = nodes(value)
    assert len(found) == 1, f"Expected one match for {value!r}, got {len(found)}"
    x1, y1, x2, y2 = bounds(found[0])
    assert x2 > x1 and y2 > y1 and found[0].get("enabled") == "true"
    command("shell", "input", "tap", str((x1+x2)//2), str((y1+y2)//2))
    time.sleep(.3)

def scroll(down):
    size = command("shell", "wm", "size").decode().strip().splitlines()[-1]
    w, h = map(int, re.search(r"(\d+)x(\d+)", size).groups())
    command("shell", "input", "swipe", str(w//2), str(int(h*(.75 if down else .4))),
            str(w//2), str(int(h*(.4 if down else .75))), "600")

def find(value):
    # A clipped navigation row can remain in the accessibility tree even when its
    # title is outside the viewport. Bring the whole row into view before tapping.
    previous = None
    for direction in (True, False):
        previous = None
        for _ in range(8):
            root = dump()
            found = nodes(value, root)
            if found:
                if value.startswith("settings_group_"):
                    viewport = next(n for n in root.iter("node") if n.get("scrollable") == "true")
                    _, top, _, bottom = bounds(viewport)
                    _, y1, _, y2 = bounds(found[0])
                    if y1 <= top or y2 >= bottom:
                        # Nudge using observed clipping; a full fling can jump past a short row.
                        x1, _, x2, _ = bounds(viewport)
                        center = (top + bottom) // 2
                        overflow = top - y1 if y1 <= top else y2 - bottom
                        distance = min((bottom - top) // 3, max(0, overflow) + (bottom - top) // 8)
                        end = center + distance if y1 <= top else center - distance
                        command("shell", "input", "swipe", str((x1+x2)//2), str(center),
                                str((x1+x2)//2), str(end), "600")
                        previous = None
                        continue
                return
            signature = ET.tostring(root)
            if signature == previous: break
            previous = signature
            scroll(direction)
    raise AssertionError(f"Unreachable setting: {value}")

def back():
    command("shell", "input", "keyevent", "4")
    time.sleep(.3)

def shot(name):
    root = dump()
    assert not any(n.get("text", "").startswith("jeeklan://") for n in root.iter("node")), "Sensitive pairing field"
    A.output.mkdir(parents=True, exist_ok=True)
    (A.output / (name + ".png")).write_bytes(command("exec-out", "screencap", "-p"))
    (A.output / (name + ".xml")).write_bytes(ET.tostring(root, encoding="utf-8"))

def verify():
    # A real navigation round trip checks that grouping did not orphan settings.
    targets = {"library": "settings_add_folder", "playback": None, "appearance": "open_tab_manager",
               "devices": None, "data": "backup_now", "lyrics": "online_lyrics",
               "covers": "online_covers", "analysis": "settings_analyze_now", "preference": None, "about": "auto_update"}
    find("check_update")
    scroll(False)
    shot("home-top")
    results = []
    for page, control in targets.items():
        tag = "settings_group_" + page
        find(tag)
        before = bounds(nodes(tag)[0])
        tap(tag)
        assert not nodes(tag), f"Tapping {page} did not open its category"
        if control: find(control)
        shot(page)
        if page in ("lyrics", "covers"):
            toggle = "online_" + page
            state = nodes(toggle)[0].get("checked")
            assert state in ("true", "false"), "Online switch lost accessible toggle semantics"
            if state == "false":
                tap(toggle)
                assert any(n.get("text") in ("Cancel", "取消") for n in dump().iter("node")), "Privacy confirmation missing"
                back()
                assert nodes(toggle)[0].get("checked") == "false", "Dismissing privacy notice enabled online fetching"
        if page == "playback":
            for mode in ("BACKGROUND", "ALWAYS", "OFF"):
                assert nodes("low_power_" + mode), "Missing low-power option"
        if page in ("library", "appearance"):
            editor, screen = (("open_artist_settings", "artist_settings") if page == "library"
                              else ("open_tab_manager", "tab_manager"))
            find(editor)
            editor_bounds = bounds(nodes(editor)[0])
            tap(editor)
            assert nodes(screen), "Existing settings editor is unreachable"
            shot(screen)
            back()
            assert nodes(editor) and bounds(nodes(editor)[0]) == editor_bounds, "Editor return lost category scroll"
        if page == "devices":
            assert any(n.get("text") in ("Scan to pair", "扫码配对") for n in dump().iter("node")), "LAN pairing entry missing"
        if page == "about":
            original = nodes("auto_update")[0].get("checked")
            assert original in ("true", "false"), "Switch row is missing toggle semantics"
            try:
                tap("auto_update")
                assert nodes("auto_update")[0].get("checked") != original
                back(); find(tag); tap(tag)
                assert nodes("auto_update")[0].get("checked") != original, "Switch state lost on return"
            finally:
                if nodes("auto_update") and nodes("auto_update")[0].get("checked") != original:
                    tap("auto_update")
            assert nodes("auto_update")[0].get("checked") == original
        back()
        assert nodes(tag), f"Back from {page} lost the overview scroll position"
        assert bounds(nodes(tag)[0]) == before, f"Overview jumped after visiting {page}"
        results.append({"category": page, "navigation": "passed", "scroll_restored": True})
    shot("home-bottom")
    A.output.mkdir(parents=True, exist_ok=True)
    (A.output / "results.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    print(json.dumps(results))

if A.action == "show":
    for n in dump().iter("node"):
        if n.get("text", "").startswith("jeeklan://"): continue
        if n.get("text") or n.get("resource-id") or n.get("content-desc"):
            print(json.dumps(n.attrib, ensure_ascii=True))
elif A.action == "tap": tap(A.value)
elif A.action == "shot": shot(A.value)
elif A.action == "verify": verify()
elif A.action == "back": back()
else: scroll(A.action == "down")
