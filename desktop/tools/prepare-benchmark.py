"""Create an isolated 50,000-track synthetic library, never touching a real music folder.

python desktop/tools/prepare-benchmark.py
Then use test-app.ps1 with the printed music and data paths. This measures directory,
metadata and UI overhead; tiny WAV hardlinks do not simulate decoding a real mixed library.
"""
import json
import os
from pathlib import Path
import tempfile
import wave

root = Path(tempfile.mkdtemp(prefix="JeekMusic-benchmark-"))
music = root / "music"
data = root / "data"
data.mkdir()
for album in range(500):
    directory = music / f"Album {album:03}"
    directory.mkdir(parents=True)
    seed = root / f"seed-{album:03}.wav"
    with wave.open(str(seed), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(16000)
        output.writeframes(bytes(3200))
    for track in range(100):
        os.link(seed, directory / f"Artist {album:03} - Track {track:03}.wav")
(data / "settings.json").write_text(json.dumps({
    "language": "en", "autoUpdateCheck": False, "analysisMode": "Manual",
    "closeAction": "Exit", "onlineCovers": False, "onlineLyrics": False, "includeSubfolders": True,
}), encoding="utf-8")
print("Music:", music)
print("Data:", data)
