# Native tag preservation validation (2026-10-25)

The [shared policy](../../docs/tag-writing-policy.md) defines the target records,
Unicode encodings and safe refusal contract. This change does not retag a library,
download replacement lyrics, migrate databases or touch the previous recovery data.

## Implementation

`TagWriter.Write` keeps the existing production cover/lyric entry point, in-use/CUE
checks in its callers, normal mtime/creation-time preservation and external-save
fallback. It validates Unicode scalars and JPEG input, stages on the same volume,
checks source SHA-256/signature and replaces only after successful validation.
Bounded Windows sharing retries recheck the source rather than overwriting a change.

The small native profiles work with framed records, not a new universal tag/audio
model. ID3, FLAC, RIFF/FORM and APEv2 copy non-target records and encoded audio from
the original. Ogg edits only single-stream, separate codec-header pages, validates
CRC, preserves original comment vectors and verifies all other packets/granules.
Unsupported flags, malformed lengths, ambiguous lyric slots and over-limit inputs
refuse before replacement. ID3v2.3 stays v2.3 with UTF-16 target frames; no pretend
version upgrade or normalization of unrelated old text is performed.

MP4 and ASF use ATL 7.18.0's public native partial-update API through its format
factory, avoiding `Track.Save`'s full high-level text/date projection. Experiments
on synthetic files also found that even this partial API can reorder non-target
bilingual LRC. Only its generated target atoms/descriptors are inserted into the
original envelope. Original unknown/typed records and non-target lyrics are retained.
MP4 ancestor sizes and internal sample offsets are adjusted and independently
validated against unchanged `mdat`; ASF object counts/sizes and the stale native
File Properties file-size field are adjusted from known lengths and checked.

`get_diagnostics.tagWriting` and existing `get_logs` expose the same production
policy/limits/result/reason in managed and NativeAOT builds without lyric content.
No raw write bypass, development-only API or MCP input-schema change was added.

## Automated verification

All media in the tracked shared corpus is synthetic; no user audio or downloaded
lyrics/artwork is checked in. [Fixture provenance and limits](../../testdata/tag-writing/README.md).

* Full Release App suite: **485 passed, 1 skipped**. The skip is the existing opt-in
  real-endpoint complete-format test, not an unreported pass.
* Full Core suite: **70 passed**. Workflow constraint tests: **7 passed**.
* Independent raw parsers inspect encoded audio and the entire raw bilingual LRC,
  including millisecond times, empty marker and offset tag, not only rendered text.
* Cover-only / lyric-only / combined cases include ID3v2.3/v2.4, untagged MP3,
  FLAC, Vorbis, Opus, ordinary/fast-start/untagged MP4, WAV, AIFF, ASF and WavPack.
  Original ID3v1 bytes, year-only dates, multivalues, PRIV/TXXX, non-ASCII back-cover
  descriptions and opaque native binary records are protected.
* Failure cases include malformed/stacked/unsupported ID3, invalid Unicode,
  multiple lyric slots, locked sources, native checksum/unknown-atom corruption,
  read-only APE items and PCM/WavPack audio containing a possible `TAG` footer.
  Large lyrics exercise multi-page Vorbis/Opus pagination with unchanged audio.

NativeAOT builds successfully with the installed Visual Studio toolchain (the local
shell needed the existing VS Installer directory on PATH so SDK `vswhere` discovery
did not contaminate the linker path). The only emitted AOT/trim warnings were the
existing Avalonia DataGrid assembly summaries, not tag-writing diagnostics.

## Actual NativeAOT client business path

An isolated, real **1.0.163 NativeAOT** client imports **39 synthetic files** using
production MCP `add_folder`. Each source layout has separate cover-only,
lyrics-only and combined sidecar inputs. The normal library worker `EmbedPrevious`
uses production cover/lyric services and `TagWriter`; no mock or special test API
performs the save. Online requests, automatic analysis/global hotkeys and automatic
device-format changes are disabled only in this isolated client's settings.

All 39 outputs change as requested, keep mtime, consume their downloaded sidecars
through normal business cleanup, and pass independent encoded-audio/raw-LRC checks.
The NativeAOT MCP diagnostics confirm `dynamicCodeSupported=false`, version 1.0.163,
and the same tag-writing policy. Evidence is local/ignored:

* `desktop/artifacts/tag-write-policy/runtime-final/{before-manifest,after-manifest}.json`
* `runtime-final/independent-verification.json`, `final-diagnostics.json`, `logs.json`
* `desktop/artifacts/tag-write-policy/results/{app-final,core-full}.trx`

## Real UI limitation and user state

Windows computer-use `list_windows` fails with **native pipe unavailable, os error 2**.
A bounded retry plus the skill's fresh JS initialization has the same error. No
authentication/lock-screen workaround, alternate Win32 screenshot helper, guessed
UI input or emulator access was attempted. Consequently **real UI actions/screenshots
are not accepted**; the client/MCP/native-file business checks above do not substitute
for visible UI validation. The former AccessDenied/D3D screenshot failures are not
claimed fixed.

Before replacing the daily client, capture the latest production MCP state and a
consistent local DB backup, settings/snapshot and all 1684 library file signatures.
Preserve the latest queue/manual entries/source, current song/position/play intent,
shuffle/repeat and saved view/layout; never restore an old whole snapshot over newer
actions. The local `restart-final` evidence records the actual captured/restored
state and process IDs. Previously recovered 24 files and all 37 permanent recovery
backups are hash-checked unchanged; LaFee's three unresolved punctuation characters
and the unmatched Liang Zhu lyric remain outside this implementation task.

APE/TAK/Musepack share the bounded APEv2 metadata profile, but no encoded codec
fixture for those three is present: only WavPack has actual encoded-payload tests.
Large/complex envelopes explicitly fall back as documented. Android's independent
JNI/device results and additional platform restrictions must be integrated before
a combined cross-platform release. This stage does **not push or publish**.
