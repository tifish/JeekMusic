# Synthetic shared tag-writing corpus

All audio here is generated locally by FFmpeg: 0.3-second silence, with a 440 Hz
synthetic tone for WavPack. JPEGs are solid procedural images. Text is synthetic.
No personal music, downloaded songs, album art or copyrighted lyric source is used.
These fixture contents may be used under the repository's license.

`manifest.json` records the exact checked-in asset hashes and source tool version.
`generate.py` needs FFmpeg and Pillow; it recreates the logical corpus but native
encoder/container versions or random stream/file IDs can change binary hashes.
Update the manifest deliberately if regenerating, and share the resulting commit.

The contract is [tag-writing-policy.md](../../docs/tag-writing-policy.md). Do not
interpret every asset as a required write capability on every platform. A platform
must explicitly report write/refuse/external fallback per format and operation.

* `id3v23.mp3`, `id3v24.mp3`: native text, year-only date, multivalues, PRIV/TXXX,
  original bilingual/millisecond LRC, a back cover with a non-ASCII description,
  and deliberately different original ID3v1 bytes.
* `multiple-lyrics.mp3`: two lyric language/description slots. Cover-only must
  preserve both; an unscoped lyric replacement should refuse safely.
* `untagged.mp3`: new tags must use ID3v2.4/UTF-8.
* Native formats: Vorbis/Opus, FLAC, MP4, WAV/AIFF, ASF and WavPack. Some contain
  opaque native records matching `opaque-marker.bin`; FLAC also has repeated
  ARTIST entries and an APPLICATION block. Native unsupported formats may refuse.
* `faststart.m4a` exercises media relocation/chunk offsets; `untagged.m4a` exercises
  creation of the native metadata hierarchy without an existing `ilst`.
* `front.jpg`, `back.jpg`, `lyrics-before.lrc`, `lyrics-after.lrc` are local edit
  inputs. LRC includes a translation, millisecond times, a blank marker and offset.

The desktop suite independently inspects encoded payload and raw LRC contents;
it does not rely only on the same library that writes the file. APE/TAK/Musepack
share the APEv2 envelope implementation, but this corpus has no encoded APE/TAK/
Musepack sample. Do not claim codec/device validation of those formats from the
WavPack or envelope tests alone.
