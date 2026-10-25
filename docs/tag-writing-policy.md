# Audio tag writing policy — shared desktop / Android contract

Policy version: **3**. This applies to a requested cover/lyrics edit, not a library-wide
retag, charset repair, lyric download/matching decision, or implicit ID3 migration.
Each platform must document its actual implemented/validated subset. Recognizing an
extension or a library advertising `save()` is not proof of safe writing.

## Common invariants

* `cover = null` preserves every picture; `lyrics = null` preserves every lyric record,
  including translations, language/description slots, original timestamp precision,
  offsets, blank lines, and literal question marks/U+FFFD. Never download or repair a
  non-target field during an embed. Existing legacy text bytes are not auto-corrected.
* The cover target is front cover/generic artwork, or the native artwork field where
  that container has no role metadata. Preserve other roles. A lyrics target is one
  unambiguous unsynchronized text slot. Multiple language/description slots or SYLT
  require explicit selection; a writer may safely refuse instead of deleting them.
* New text is Unicode scalars, with no replacement fallback. New lyric text retains
  the entire supplied bilingual LRC, not just the renderer's main-language lines.
  No matching/source switch/time shift is implied. User per-track offset is separate.
* Preserve non-target values, multivalue cardinality/order, dates at their original
  precision, native typed values, unknown/private data, other images, audio packets,
  codec configuration, sample locations and timelines. Where local record updates
  are available, preserve the non-target raw record bytes too.
* Preserve an existing ID3v1 footer's **entire original 128 bytes**, and never put UTF-8
  into ID3v1. Do not infer a footer from `TAG` inside RIFF/FLAC/MP4/audio payloads.
* Stage on the same volume. Validate output independently of the serializer, then
  recheck source identity/signature and full SHA-256 under an exclusive write/delete
  guard before committing. Never overwrite an external replacement, even after the
  last hash check or during a retry wait. Aborted edits preserve original bytes/times;
  an external replacement retains ownership of its pathname. Preserve
  creation/mtime for ordinary cover/lyric embedding; explicit recovery is a separate
  authorized operation. Never write a shared CUE image for a single CUE track.
* Refuse malformed, unsupported, encrypted/ambiguous or over-limit envelopes. Reuse
  the product's existing sidecar/hidden-folder/cache fallback and expose the reason;
  do not silently weaken preservation or add competing tagging systems.

## Native standards and version policy

| Audio/container | Native edit policy | Desktop read / write entry points |
|---|---|---|
| MP3 | New tag: ID3v2.4, UTF-8 target text. Existing v2.4: target APIC/USLT only; opaque non-target frames. Existing v2.3: retain v2.3 and use UTF-16 with BOM in new target frames. **No automatic upgrade.** | ATL reads; bounded ID3 envelope edit writes |
| FLAC | UTF-8 Vorbis comments, native PICTURE blocks; preserve vendor, repeated comments, APPLICATION, CUESHEET, SEEKTABLE, STREAMINFO and audio | ATL reads; local metadata-block edit writes |
| Ogg Vorbis / Opus (`ogg`, `oga`, `opus`) | UTF-8 native comments, `METADATA_BLOCK_PICTURE`; preserve all other vectors/vendor/suffix and all codec/audio packets/granules | ATL reads; bounded single-stream header-page edit, CRC and packet/timeline validation |
| MP4/M4A (AAC/ALAC) | Native `ilst` typed `data` atoms, `covr` artwork and `©lyr`; preserve integer/binary types and non-target atoms; validate relocated chunk offsets relative to unchanged `mdat` | ATL public native partial API generates targets; retain original non-target atoms; independent validation |
| APE / WavPack / TAK / Musepack | APEv2 UTF-8 text / binary cover items, preserve other item bytes and original ID3v1 | ATL reads; bounded native APEv2 item edit writes |
| WAV/BWF | Preserve RIFF INFO/BEXT and unknown chunks in their original representation; Unicode cover/lyrics in an embedded ID3 chunk. Never force UTF-8 into INFO/BEXT. | ATL reads; RIFF chunk envelope edit writes |
| AIFF/AIFC | Preserve native NAME/AUTH/ANNO/etc.; Unicode targets in an ID3 chunk; FORM uses big-endian chunk lengths | ATL reads; FORM chunk envelope edit writes |
| WMA/ASF | Native UTF-16LE descriptors and typed binary pictures; preserve non-target descriptors, objects, data packets and indexes | ATL public native partial API generates targets; retain original non-target descriptors/objects; independent validation |
| Raw AAC (`aac`) | Read-only: no new tagging scheme is invented for elementary AAC | Scanner/ATL read; no embed write |

