#!/usr/bin/env python3
"""Drive the signed instrumentation update bridge on an explicitly selected emulator.

This never presses an Android install confirmation. The initial app/test APKs must already
be installed. `install` stages a local APK and invokes production UpdateInstaller in the app
UID; `diagnostics` and `capture` are read-only. See docs/android-update.md.
"""
import argparse
import json
import pathlib
import re
import subprocess
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--serial", required=True, help="Explicit emulator serial; no physical-device default")
    parser.add_argument("--package", default="com.tinyfish.jeekmusic")
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("action", choices=["install", "diagnostics", "cancel", "recover", "capture", "validate"])
    parser.add_argument("--apk", type=pathlib.Path)
    args = parser.parse_args()
    if not args.serial.startswith("emulator-"):
        parser.error("This regression harness only operates on emulators")
    args.output.mkdir(parents=True, exist_ok=True)

    def adb(*command, binary=False):
        return subprocess.run(["adb", "-s", args.serial, *command], check=True, capture_output=True,
                              encoding=None if binary else "utf-8", errors=None if binary else "replace",
                              timeout=60).stdout

    def capture(label):
        (args.output / f"{label}.png").write_bytes(adb("exec-out", "screencap", "-p", binary=True))
        adb("shell", "uiautomator", "dump", "/data/local/tmp/jeek-update-ui.xml")
        xml = adb("shell", "cat", "/data/local/tmp/jeek-update-ui.xml")
        (args.output / f"{label}.xml").write_text(xml, encoding="utf-8")
        return xml

    before = adb("shell", "dumpsys", "package", args.package)
    if args.action == "capture":
        capture("screen")
        return
    command = ["shell", "am", "instrument", "-w", "-r", "-e", "class",
               "com.tinyfish.jeekmusic.update.SelfUpdateDeviceTest#" +
               ("validatesArchiveAndRejectsForgedResults" if args.action == "validate" else "bridge")]
    if args.apk:
        staged = "/data/local/tmp/jeek-update-fixture.apk"
        adb("push", str(args.apk.resolve()), staged)
        adb("shell", "chmod", "644", staged)
        command += ["-e", "updateApk", staged]
    if args.action == "install" and not args.apk:
        parser.error("install requires --apk")
    command += ["-e", "updateAction", args.action,
                args.package + ".test/androidx.test.runner.AndroidJUnitRunner"]
    started = time.time()
    result = adb(*command)
    (args.output / "instrumentation.txt").write_text(result, encoding="utf-8")
    diagnostics = [json.loads(line.split("updateDiagnostics=", 1)[1]) for line in result.splitlines()
                   if "updateDiagnostics=" in line]
    if diagnostics:
        (args.output / "diagnostics.json").write_text(json.dumps(diagnostics[-1], indent=2, ensure_ascii=False), encoding="utf-8")
    after = adb("shell", "dumpsys", "package", args.package)
    summary = {"action": args.action, "elapsedSeconds": round(time.time() - started, 3),
               "beforeVersions": re.findall(r"versionCode=(\d+)", before),
               "afterVersions": re.findall(r"versionCode=(\d+)", after),
               "instrumentationCompleted": "OK (" in result}
    (args.output / "result.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary))
    if args.action != "install" and not summary["instrumentationCompleted"]:
        raise SystemExit("Instrumentation failed; inspect instrumentation.txt")
    # A self-update may kill instrumentation. This is not a passing test result: capture the
    # real UI and subsequently read version + journal using a fresh diagnostics invocation.
    capture("after")


if __name__ == "__main__":
    main()
