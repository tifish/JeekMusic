"""Windows build/publish round trips; stop the daily player before running.

Default EXE/MCP checks only launch clients and read diagnostics; no playback or
output switching. --audio and --exclusive explicitly opt into real audio tests.
Silent audio / application mute do NOT prevent exclusive device ownership.
--build-only never launches a client.
Logs stay in desktop/artifacts.
By default also publishes NativeAOT (requires MSVC); CI can reuse --published-exe.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time


ROOT = Path(__file__).resolve().parents[2]
PROJECT = "desktop/src/JeekMusic.App"
OUTPUT = ROOT / "bin"
LOGS = ROOT / "desktop/artifacts" / ("build-isolation-" + time.strftime("%Y%m%d-%H%M%S"))
ENV = {**os.environ}
INSTALLER = Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)")) / "Microsoft Visual Studio/Installer"
ENV["PATH"] += os.pathsep + str(INSTALLER)
MCP_OPTIONS = ["--startup-only"]
CATALOGS = []
BUILD_ONLY = False


def run(label, command, *, env=None, expected=0, stdin=None):
    print(f"{label}: {' '.join(map(str, command))}", flush=True)
    result = subprocess.run(list(map(str, command)), cwd=ROOT, env=env or ENV,
                            input=stdin, capture_output=True, text=True, encoding="utf-8",
                            errors="replace", timeout=1200)
    (LOGS / (label + ".log")).write_text(
        f"command={command!r}\nexit={result.returncode}\nstdout:\n{result.stdout}\nstderr:\n{result.stderr}",
        encoding="utf-8")
    assert result.returncode == expected, (label, result.returncode, result.stdout[-4000:], result.stderr[-4000:])
    print(f"PASS {label}: exit={result.returncode}", flush=True)
    return result


def build(label, *options):
    run(label, ["dotnet", "build", PROJECT, "--verbosity", "minimal", *options])


def properties(*options):
    result = run("paths-" + str(properties.counter), ["dotnet", "msbuild", PROJECT, "-nologo",
                 "-getProperty:TargetDir,BaseIntermediateOutputPath,OutputPath,PublishDir", *options])
    properties.counter += 1
    return json.loads(result.stdout)["Properties"]


properties.counter = 0


def daily_signature():
    assert (OUTPUT / "runtime/hostfxr.dll").is_file()
    assert (OUTPUT / "runtime/coreclr.dll").is_file()
    assert (OUTPUT / "Models/yamnet.onnx").is_file()
    assert (OUTPUT / "portable.json").is_file()
    assert not list((OUTPUT / "runtime").rglob("*.pdb"))
    assert not list((OUTPUT / "runtime").rglob("*.lib"))
    config = json.loads((OUTPUT / "runtime/JeekMusic.runtimeconfig.json").read_text())
    assert config["runtimeOptions"].get("includedFrameworks")
    return {name: hashlib.sha256((OUTPUT / name).read_bytes()).hexdigest() for name in
            ("JeekMusic.exe", "runtime/JeekMusic.dll", "runtime/JeekMusic.runtimeconfig.json", "runtime/JeekMusic.deps.json")}


def daily_build(label, configuration):
    build(label, "-c", configuration, "-r", "win-x64", "--self-contained", "true",
          "-p:PublishAot=false", "-p:JeekStageDailyBuild=true")


def smoke(label, exe, *, aot=False):
    trace = LOGS / (label + "-host-trace.log")
    env = {**ENV, "DOTNET_DISABLE_GUI_ERRORS": "1", "DOTNET_HOST_TRACE": "1",
           "DOTNET_HOST_TRACEFILE": str(trace)}
    if aot:
        assert not exe.with_suffix(".runtimeconfig.json").exists()
        assert not (exe.parent / "hostfxr.dll").exists()
        # Disable environment-based runtime discovery in just this subprocess tree.
        env.update(DOTNET_ROOT=str(LOGS / "no-dotnet"), DOTNET_ROOT_X64=str(LOGS / "no-dotnet"),
                   DOTNET_MULTILEVEL_LOOKUP="0", JEEK_EXPECT_DYNAMIC_CODE="false")
    if BUILD_ONLY:
        assert exe.is_file()
        print(f"SKIP {label}: --build-only forbids client startup", flush=True)
        return
    catalog_path = LOGS / (label + "-catalog.json")
    run(label, [sys.executable, "desktop/tools/test-mcp.py", "--exe", exe,
               "--catalog", catalog_path, *MCP_OPTIONS], env=env)
    catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
    if CATALOGS:
        assert catalog == CATALOGS[0], "MCP tools/descriptions/schemas differ between builds"
    CATALOGS.append(catalog)
    if aot:
        assert not trace.exists(), "NativeAOT unexpectedly invoked the .NET host"


def main():
    global BUILD_ONLY
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--published-exe", type=Path, help="Reuse an existing NativeAOT EXE (CI)")
    parser.add_argument("--startup-only", action="store_true", help="Explicit alias for the default EXE/MCP startup checks")
    parser.add_argument("--build-only", action="store_true", help="Build/static checks only; never launch any client")
    parser.add_argument("--audio", action="store_true", help="Opt into real shared WASAPI tests (not for concurrent gaming)")
    parser.add_argument("--exclusive", action="store_true", help="With --audio, opt into exclusive tests that can silence other apps")
    args = parser.parse_args()
    if args.exclusive and not args.audio:
        parser.error("--exclusive requires --audio")
    if (args.startup_only or args.build_only) and args.audio:
        parser.error("--audio cannot be combined with --startup-only or --build-only")
    BUILD_ONLY = args.build_only
    MCP_OPTIONS.clear()
    if args.audio:
        MCP_OPTIONS.append("--audio")
    else:
        MCP_OPTIONS.append("--startup-only")
    if args.exclusive:
        MCP_OPTIONS.append("--exclusive")
    LOGS.mkdir(parents=True)
    print(f"Evidence: {LOGS}", flush=True)
    daily_build("01-debug", "Debug")
    smoke("02-debug-mcp", OUTPUT / "JeekMusic.exe")
    debug_signature = daily_signature()

    sc_options = ["-p:Configuration=Release", "-p:RuntimeIdentifier=win-x64",
                  "-p:SelfContained=true", "-p:PublishAot=false"]
    sc = properties(*sc_options)
    build("03-rid-self-contained", "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishAot=false")
    assert daily_signature() == debug_signature, "RID build replaced the daily entry point"
    sc_exe = Path(sc["TargetDir"]) / "JeekMusic.exe"
    smoke("04-self-contained-mcp", sc_exe)

    # Ordinary builds never replace the portable player, even without an RID.
    build("05-ordinary-framework-dependent", "-c", "Release")
    assert daily_signature() == debug_signature
    run("06-layout-migration", ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
                               "-File", "desktop/tools/test-runtime-layout.ps1"])
    daily_build("06-release-daily", "Release")
    smoke("07-release-mcp", OUTPUT / "JeekMusic.exe")
    release_signature = daily_signature()

    fdd_options = ["-p:Configuration=Release", "-p:RuntimeIdentifier=win-x64",
                   "-p:SelfContained=false", "-p:PublishAot=false"]
    fdd = properties(*fdd_options)
    assert sc["BaseIntermediateOutputPath"] != fdd["BaseIntermediateOutputPath"]
    build("08-rid-framework-dependent", "-c", "Release", "-r", "win-x64", "--self-contained", "false", "-p:PublishAot=false")
    assert daily_signature() == release_signature
    smoke("09-rid-framework-dependent-mcp", Path(fdd["TargetDir"]) / "JeekMusic.exe")

    if args.published_exe:
        aot_exe = args.published_exe.resolve()
    else:
        aot_dir = ROOT / "desktop/artifacts/build-isolation-publish"
        run("10-native-aot-publish", ["dotnet", "publish", PROJECT, "-c", "Release", "-r", "win-x64", "-o", aot_dir])
        aot_exe = aot_dir / "JeekMusic.exe"
    assert daily_signature() == release_signature, "Publish replaced the daily entry point"
    smoke("11-native-aot-mcp", aot_exe, aot=True)

    # Explicit daily incremental builds must recopy the correct configuration after
    # publish / RID builds, and even if the SDK decides no compilation is needed.
    daily_build("12-debug-return", "Debug")
    assert daily_signature() == debug_signature
    smoke("13-debug-return-mcp", OUTPUT / "JeekMusic.exe")
    daily_build("14-release-return", "Release")
    assert daily_signature() == release_signature
    smoke("15-release-return-mcp", OUTPUT / "JeekMusic.exe")

    for label, verb in (("16-reject-build-output", "build"), ("17-reject-publish-output", "publish")):
        result = run(label, ["dotnet", verb, PROJECT, "-c", "Release", "-o", OUTPUT], expected=1)
        assert "reserved for the staged daily player" in result.stdout
        assert daily_signature() == release_signature
    print("PASS: build/publish/RID/deployment/configuration isolation, legacy migration" +
          ("; EXE/MCP checks SKIPPED (--build-only)" if BUILD_ONLY else ", real EXE/MCP round trips"), flush=True)


if __name__ == "__main__":
    main()
