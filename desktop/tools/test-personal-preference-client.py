"""Explicit isolated synthetic acceptance through production MCP/TLS/services.

Never accepts a real data directory or arbitrary songs. All fitted labels are marked
synthetic_validation and can never create an eligible personal head. No audio upload.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import time
import uuid
from mcp_client import McpClient

ROOT=Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location("lan_fixture",Path(__file__).with_name("test-lan-sync.py"))
lan=importlib.util.module_from_spec(spec);spec.loader.exec_module(lan)


def call(client,tool,**args):
    result=client.call(tool,args)
    if result.get("isError"): raise AssertionError((tool,result))
    return result["structuredContent"]


def job(client,action,dimension="MOOD",cancel=False):
    call(client,"preference_training",action=action,dimension=dimension)
    if cancel: time.sleep(.15); call(client,"preference_training",action="cancel",dimension=dimension)
    deadline=time.monotonic()+(360 if action!="prepare" else 180)
    while time.monotonic()<deadline:
        state=call(client,"preference_training",action="status",dimension=dimension)
        if state["state"] not in ("checking_material","running","preparing_components"):
            print(action,dimension,state["state"],flush=True);return state
        time.sleep(.3)
    raise TimeoutError("Bounded fixture job did not terminate")


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument("--exe",required=True,type=Path);p.add_argument("--data",required=True,type=Path);p.add_argument("--serial",required=True);p.add_argument("--components",type=Path);p.add_argument("--compute",action="store_true");p.add_argument("--resume",action="store_true");a=p.parse_args()
    data=a.data.resolve(); artifacts=(ROOT/"desktop/artifacts").resolve()
    assert data.is_relative_to(artifacts) and a.serial=="emulator-5584","Explicit allocated synthetic acceptance only"
    out=artifacts/"personal-preference-acceptance";music=out/"music";out.mkdir(exist_ok=True)
    connection=json.loads(subprocess.check_output(["adb","-s",a.serial,"shell","run-as","com.tinyfish.jeekmusic.debug","cat","files/lan-sync.json"]))["connection"]
    before_hash=hashlib.sha256(subprocess.check_output(["adb","-s",a.serial,"shell","run-as","com.tinyfish.jeekmusic.debug","cat","files/lan-sync.json"])).hexdigest()
    report={"synthetic":True,"humanTruth":False,"audioUploaded":False,"productionModelActivated":False,"headStates":{}}
    with McpClient(a.exe,data,timeout=100) as c:
        initial=call(c,"get_status");assert not initial["playing"] and initial["queueCount"]==0
        call(c,"rescan_library")
        for _ in range(200):
            tracks=call(c,"search_tracks",query="Fixture",limit=200)["items"]
            if len(tracks)==24 and not call(c,"get_status")["scanning"]:break
            time.sleep(.2)
        assert len(tracks)==24
        for t in tracks:
            detail=call(c,"get_track",trackId=t["id"]);assert Path(detail["path"]).resolve().is_relative_to(music.resolve())
        if a.resume:
            profile=(out/"fixture-profile-id.txt").read_text();call(c,"preference_profile",action="select",profileId=profile);call(c,"preference_profile",action="dimension",dimension="MOOD");report["frozenSyntheticFrame"]=True
        else:
            profile=call(c,"preference_profile",action="create",name="SYNTHETIC four-head pipeline; not human labels",purpose="synthetic_validation")["profileId"]
            (out/"fixture-profile-id.txt").write_text(profile)
            for dimension,(positive,negative) in {"MOOD":("SPORT","NON_SPORT"),"VOICE":("VOCAL","INSTRUMENTAL"),"GENRE":("CLASSICAL","NON_CLASSICAL"),"CALMNESS":("CALM","NON_CALM")}.items():
                call(c,"preference_profile",action="dimension",dimension=dimension)
                answered=set()
                while len(answered)<24:
                    batch=call(c,"preference_batch",action="prepare",dimension=dimension)
                    for item in batch["items"]:
                        assert item["title"].startswith("Fixture ") and item["artist"].startswith("Synthetic")
                        number=int(item["title"].split()[-1]);label=positive if number%2 else negative
                        call(c,"preference_answer",recordingId=item["recordingId"],answer=label,basis="whole_recording_familiar",reason="SYNTHETIC TEST FIXTURE, generated tone; not human ground truth",dimension=dimension)
                        answered.add(item["recordingId"])
                state=job(c,"train",dimension)
                assert state["state"] in ("components_required","identity_incomplete"),state
                print("24 declared synthetic answers:",dimension,flush=True)
            call(c,"preference_profile",action="dimension",dimension="MOOD")
            # Read only the synthetic fixture DB, never a user's production ledger.
            with sqlite3.connect(data/"preferences.db") as db:
                recordings=[json.loads(row[0]) for row in db.execute("SELECT json FROM events WHERE profile=? AND kind='recording'",(profile,))]
            for e in recordings:
                r=e["payload"]
                call(c,"preference_identity",recordingId=e["entityId"],work=r["workGroup"],album=r["albumGroup"],artists=r["artists"],exposed=False)
            events=[]
            for i in range(84):
                h=lambda text:hashlib.sha256(text.encode()).hexdigest()
                entity=h("missing synthetic reference "+str(i))
                events.append(dict(protocol=2,eventId=uuid.uuid4().hex,profileId=profile,entityId=entity,kind="recording",deviceId="synthetic_acceptance",revision=1,parents=[],at=int(time.time()*1000),payload=dict(recordingId=entity,contentSignature=h("synthetic content "+str(i)),title="Missing synthetic reference "+str(i),artist="Unseen synthetic "+str(i),album="Synthetic release "+str(i),artists=["UNSEEN "+str(i)],workGroup="UNSEEN WORK "+str(i),albumGroup="UNSEEN ALBUM "+str(i),durationMs=180000,identityReviewed=True,exposed=False,supported=True,fixture=True,referenceState="missing_file_fixture")))
            delta=lan.wire(connection,"preference_delta",protocol=2,sender="synthetic_acceptance",after=0,page=dict(protocol=2,events=events,nextCursor=len(events),hasMore=False))
            assert "error" not in delta,delta
            frozen=call(c,"preference_inventory",action="freeze");assert frozen["state"]=="frozen"
            report["frozenSyntheticFrame"]=True
        if a.components:
            target=data/"preference-components";target.mkdir(exist_ok=True)
            for name in ("model","venv"):
                if not (target/name).exists():shutil.copytree(a.components/name,target/name,copy_function=os.link)
            shutil.copy2(a.components/"ready.json",target/"ready.json")
        if a.compute:
            cancelled=job(c,"prepare",cancel=True);assert cancelled["state"]=="cancelled"
            prepared=job(c,"prepare");assert prepared["state"]=="components_ready"
            with sqlite3.connect(data/"preferences.db") as db:
                count=db.execute("SELECT COUNT(*) FROM local WHERE key LIKE 'vector:%'").fetchone()[0]
            for index in range(6):
                extracted=job(c,"extract")
                if extracted["state"]=="insufficient_material":break
                assert extracted["state"] in ("extraction_complete","incomplete"),extracted
                with sqlite3.connect(data/"preferences.db") as db:
                    count=db.execute("SELECT COUNT(*) FROM local WHERE key LIKE 'vector:%'").fetchone()[0]
                print("Shared real CLAP cached recordings:",count,flush=True)
                if count>=24:break
            assert count>=24
            for d in ("MOOD","VOICE","GENRE","CALMNESS"):
                state=job(c,"train",d);assert state["state"]=="synthetic_validation_complete",state
                report["headStates"][d]=state["state"]
            report["realClapExtraction"]=True
        final=call(c,"get_status");assert not final["playing"] and final["queueCount"]==0
        report["queuePreserved"]=True
    after_hash=hashlib.sha256(subprocess.check_output(["adb","-s",a.serial,"shell","run-as","com.tinyfish.jeekmusic.debug","cat","files/lan-sync.json"])).hexdigest()
    assert before_hash==after_hash;report["pairingAndMappingBytesPreserved"]=True
    (out/"client-training-report.json").write_text(json.dumps(report,indent=2))
    print("PASS: declared synthetic archive; no user truth/performance claim",flush=True)


if __name__=="__main__":main()
