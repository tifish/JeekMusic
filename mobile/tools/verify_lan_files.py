"""Independent byte/time evidence for the isolated LAN acceptance fixture, no app database writes."""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import re
import shlex
import subprocess

from lan_ui import ADB, guard

ROOT=Path(__file__).resolve().parents[2]
BASE=ROOT/"desktop/artifacts/lan-sync"
REMOTE="/sdcard/Documents/JeekLanAcceptance/JeekMusic-"+json.loads((BASE/"service-public.json").read_text())["roots"][0]["rootId"]+"/"

def ns(text):
    date,fraction,offset=re.fullmatch(r"(\d+-\d+-\d+ \d+:\d+:\d+)\.(\d+) ([+-]\d+)",text.strip()).groups()
    seconds=int(datetime.datetime.strptime(date+" "+offset,"%Y-%m-%d %H:%M:%S %z").timestamp())
    return seconds*1_000_000_000+int(fraction.ljust(9,"0"))

def inspect(relative):
    source=BASE/"source/Music"/relative
    target=REMOTE+relative
    before=source.stat();digest=hashlib.file_digest(source.open("rb"),"sha256").hexdigest()
    output=subprocess.check_output(ADB+["shell","sha256sum",shlex.quote(target)]).decode().split()[0]
    stat=subprocess.check_output(ADB+["shell","stat","-c","%s:%y",shlex.quote(target)]).decode().strip()
    size,time=stat.split(":",1)
    return {"path":relative,"sourceHash":digest,"targetHash":output,"sourceSize":before.st_size,"targetSize":int(size),
        "sourceModifiedNs":before.st_mtime_ns,"targetModifiedNs":ns(time),"hashMatches":digest==output,
        "timeDeltaNs":ns(time)-before.st_mtime_ns,"sourceStillUnchanged":source.stat().st_mtime_ns==before.st_mtime_ns}

if __name__=="__main__":
    p=argparse.ArgumentParser();p.add_argument("name");p.add_argument("--expect-conflict",action="store_true");a=p.parse_args();guard()
    files=[str(f.relative_to(BASE/"source/Music")).replace("\\","/") for f in (BASE/"source/Music").rglob("*") if f.is_file() and '.jeekmusic' not in f.parts and not str(f.relative_to(BASE/"source/Music")).startswith('Unselected') and f.name != 'NotRequired.wav']
    results=[inspect(f) for f in files]
    unselected=subprocess.run(ADB+["shell","test","!","-e",REMOTE+"Unselected/Private.wav"]).returncode==0
    result={"api":35,"abi":"x86_64","files":results,"unselectedAbsent":unselected}
    (BASE/(a.name+".json")).write_text(json.dumps(result,indent=2),encoding="utf-8")
    assert unselected
    for item in results:
        if a.expect_conflict and item["path"] == "Karajan/Album/Artist - First.wav":
            assert item["targetHash"] == json.loads((BASE/"conflict-before.json").read_text())["hash"]
        else: assert item["hashMatches"] and abs(item["timeDeltaNs"]) < 1000, item
    print(json.dumps(result,indent=2))