Desktop previously advertised all write extensions above with unguarded ATL `Track.Save`.
Current capability remains conditional on the actual envelope, not the filename.
The scanner does not currently enumerate `.mp4`, though direct writer calls recognize
it as before; this task does not widen scanning. Existing `.wma` files that are actually
MPEG are identified by content and use the MPEG profile.

ID3v2.3 and v2.4 differ in frame lengths, flags, dates, involved-people frames, text
encoding and multivalue rules. Changing a header byte or preserving incompatible
v2.3 records under a v2.4 header is forbidden. A future migration needs a separate
complete mapping/unknown-frame policy; it is not needed for artwork or lyrics.

## Desktop bounded subset / refusal conditions

* ID3v2.2, extended headers, tag-wide unsynchronization, footer tags, stacked tags,
  discard-on-alteration flags, complex target frames, multiple USLT or any SYLT on a
  lyric edit are refused. Unknown/non-target frames remain opaque and are retained.
  A named USLT slot requires an explicit future selection API. Covers alone can
  preserve all existing multilingual/synchronized lyric frames.
  CHAP/CTOC (including embedded chapter graphs), SEEK, ASPI and MLLT refuse for any
  edit: their absolute/index addressing is not relocated by this bounded profile.
* FLAC has one STREAMINFO and at most one Vorbis-comment block; repeated ordinary
  keys are retained byte-for-byte. Ambiguous multiple LYRICS fields are refused.
* Ogg supports one serial/logical stream, separate codec-header/audio pages, Vorbis or
  Opus. Chained/multiplexed streams, legacy COVERART and malformed CRC/lacing refuse.
  Header pagination and page sequence/CRC may change; audio packets/granules do not.
  Every page must have valid flags, sequence, serial, continuation, CRC and EOS.
  A page completing no packet has granule -1 (all FF), including large comment pages.
* MP4 supports one `moov` and `mdat`, internal chunk locations; fragmented/external
  sample layouts refuse. Protected sample entries, CENC auxiliary/protection data
  (`saio`, `saiz`, `senc`, `sinf`, `tenc`, `pssh`), encryption/unknown sample groups,
  item-location/fragment/segment indexes (`iloc`, `sidx`, `tfra` etc.), compressed or
  reference movies and unclassified UUID boxes refuse. Only known audio sample-entry
  envelopes and self-contained `dref` are accepted; only `stco`/`co64` are relocated.
  ATL output that changes any non-target typed value or
  unknown atom refuses. The writer inserts only generated target atoms into the original
  movie envelope; it does not accept incidental rewrites of existing lyrics/date atoms.
  Zero padding/container size/validated chunk offsets may change.
* APEv2 envelopes only; existing read-only targets/unsupported tag flags refuse.
  WavPack block lengths must place tag/footer bytes outside encoded audio; a `TAG`
  inside a block refuses. Other APE families with only a possible ID3v1 footer and
  no proven native APE boundary refuse rather than insert metadata inside audio.
  WAV/AIFF require standard bounded RIFF/FORM lengths and one embedded ID3 chunk;
  RF64 and conflicting/multiple ID3 chunks refuse. PCM ending in `TAG` is ordinary PCM.
