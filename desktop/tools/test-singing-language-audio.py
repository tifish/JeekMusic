"""Exercise the actual decoder/YAMNet/Whisper path on explicitly supplied local samples.

Samples are copied into a fresh isolated fixture. Synthetic speech is a diagnostic,
not evidence of singing accuracy. Optional historical singing samples are reported
separately; unknown is retained rather than turned into a negative language label.
No playback, network, transcription persistence, or writes to original recordings.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import shutil
import time

from mcp_client import McpClient

ROOT=Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location("startup_checks",Path(__file__).with_name("test-mcp.py"))
startup=importlib.util.module_from_spec(spec)
spec.loader.exec_module(startup)


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument("--exe",type=Path,default=ROOT/"bin/JeekMusic.exe")
    p.add_argument("--data",type=Path,required=True)
    p.add_argument("--report",type=Path,required=True)
    p.add_argument("--model",type=Path,required=True)
    p.add_argument("--samples",type=Path,required=True)
    a=p.parse_args()
    data=a.data.resolve()
    assert data.is_relative_to(ROOT/"desktop/artifacts") and data.name.endswith("fixture") and not data.exists()
    data.mkdir(parents=True)
    music=data/"music"; music.mkdir()
    (data/"settings.json").write_text(json.dumps(dict(closeAction="Exit",autoUpdateCheck=False,analysisMode="Manual",
        onlineLyrics=False,onlineCovers=False,songSort="FileName",sortDescending=False)),encoding="utf-8")
    model=data/"models/singing-language/ggml-tiny.bin"; model.parent.mkdir(parents=True)
    shutil.copyfile(a.model,model)
    fixtures=[("mandarin-speech.wav","01-English-title.wav","zh","synthetic_speech"),
              ("english-speech.wav","02-中文歌名.wav","en","synthetic_speech"),
              ("Daisy Bell sung by Edward M. Favor denoised.ogg","03-中文歌名.ogg","en","historical_singing"),
              ("Chinese Vocal and Instrumental Ensemble.ogg","04-English-title.ogg","zh","historical_singing")]
    expected={}
    for source,name,language,kind in fixtures:
        path=a.samples/source
        if path.exists():
            shutil.copyfile(path,music/name)
            expected[name]=(language,kind)
    assert len(expected)>=2
    report={"noPlayback":True,"noUploads":True,"samples":[]}
    player=None
    try:
        with McpClient(a.exe,data,timeout=60) as c:
            call=lambda tool,**kw:startup.call(c,tool,**kw)
            player=call("get_diagnostics")["processId"]
            report["nativeAot"]=not call("get_diagnostics")["dynamicCodeSupported"]
            call("add_folder",path=str(music.resolve()))
            tracks=startup.wait_for(c,"search_tracks",lambda x:x["total"]==len(expected),limit=20)["items"]
            call("singing_language",action="start",scope="track_ids",trackIds=[t["id"] for t in tracks])
            deadline=time.monotonic()+max(180,len(tracks)*130)
            while time.monotonic()<deadline:
                status=call("singing_language",action="status",limit=20)
                if status["state"] not in ("preparing","running","cancelling"): break
                time.sleep(.2)
            else: raise TimeoutError("Vocal-language audio analysis")
            report["job"]={k:v for k,v in status.items() if k!="tracks"}
            assert status["state"]=="completed" and status["failed"]==0,status
            recognized={r["trackId"]:r["language"] for r in status["tracks"]["items"]}
            for t in tracks:
                language,kind=expected[t["fileName"]]
                result=recognized[t["id"]]
                report["samples"].append(dict(file=t["fileName"],kind=kind,expected=language,result=result))
                a.report.parent.mkdir(parents=True,exist_ok=True)
                a.report.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding="utf-8")
                assert result is not None
                if kind=="synthetic_speech":
                    assert result["state"]=="known" and language in result["languages"],report["samples"][-1]
                elif result["state"]=="known":
                    assert language in result["languages"] or (language=="zh" and "yue" in result["languages"]),report["samples"][-1]
            final=call("get_status")
            assert not final["playing"] and final["queueCount"]==0
            # Cancellation keeps the already committed evidence and never opens playback.
            before = {r["trackId"]:r["language"] for r in status["tracks"]["items"]}
            call("singing_language",action="start",scope="track_ids",trackIds=[t["id"] for t in tracks],force=True)
            call("singing_language",action="cancel")
            deadline=time.monotonic()+30
            while time.monotonic()<deadline:
                cancelled=call("singing_language",action="status",limit=20)
                if cancelled["state"]=="cancelled": break
                time.sleep(.1)
            else: raise TimeoutError("Cancellation")
            assert before == {r["trackId"]:r["language"] for r in cancelled["tracks"]["items"]}
            report["cancellationRetainsCommittedResults"] = True
            call("checkpoint_client")
    finally:
        if player: startup.close_player(player)
        a.report.parent.mkdir(parents=True,exist_ok=True)
        a.report.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding="utf-8")
    print(json.dumps({"nativeAot":report["nativeAot"],"samples":[dict(kind=s["kind"],expected=s["expected"],
        state=s["result"]["state"],languages=s["result"]["languages"]) for s in report["samples"]]}))


if __name__=="__main__": main()
