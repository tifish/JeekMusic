# Tag writer fixture

`tag-writer-silence.mp3` is synthetic silence, generated locally for metadata regression tests. It contains no personal music or tags. Tests add their own ID3 frames and verify the complete MPEG payload remains byte-for-byte identical after writing.

```sh
ffmpeg -f lavfi -i anullsrc=r=44100:cl=stereo -t 0.12 -c:a libmp3lame -b:a 128k -map_metadata -1 -write_xing 0 -id3v2_version 0 tag-writer-silence.mp3
```

The FLAC and M4A (AAC) fixtures are also 0.12 seconds of synthetic silence; Ogg Vorbis is 1 second. They were generated with the same source using `-c:a flac`, `-c:a aac`, and `-c:a libvorbis`. FFmpeg wrote known Unicode title, artist, album, and comment metadata for native-format compatibility checks. They contain no user music.