* ASF retains exact native descriptor bytes outside targets; only generated target
  descriptors are inserted into the original objects. Header/object counts and the
  structural File Properties file-length field are updated and checked. Native
  serialization is never treated as a reason to
  convert ASF Unicode descriptors or BWF ASCII fields to UTF-8.
* Initial bounded editing limit is 256 MiB per file / 32 MiB metadata. Larger files
  use the existing fallback rather than unbounded allocations. This is an explicit
  implementation limit, not a format limitation.

`get_diagnostics.tagWriting` reports policy version, limits, last detected native
profile and saved/rejected result/reason without lyric content. `get_logs` exposes
the same production outcomes. Debug/Release/NativeAOT have the same behavior.

### Windows no-clobber commit and interruption boundary

Windows path-based `ReplaceFile` has no expected-file-identity compare-and-swap.
Desktop policy 3 therefore prepares native filesystem metadata while the source
name still exists: `CopyFile` to a private template and `ReplaceFile` there, without
ignore-merge/ACL-error flags, preserve ADS. It explicitly clones owner/group/DACL/
audit SACL, including protection and inheritance control bits, and checks all those
values independently. `SetKernelObjectSecurity` retains the non-auto-inherited
DACL case that the high-level setter otherwise normalizes. All work stays in the
same-volume staging directory; the public source is never a `ReplaceFile` target.

The **expected candidate hash comes from the independently validated output bytes**,
not a later unvalidated reread. After preparation/flush it captures candidate file
identity. At commit, read/DELETE handles deny write/delete sharing on **both** source
and candidate. Verify volume/file ID, length/times, full SHA and filesystem metadata
again under those guards. Replacing or truncating the candidate in the earlier
validation-to-open window refuses; ordinary writers/renamers cannot alter either
guarded object. Privileged/kernel manipulation and ancestor-junction replacement
are not certified by this ordinary local-filesystem writer contract.

Before changing names, persist a write-through, flushed JSON journal in
`<dataDirectory>/tag-transactions/`: absolute source/candidate/reserved-original
paths, both expected identities/hashes and the filesystem-metadata digest. Then
reserve the exact original by handle and rename the candidate to the source name
**without replacement**. The normal brief pathname gap contains no full-file hash,
metadata copy or journal serialization. Every retry rechecks both objects and name
vacancy after waiting. An external newly created source path always wins.

This is **not a continuously visible atomic replacement**. Process termination can
leave a missing name, but the mapping now exists before that is possible. Production
startup recovers before library initialization; every scanner also recovers before
enumerating or deleting records. A missing name restores the exact original object
by no-replace rename. An already committed candidate is independently verified
before finalizing cleanup. Different identities, hashes, metadata, unavailable
storage or malformed/unreadable journals are retained as pending, never overwritten;
affected scans defer instead of deleting missing records. Ordinary `TagWriter.Write`
also recovers before opening its source; existing MCP `rescan_library` retries this
same recovery path. Diagnostics expose pending count/problem and logs identify the
journal. Successful cleanup deletes only expected, verified objects by handle.
Recovery is idempotent. Power-loss/storage-firmware durability is not claimed by
the process-kill tests; ambiguous states remain pending. Old policy-2 `.original`
files lacking a mapping are never guessed back into the library or auto-deleted.

Filesystem subset: ordinary attributes (archive/hidden/system/not-content-indexed/
temporary), complete readable/copyable security descriptors and bounded named data
streams (32 MiB total). Read-only, encrypted/compressed/sparse/reparse/offline/cloud
flags, pre-existing DOS short-name aliases, object-ID/EA/other unsupported backup
records, unknown stream-enumeration support or insufficient security rights refuse
before source changes, using the normal external-file fallback. No elevation or
weakened ACL is used to force a write. Last-access effects of reading are not a
promise of preservation. Hard-link aliases retain old bytes, as with replacement.

