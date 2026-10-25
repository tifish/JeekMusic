"""Actual Android UI operations; restricted to this task's isolated AVD. Never capture pairing text."""
import argparse
import re
import subprocess
import sys
from pathlib import Path
from xml.etree import ElementTree as ET

ADB = ["adb", "-s", "emulator-5582"]
sys.stdout.reconfigure(encoding="utf-8")

def command(*args):
    return subprocess.check_output(ADB + list(args))

def guard():
    assert command("emu", "avd", "name").splitlines()[0].strip() == b"JeekLanSync35", "Wrong emulator"

def dump():
    result = command("shell", "uiautomator", "dump", "/sdcard/lan-ui.xml")
    assert b"dumped to" in result, "No fresh UI observation; do not act on an old dump"
    return ET.fromstring(command("exec-out", "cat", "/sdcard/lan-ui.xml"))

def show():
    root = dump()
    # Do not dump an EditText containing a sensitive invitation.
    for n in root.iter("node"):
        a = n.attrib
        if a.get("text", "").startswith("jeeklan://"): continue
        if a.get("text") or a.get("resource-id") and a.get("package") == "com.tinyfish.jeekmusic.debug" or a.get("checkable") == "true":
            print({k: a[k] for k in ["text", "resource-id", "bounds", "checked", "enabled"]})

def tap(value):
    root = dump()
    nodes = [n for n in root.iter("node") if value in (n.get("text"), n.get("resource-id"), n.get("content-desc"))]
    assert len(nodes) == 1, f"Expected one UI match for {value!r}, got {len(nodes)}"
    b = [int(x) for x in re.findall(r"\d+", nodes[0].get("bounds"))]
    assert b[2] > b[0] and b[3] > b[1]
    command("shell", "input", "tap", str((b[0]+b[2])//2), str((b[1]+b[3])//2))

if __name__ == "__main__":
    p=argparse.ArgumentParser();p.add_argument("action",choices=["show","tap","shot","down","up","back"]);p.add_argument("value",nargs="?");a=p.parse_args();guard()
    if a.action=="show":show()
    elif a.action=="tap":tap(a.value)
    elif a.action=="shot":
        assert not any(n.get("text", "").startswith("jeeklan://") for n in dump().iter("node")), "Sensitive pairing field on screen"
        Path(a.value).write_bytes(command("exec-out","screencap","-p"))
    elif a.action=="back":command("shell","input","keyevent","4")
    else:command("shell","input","swipe","550","1900" if a.action=="down" else "700","550","700" if a.action=="down" else "1900","450")
