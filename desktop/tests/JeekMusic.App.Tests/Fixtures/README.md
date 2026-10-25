# Tag writer fixture

`tag-writer-silence.mp3` is synthetic silence, generated locally for metadata regression tests. It contains no personal music or tags. Tests add their own ID3 frames and verify the complete MPEG payload remains byte-for-byte identical after writing.

```sh
ffmpeg -f lavfi -i anullsrc=r=44100:cl=stereo -t 0.12 -c:a libmp3lame -b:a 128k -map_metadata -1 -write_xing 0 -id3v2_version 0 tag-writer-silence.mp3
```