Position-dependency review: FLAC seek offsets are relative to the first unchanged
audio frame; APEv2 updates do not move encoded audio; standard RIFF cues address
samples/data, not the file's metadata start; ASF standard indexes address the Data
Object/packets and the entire data/index suffix is copied unchanged. Preserving
opaque bytes cannot prove arbitrary undocumented vendor pointers: MP4 UUIDs and
the known unsafe structures above are refused, not certified by an `mdat` hash.

## Cross-platform test contract

Use only synthetic silence/tones and synthetic text/pictures in `testdata/tag-writing`.
For each implemented profile run cover-only, lyrics-only and combined operations.
Verify with a separate raw parser: Unicode scalars (Chinese, accents, Arabic, Greek,
non-BMP CJK, emoji), target native encoding marker/type, year-only dates, repeated
artists, unknown binary/private data, alternate images, bilingual timed lyrics,
millisecond timestamps, blank lines and offset tags. Hash encoded audio payload,
not just decoded PCM. Native container sizes/CRC/indexes must still be valid.
Run no-tag creation and existing-tag updates, legacy ID3v1 preservation, v2.3 without
upgrade, v2.4 unknown records and failure transactions (malformed/unsupported,
locked source, serializer/verification failure). A safe refusal is an explicit
expected result for a marked complex fixture, not a passing write test.

Each platform reports fixture IDs, successful/refused operations, independent
payload/tag evidence and actual device/UI coverage. Do not call a mock/host-only
native test a device pass. See the fixture manifest and platform validation reports.

### Platform capability differences

Android's current declared set is MP3, FLAC, Vorbis/Opus and MP4; its JNI/device
validation is owned by the Android workstream. WAV/BWF, raw AAC, MKA and AMR use
the existing external-file fallback until safe native writing is proven there.
APE and ASF are not claimed on Android. Desktop retains its existing advertised
families with the bounded conditions above. For existing ID3v2.3 a platform may
either retain v2.3/UTF-16 targets or prove a complete v2.4 migration (including
private/unknown records); otherwise refuse. Platform-specific capability/refusal
differences must be visible, never a silent deletion of tags or advertised support.

## Primary references

* [ID3v2.4 structure](https://id3.org/id3v2.4.0-structure),
  [v2.4 frames](https://id3.org/id3v2.4.0-frames),
  [ID3v2.3](https://id3.org/id3v2.3.0).
* [Xiph Vorbis comments](https://xiph.org/vorbis/doc/v-comment.html),
  [FLAC RFC 9639 §8](https://www.rfc-editor.org/rfc/rfc9639.html#section-8),
  [Opus RFC 7845 §5.2](https://www.rfc-editor.org/rfc/rfc7845.html#section-5.2).
* [Apple metadata atoms/types](https://developer.apple.com/documentation/quicktime-file-format/metadata_atoms_and_types).
* [ID3 chapter absolute offsets](https://id3.org/id3v2-chapters-1.0),
  [Ogg page framing/granules](https://www.xiph.org/ogg/doc/framing.html),
  [ISO BMFF common encryption layout](https://www.w3.org/TR/eme-stream-mp4/),
  [Windows handle rename semantics](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info).
* [ReplaceFile metadata/ADS and partial-failure behavior](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew),
  [BackupRead filesystem records](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-backupread).
* [Monkey's Audio SDK and tagging implementation](https://www.monkeysaudio.com/developers.html).
* [EBU BWF Tech 3285](https://tech.ebu.ch/docs/tech/tech3285.pdf),
  [Microsoft ASF overview/specification link](https://learn.microsoft.com/en-us/windows/win32/wmformat/overview-of-the-asf-format).
* [Pinned ATL 7.18.0 source](https://github.com/Zeugma440/atldotnet/tree/61fbf286d99d04f6d668e944bc9c57c007ffd2c2).
