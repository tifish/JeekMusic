# Android file modification time precision

The Android song list now caches the fractional second from the actual audio file
alongside the existing MediaStore Unix-second timestamp. The scanner opens the
MediaStore content URI read-only and uses the existing native `fstat` primitive;
it does not read or hash audio contents. It accepts the fraction only when file
size and whole seconds match the MediaStore row. Inaccessible or unsupported
descriptors, and stale MediaStore rows, fall back to whole seconds.

Room version 8 adds nullable `track.modified_nanos`. Incremental scans fill or
refresh this field for existing songs (including every CUE row) without parsing
tags again, replacing IDs, clearing the library, or triggering model training.
The normal startup scan performs the backfill after an upgrade. Sync's existing
file refresh also carries the fraction through `TrackFactory`.

Modified sorting compares valid whole seconds newest first, then fractional
nanoseconds newest first, then the existing filename/CUE ordering. Unknown dates
stay last. No file I/O occurs during sorting or UI binding. Other sort modes,
date display, sync timestamps, hashes and learning signatures are unchanged.
Windows already uses high precision mtime; no Windows binary or MCP contract
change is necessary for this Android-only fix.

This resolves the observed same-second ordering discrepancy. It does not promise
identical lists when filters, chosen sort modes, folder scopes or available files
differ, or when a storage provider only supplies coarse timestamps. Existing
platform-specific transliteration/folder tie rules have not been replaced.

## Original read-only device comparison

On the connected physical phone running 1.0.235, 1,586 songs matched their PC
relative paths, sizes and whole-second timestamps. Two directly inspected files
also retained identical fractional timestamps on both filesystems:

- 周深 - 花火 (Live版).mp3: 2026-10-27T11:15:44.3189158Z
- 卫兰 - 一格格.mp3: 2026-10-27T11:15:44.1887647Z

The old Android MediaStore-backed sort reduced both to 1793099744 seconds.
The new comparator retains their chronological order, independently of filename.

## Verification

- 122 JVM tests passed, including same-second reverse-filename sorting, invalid
  timestamp fallback and stale MediaStore identity rejection.
- Debug APK and instrumentation APK built; lint: 0 errors (85 warnings).
- API35 x86_64: 6 instrumentation tests passed, including real private/external
  timestamps, public Music MediaStore scanning and Room roundtrip, and migrations
  through version 8 preserving corrections and queue entries.
- Actual Debug UI showed `z-newer` above `a-older` for timestamps .318915800 and
  .188764700 within the same second. [Screenshot](../output/mtime-precision/order.png).
  Only isolated emulator fixture files were created; they were removed afterwards.
- Windows and its MCP were unchanged; no desktop restart or production library
  file write was required. Formal signed package delivery is recorded separately.

## Signed delivery

- Source commit: `640ca4fc876bca81060e1bdc181efe3d14b166f7`, version `1.0.237`.
- Local minified signed APK: `output/mtime-precision/JeekMusic-1.0.237.apk`.
  SHA-256: `6379b68b1c1be468a7570f3ac9667fb7fe7ac30b63075c1764cd83155cd544a9`.
- Signing certificate SHA-256:
  `28a86a1affb4a31ed0a2a14e602b2ebed23d1c4385b366e9ccfac3eaf5a972fe` (unchanged).
- Same-signature `install -r` on the API35 emulator succeeded. The formal app was
  stopped and started, its Chinese home screen was inspected, and original first
  install time remained 2026-10-31 10:44:42. No uninstall or clear-data operation.
- The connected physical phone rejected the ADB installation with
  `INSTALL_FAILED_ABORTED: User rejected permissions`. It remains at 1.0.235 with
  its original last-update time. No bypass was attempted; ARM/OEM execution of
  this new version is therefore not verified. The user can install through the
  existing in-app update flow or use the signed APK.
- [Mobile CI](https://github.com/tifish/JeekMusic/actions/runs/41847466400)
  completed successfully at the source SHA above. `v1.0.237` and the release
  `target_commitish` both resolve to that SHA.
- [Published APK](https://github.com/tifish/JeekMusic/releases/download/v1.0.237/JeekMusic-1.0.237.apk)
  SHA-256: `54ffc80a8d4764dc3bc069b820d7bb963dcdcf1e584a1409b21787e31371403b`.
  Downloaded bytes match GitHub's asset digest; package/version and unchanged
  release certificate were verified. This CI APK also completed emulator
  `install -r` and cold start. Local and CI builds are recorded separately;
  they are not claimed byte-identical.
