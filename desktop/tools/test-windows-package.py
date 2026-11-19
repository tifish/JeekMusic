"""Verify the Windows x64 Native AOT release ZIP before uploading it."""

import argparse
from pathlib import PurePosixPath
import struct
from zipfile import ZipFile


def verify(archive):
    with ZipFile(archive) as package:
        files = {PurePosixPath(entry.filename) for entry in package.infolist() if not entry.is_dir()}
        whisper = PurePosixPath("JeekMusic/runtimes/noavx/win-x64")
        required = {PurePosixPath("JeekMusic/JeekMusic.exe")}
        required.update(whisper / name for name in (
            "whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cpu-whisper.dll",
        ))
        if missing := required - files:
            raise ValueError("Missing Windows x64 binaries: " + ", ".join(map(str, sorted(missing))))

        for path in sorted(files):
            suffixes = {suffix.lower() for suffix in path.suffixes}
            if suffixes & {".so", ".dylib", ".wasm", ".a", ".lib"}:
                raise ValueError(f"Unexpected binary in Windows x64 ZIP: {path}")
            if path.parts[:2] == ("JeekMusic", "runtimes"):
                runtime_path = path.parts[2:]
                if runtime_path[:1] == ("noavx",):
                    runtime_path = runtime_path[1:]
                if runtime_path[:1] != ("win-x64",):
                    raise ValueError(f"Unexpected runtime in Windows x64 ZIP: {path}")
            if path.suffix.lower() in (".exe", ".dll"):
                with package.open(path.as_posix()) as binary:
                    header = binary.read(64)
                    if len(header) != 64 or header[:2] != b"MZ":
                        raise ValueError(f"Missing PE header: {path}")
                    binary.seek(struct.unpack_from("<I", header, 60)[0])
                    if binary.read(6) != b"PE\0\0\x64\x86":  # IMAGE_FILE_MACHINE_AMD64
                        raise ValueError(f"Expected a Windows x64 executable or DLL: {path}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive")
    args = parser.parse_args()
    try:
        verify(args.archive)
    except ValueError as error:
        parser.exit(1, f"{error}\n")
    print(f"Verified Windows x64 binaries in {args.archive}")
