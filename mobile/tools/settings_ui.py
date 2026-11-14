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
P.add_argument("action", choices=["show", "tap", "shot", "verify", "verify-sync", "verify-sync-wifi", "verify-sync-offline", "verify-sync-mapping-order", "down", "up", "back"])
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
                if value.startswith(("settings_group_", "lan_", "sync_")):
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
               "devices": "lan_pair_manual", "lyrics": "online_lyrics",
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

def verify_sync():
    """Safe navigation only; no pairing submission, file sync, or mapping changes."""
    if nodes("open_settings"): tap("open_settings")
    find("settings_group_library")
    assert any(re.match(r"0 (?:folders?|个目录)", n.get("text", "")) for n in nodes("settings_group_library")[0].iter("node")), "Use an empty isolated library"
    find("settings_group_devices")
    assert not nodes("settings_group_data"), "Removed data/backup category is still visible"
    home_bounds = bounds(nodes("settings_group_devices")[0])
    shot("sync-home")
    tap("settings_group_devices")
    assert not nodes("settings_group_devices"), "Sync did not open"
    assert any(n.get("text") in ("Sync", "同步") for n in dump().iter("node")), "Unified title missing"
    find("lan_pair_manual")
    shot("sync-connection")
    tap("lan_pair_manual")
    assert nodes("lan_invitation"), "Manual pairing dialog is unreachable"
    shot("sync-pair-dialog")
    back()
    find("lan_choose_roots")
    assert nodes("lan_choose_roots")[0].get("enabled") == "false", "Use the unpaired fixture"
    shot("sync-folders")
    find("lan_start")
    assert nodes("lan_cancel"), "Cancel action is unreachable"
    shot("sync-actions")
    find("sync_advanced")
    tap("sync_advanced")
    find("sync_legacy_directory")
    original = nodes("sync_legacy_directory")[0].get("checked")
    assert original in ("true", "false"), "Compatibility setting lost switch semantics"
    shot("sync-advanced")
    # The caller uses an empty, isolated library. Change only the compatibility preference,
    # restore it in finally, and verify its persistence across both back-navigation and restart.
    try:
        tap("sync_legacy_directory")
        assert nodes("sync_legacy_directory")[0].get("checked") != original
        saved_bounds = bounds(nodes("sync_legacy_directory")[0])
        back()
        assert bounds(nodes("settings_group_devices")[0]) == home_bounds, "Overview scroll changed"
        tap("settings_group_devices")
        assert bounds(nodes("sync_legacy_directory")[0]) == saved_bounds, "Sync scroll or expansion lost"
        assert nodes("sync_legacy_directory")[0].get("checked") != original, "Preference lost on return"
        command("shell", "am", "force-stop", A.package)
        command("shell", "am", "start", "-W", "-n", A.package + "/com.tinyfish.jeekmusic.MainActivity")
        time.sleep(1)
        if nodes("open_settings"): tap("open_settings")
        find("settings_group_devices"); tap("settings_group_devices")
        find("sync_advanced")
        if not nodes("sync_legacy_directory"): tap("sync_advanced")
        find("sync_legacy_directory")
        assert nodes("sync_legacy_directory")[0].get("checked") != original, "Preference lost on restart"
    finally:
        if nodes("sync_legacy_directory") and nodes("sync_legacy_directory")[0].get("checked") != original:
            tap("sync_legacy_directory")
    assert nodes("sync_legacy_directory")[0].get("checked") == original
    # Scan every part of the unified page for removed backup controls and the second Sync now.
    seen = set()
    for down in (False, True):
        previous = None
        for _ in range(12):
            root = dump()
            for n in root.iter("node"):
                if n.get("text"): seen.add(n.get("text"))
                assert n.get("resource-id") not in ("backup_now", "restore_from_file", "settings_group_data")
            signature = ET.tostring(root)
            if signature == previous: break
            previous = signature; scroll(down)
    forbidden = ("Back up now", "Restore from backup file", "File recovery backups", "Data sync & backup",
                 "立即备份", "从备份文件恢复", "文件恢复备份", "数据同步与备份")
    assert not seen.intersection(forbidden), "Backup product UI is still exposed"
    assert len(seen.intersection(("Sync now", "立即同步"))) == 1, "Expected one sync command label"
    back()
    find("settings_group_devices")
    shot("sync-return")
    result = dict(single_sync_entry=True, manual_pairing_reachable=True, actions_reachable=True,
                  legacy_preference_persisted_and_restored=True, back_and_scroll_restored=True,
                  removed_backup_controls_absent=True, music_actions_invoked=False)
    (A.output / "sync-results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result))

def verify_sync_mapping_order():
    """Three synthetic, unpaired mappings; verify row/indicator taps and persisted identity."""
    assert A.package.endswith(".debug")
    assert command("emu", "avd", "name").splitlines()[0].strip() == b"JeekLanEmpty35"
    roots = [letter * 32 for letter in "abc"]
    tags = ["lan_mapping_enabled_" + root for root in roots]

    def state():
        config = json.loads(command("exec-out", "run-as", A.package, "cat", "files/lan-sync.json"))
        assert "connection" not in config, "Use unpaired synthetic mappings"
        mappings = config["mappings"]
        assert {m["name"] for m in mappings} == {"Toggle fixture " + letter for letter in "ABC"}
        return mappings

    assert [m["rootId"] for m in state()] == roots
    assert all(not m["enabled"] for m in state())
    if nodes("open_settings"): tap("open_settings")
    find("settings_group_devices"); tap("settings_group_devices")

    def verify_rows(expected):
        assert [m["rootId"] for m in state()] == roots, "Updating a mapping moved its row"
        assert [m["enabled"] for m in state()] == expected, "Another folder's persisted state changed"
        for tag, checked in zip(tags, expected):
            find(tag)
            assert nodes(tag)[0].get("checked") == str(checked).lower(), "Wrong folder switch state"

    expected = [False] * 3
    verify_rows(expected)
    shot("mapping-order-initial")
    # Alternate whole-row and switch-indicator taps, including repeated taps on A.
    for step, (index, indicator) in enumerate([(0, False), (0, True), (1, True), (2, False), (1, False), (2, True)]):
        find(tags[index])
        original_bounds = bounds(nodes(tags[index])[0])
        if indicator:
            x1, y1, x2, y2 = original_bounds
            command("shell", "input", "tap", str(x2 - (x2-x1)//12), str((y1+y2)//2))
            time.sleep(.3)
        else:
            tap(tags[index])
        expected[index] = not expected[index]
        assert bounds(nodes(tags[index])[0]) == original_bounds, "The folder moved under the pointer"
        verify_rows(expected)
        find(tags[index]); shot(f"mapping-order-{step + 1}")
    # Leave a non-default state to distinguish persisted values from defaults.
    find(tags[0]); tap(tags[0]); expected[0] = True
    back(); find("settings_group_devices"); tap("settings_group_devices")
    verify_rows(expected)
    command("shell", "am", "force-stop", A.package)
    command("shell", "am", "start", "-W", "-n", A.package + "/com.tinyfish.jeekmusic.MainActivity")
    time.sleep(1)
    if nodes("open_settings"): tap("open_settings")
    find("settings_group_devices"); tap("settings_group_devices")
    verify_rows(expected)
    find(tags[0]); tap(tags[0]); expected[0] = False
    verify_rows(expected)
    find(tags[0]); shot("mapping-order-after-restart")
    result = dict(folder_order_preserved=True, only_selected_folder_changed=True,
                  row_and_indicator_taps=True, navigation_and_restart_persisted=True, real_library_accessed=False)
    (A.output / "mapping-order-results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result))


def verify_sync_offline():
    """Only for the synthetic offline fixture: closed local port, fake credentials, no SAF grant."""
    assert command("emu", "avd", "name").splitlines()[0].strip() == b"JeekLanEmpty35"
    if nodes("open_settings"): tap("open_settings")
    find("settings_group_devices"); tap("settings_group_devices")
    assert any("Offline UI fixture PC" in n.get("text", "") for n in dump().iter("node")), "Wrong pairing fixture"
    shot("offline-connection")
    find("lan_choose_roots"); tap("lan_choose_roots")
    assert nodes("lan_roots_error"), "Offline directory error is missing"
    shot("offline-folder-error")
    tap("lan_refresh_roots")
    assert nodes("lan_roots_error"), "Retry did not preserve the directory error"
    back()
    current, old = "lan_mapping_enabled_ui-current", "lan_mapping_enabled_ui-old"
    find(old)
    assert nodes(old)[0].get("enabled") == "false" and nodes(old)[0].get("checked") == "false"
    assert any("Old mappings cannot run" in n.get("text", "") or "旧映射已失效" in n.get("text", "") for n in dump().iter("node"))
    shot("offline-old-mapping")
    find(current)
    assert nodes(current)[0].get("checked") == "false"
    tap(current)
    assert nodes(current)[0].get("checked") == "true", "Mapping resume failed"
    find("lan_start")
    assert nodes("lan_start")[0].get("enabled") == "true", "Sync command lost after consolidation"
    shot("offline-actions-ready")
    tap("lan_start")
    find("lan_status")
    shot("offline-sync-issue")
    find("lan_cancel"); tap("lan_cancel")
    find("lan_auto_status")
    assert any("cancelled" in n.get("text", "").lower() or "已取消" in n.get("text", "") for n in dump().iter("node"))
    shot("offline-cancelled")
    command("shell", "am", "force-stop", A.package)
    command("shell", "am", "start", "-W", "-n", A.package + "/com.tinyfish.jeekmusic.MainActivity")
    time.sleep(1)
    if nodes("open_settings"): tap("open_settings")
    find("settings_group_devices"); tap("settings_group_devices")
    find("lan_auto_status")
    assert any("cancelled" in n.get("text", "").lower() or "已取消" in n.get("text", "") for n in dump().iter("node")), "Cancel lost on restart"
    find(current); tap(current)
    assert nodes(current)[0].get("checked") == "false", "Mapping pause failed"
    shot("offline-paused")
    back()
    result = dict(offline_roots_retry_reachable=True, old_mapping_requires_reselection=True,
                  mapping_resume_and_pause=True, sync_now_invoked_against_closed_local_port=True,
                  cancel_persisted_across_restart=True, real_music_library_accessed=False)
    (A.output / "offline-results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result))

def verify_sync_wifi():
    def open_sync():
        if nodes("open_settings"): tap("open_settings")
        if nodes("settings_group_devices"):
            find("settings_group_devices"); tap("settings_group_devices")
        find("sync_wifi_only")

    def wait_for(predicate):
        end = time.monotonic() + 20
        while not predicate():
            assert time.monotonic() < end, "Wi-Fi UI transition timed out"
            time.sleep(.3)

    def waiting():
        return any(n.get("text", "").startswith(("Waiting for Wi-Fi", "等待 Wi-Fi")) for n in nodes("lan_schedule"))

    open_sync()
    original = nodes("sync_wifi_only")[0].get("checked")
    original_wifi = command("shell", "settings", "get", "global", "wifi_on").strip() != b"0"
    try:
        command("shell", "svc", "wifi", "enable")
        if original == "false": tap("sync_wifi_only")
        wait_for(lambda: not waiting())
        shot("wifi-on")
        tap("sync_wifi_only")
        assert nodes("sync_wifi_only")[0].get("checked") == "false"
        shot("wifi-off")
        command("shell", "am", "force-stop", A.package)
        command("shell", "am", "start", "-W", "-n", A.package + "/com.tinyfish.jeekmusic.MainActivity")
        time.sleep(1)
        open_sync()
        assert nodes("sync_wifi_only")[0].get("checked") == "false", "Wi-Fi option lost across restart"
        shot("wifi-off-restarted")
        tap("sync_wifi_only")
        command("shell", "svc", "wifi", "disable")
        wait_for(waiting)
        shot("wifi-waiting")
        command("shell", "svc", "wifi", "enable")
        wait_for(lambda: not waiting())
        shot("wifi-restored")
    finally:
        command("shell", "svc", "wifi", "enable" if original_wifi else "disable")
        if nodes("sync_wifi_only") and nodes("sync_wifi_only")[0].get("checked") != original:
            tap("sync_wifi_only")
    result = dict(realClient=True, toggleOnOff=True, persistedAcrossRestart=True, waitingForWifiVisible=True, wifiReturnClearsWaiting=True)
    (A.output / "wifi-ui-results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result))

if A.action == "show":
    for n in dump().iter("node"):
        if n.get("text", "").startswith("jeeklan://"): continue
        if n.get("text") or n.get("resource-id") or n.get("content-desc"):
            print(json.dumps(n.attrib, ensure_ascii=True))
elif A.action == "tap": tap(A.value)
elif A.action == "shot": shot(A.value)
elif A.action == "verify": verify()
elif A.action == "verify-sync": verify_sync()
elif A.action == "verify-sync-wifi": verify_sync_wifi()
elif A.action == "verify-sync-offline": verify_sync_offline()
elif A.action == "verify-sync-mapping-order": verify_sync_mapping_order()
elif A.action == "back": back()
else: scroll(A.action == "down")
