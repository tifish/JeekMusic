"""Local bounded worker. Only explicitly prepared components; no audio upload or production tag writes."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import urllib.request

os.environ.update(OMP_NUM_THREADS="2", MKL_NUM_THREADS="2", OPENBLAS_NUM_THREADS="2", HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", CUDA_VISIBLE_DEVICES="")
HERE = Path(__file__).resolve().parent
CONTRACT = json.loads((HERE / "components.json").read_text())
EXTRACTOR = "clap-htsat-unfused@" + CONTRACT["revision"] + "/center10-rms01-v1"


HEAD_PATH=HERE/"head_protocol.json"
if not HEAD_PATH.exists(): HEAD_PATH=HERE.parents[4]/"shared/testcases/personal-tag-heads-v2.json"
HEAD_CONTRACT=json.loads(HEAD_PATH.read_text(encoding="utf-8"))
HEADS=HEAD_CONTRACT["heads"]


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1024 * 1024), b""): h.update(b)
    return h.hexdigest()


def atomic(path, value):
    path = Path(path)
    tmp = path.with_suffix(path.suffix + ".tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(value, f, ensure_ascii=False, allow_nan=False)
        f.flush(); os.fsync(f.fileno())
    os.replace(tmp, path)


def prepare(root):
    """Explicit download action, immutable official revision + audited content hashes."""
    model = root / "model"; model.mkdir(parents=True, exist_ok=True)
    for name, meta in CONTRACT["files"].items():
        target = model / name
        if target.exists() and sha(target) == meta["sha256"]: continue
        url = f'https://huggingface.co/{CONTRACT["repository"]}/resolve/{CONTRACT["revision"]}/{name}'
        tmp = target.with_suffix(target.suffix + ".download")
        with urllib.request.urlopen(url, timeout=60) as src, open(tmp, "wb") as dst:
            while b := src.read(1024 * 1024): dst.write(b)
        if tmp.stat().st_size != meta["size"] or sha(tmp) != meta["sha256"]:
            tmp.unlink(missing_ok=True); raise ValueError("Official component hash mismatch: " + name)
        os.replace(tmp, target)
    # Runtime belongs to the app's private optional components, never the system Python environment.
    venv = root / "venv"
    python = venv / "Scripts/python.exe" if os.name == "nt" else venv / "bin/python"
    if not python.exists(): subprocess.run([sys.executable, "-m", "venv", str(venv)], check=True)
    subprocess.run([str(python), "-m", "pip", "install", "--disable-pip-version-check", "--only-binary=:all:", "--index-url", "https://download.pytorch.org/whl/cpu", "torch==2.9.0+cpu", "--report", str(root / "torch-install.json")], check=True, stdout=subprocess.DEVNULL)
    subprocess.run([str(python), "-m", "pip", "install", "--disable-pip-version-check", "--only-binary=:all:", "--index-url", "https://pypi.org/simple", "transformers==4.57.1", "numpy==2.3.4", "--report", str(root / "runtime-install.json")], check=True, stdout=subprocess.DEVNULL)
    # pip verifies index-supplied wheel hashes; preserve resolved dependency names, versions, hashes and license metadata.
    inventory = subprocess.check_output([str(python), "-c", "import importlib.metadata as m,json; print(json.dumps([{'name':d.metadata['Name'],'version':d.version,'license':d.metadata.get('License-Expression') or d.metadata.get('License'),'licenseFiles':d.metadata.get_all('License-File')} for d in m.distributions()]))"], text=True)
    atomic(root / "licenses.json", json.loads(inventory))
    atomic(root / "ready.json", {"contract": sha(HERE / "components.json"), "worker": sha(__file__), "source": CONTRACT["source"], "weights": CONTRACT["files"]["pytorch_model.bin"]["sha256"]})
    return {"state": "components_ready", "downloadBytes": sum(f["size"] for f in CONTRACT["files"].values()), "license": CONTRACT["license"]}


def unit(x):
    import numpy as np
    n = np.linalg.norm(x)
    if not np.isfinite(n) or n <= 1e-12: raise ValueError("Degenerate audio/vector")
    return x / n


def fit(x, y):
    import numpy as np
    x, y = np.asarray(x, dtype=float), np.asarray(y, dtype=float)
    if x.ndim != 2 or len(x) < 3 or y.shape != (len(x),) or set(y) != {-1, 1} or not np.isfinite(x).all(): raise ValueError("Need both classes and finite recordings")
    center = x.mean(axis=0)
    _, singular, vt = np.linalg.svd(x-center, full_matrices=False)
    if len(singular) < 2 or singular[1] <= 1e-10: raise ValueError("PCA rank below two")
    axes = vt[:2].T
    z = (x-center) @ axes
    scale = np.maximum(z.std(axis=0), 1e-6); z /= scale
    weights = np.array([1 / np.count_nonzero(y == v) for v in y]); weights /= weights.sum()
    zm, ym = weights @ z, weights @ y
    zc, yc = z-zm, y-ym
    coef = np.linalg.solve(zc.T @ (weights[:, None]*zc) + np.eye(2), zc.T @ (weights*yc))
    return {"center": center.tolist(), "axes": axes.tolist(), "scale": scale.tolist(), "coef": coef.tolist(), "intercept": float(ym-zm @ coef)}


def score(model, x):
    import numpy as np
    return float(((np.asarray(x)-model["center"]) @ np.asarray(model["axes"]) / model["scale"]) @ model["coef"] + model["intercept"])


def decision(value, dimension="MOOD"):
    h=HEADS[dimension]
    return h["positive"] if value>h["margin"] else h["negative"] if value< -h["margin"] else "ABSTAIN"


def evaluate(rows, attempted, dimension="MOOD"):
    import numpy as np
    h=HEADS[dimension]; positive=h["positive"]; negative=h["negative"]
    # This input is a dedicated training snapshot, not the sync ledger or evaluation database.
    if any(r.get("role") != "training" or r.get("exposed") or not r.get("identityReviewed") for r in rows): raise ValueError("Training isolation violation")
    if len({r["recordingId"] for r in rows}) != len(rows): raise ValueError("Duplicate recording")
    if any(r["label"] not in (positive,negative) or r.get("dimension",dimension)!=dimension for r in rows): raise ValueError("Missing or cross-dimension label cannot become a negative")
    groups = sorted({r["componentId"] for r in rows})
    positives = sum(r["label"] == positive for r in rows)
    dual_artists = {a for r in rows for a in r["artists"] if {s["label"] for s in rows if a in s["artists"]} == {positive,negative}}
    if attempted not in h["nodes"] or len(rows)<h["minimum"] or min(positives,len(rows)-positives)<h["per_class"] or len(groups)<h["components"] or dimension=="MOOD" and len(dual_artists)<3:
        return {"state": "insufficient_material", "explicitLabels": len(rows), "components": len(groups), "dualClassArtists": len(dual_artists)}
    predictions = []
    for group in groups:
        train = [r for r in rows if r["componentId"] != group]
        model = fit([r["vector"] for r in train], [1 if r["label"] == positive else -1 for r in train])
        for row in rows:
            if row["componentId"] != group: continue
            value = score(model, row["vector"])
            predictions.append({"recordingId": row["recordingId"], "componentId": group, "label": row["label"], "decision": decision(value,dimension), "score": value, "rules": row.get("rules", "ABSTAIN")})
    p = [r for r in predictions if r["label"] == positive]; n = [r for r in predictions if r["label"] == negative]
    false_accept = sum(r["decision"] == positive for r in n)/len(n)
    effective_miss = sum(r["decision"] != positive for r in p)/len(p)
    coverage = sum(r["decision"] != "ABSTAIN" for r in predictions)/attempted
    class_coverage = [sum(r["decision"] != "ABSTAIN" for r in side)/len(side) for side in (p,n)]
    # A missing public baseline cannot pass an improvement gate.
    rules_complete = all(r["rules"] in (positive,negative) for r in predictions)
    rules_miss = sum(r["rules"] != positive for r in p)/len(p)
    passed = false_accept <= (.15 if dimension=="CALMNESS" else .1) and effective_miss<=h["miss"] and coverage>=h["coverage"] and min(class_coverage)>=h["coverage"] and (dimension!="MOOD" or rules_complete and rules_miss-effective_miss>=.1)
    return {"state": "training_gate_passed" if passed else "validation_failed", "metrics": {"falseAcceptance": false_accept, "effectiveMiss": effective_miss, "coverage": coverage, "classCoverage": class_coverage, "rulesBaselineComplete": rules_complete}, "predictions": predictions,
            "model": fit([r["vector"] for r in rows], [1 if r["label"] == positive else -1 for r in rows]) if passed else None,
            "dimension":dimension, "margin":h["margin"], "activation": "blocked_until_frozen_development_regression_independent_and_deployment_gates"}


def binomial_upper(errors, total):
    """One-sided 95% exact binomial upper bound, including the 0/n boundary."""
    import math
    if total <= 0: return None
    if errors == total: return 1.0
    lo, hi = 0.0, 1.0
    for _ in range(80):
        p=(lo+hi)/2
        cdf=sum(math.comb(total,k)*p**k*(1-p)**(total-k) for k in range(errors+1))
        if cdf>.05: lo=p
        else: hi=p
    return (lo+hi)/2


def evaluate_frozen(data):
    """Separate one-shot evaluator. No fitting, PCA updates or threshold search."""
    rows=data["rows"]; stage=data["stage"]; model=data["model"]; dimension=data.get("dimension","MOOD"); h=HEADS[dimension]; positive=h["positive"]; negative=h["negative"]
    expected={"development":h["development"],"sealed_test":h["test"],"calibration_regression":11}[stage]
    if len(rows)!=expected or len({r["recordingId"] for r in rows})!=expected:
        return {"state":"inconclusive","reason":"Frozen attempt list incomplete"}
    if any(r["role"]!=stage for r in rows): raise ValueError("Evaluation purpose mismatch")
    predictions=[]; direct_flip=False; stable_complete=True
    for row in rows:
        value=score(model,row["vector"]) if row.get("vector") is not None else None
        prediction=decision(value,dimension) if value is not None else "ABSTAIN"
        perturbed={k:decision(score(model,v),dimension) for k,v in row.get("perturbations",{}).items()}
        stable_complete &= set(perturbed)=={"base","minus6","plus6","minus2","plus2"}
        direct_flip |= positive in perturbed.values() and negative in perturbed.values()
        predictions.append({"recordingId":row["recordingId"],"label":row["label"],"decision":prediction,"rules":row.get("rules","ABSTAIN"),"perturbations":perturbed})
    if stage=="calibration_regression":
        negatives=[r for r in predictions if r["label"]==negative]
        old_correct=[r for r in predictions if r["label"]==positive and r["rules"]==positive]
        old_missed=[r for r in predictions if r["label"]==positive and r["rules"]!=positive]
        if any(not r.get("vector") for r in rows) or not stable_complete: return {"state":"incomplete","reason":"Regression vectors/stability incomplete"}
        passed=not any(r["decision"]==positive for r in negatives) and not any(r["decision"]==negative for r in old_correct) and sum(r["decision"]==positive for r in old_missed)>=2 and not direct_flip
        return {"state":"regression_passed" if passed else "validation_failed","predictions":predictions,"independent":False}
    p=[r for r in predictions if r["label"]==positive]; n=[r for r in predictions if r["label"]==negative]
    minimum=((4,4) if dimension=="MOOD" else (3,3)) if stage=="development" else ((15,30) if dimension=="MOOD" else (8,8))
    if len(p)<minimum[0] or len(n)<minimum[1]: return {"state":"inconclusive","reason":"Insufficient direct whole-recording classes; no replacements","positives":len(p),"negatives":len(n)}
    fp=sum(r["decision"]==positive for r in n); miss=sum(r["decision"]!=positive for r in p)
    coverage=sum(r["decision"]!="ABSTAIN" for r in predictions)/expected
    class_coverage=[sum(r["decision"]!="ABSTAIN" for r in side)/len(side) for side in (p,n)]
    passed=(fp==0 if stage=="development" else fp/len(n)<=.1 and binomial_upper(fp,len(n))<=.1) and miss/len(p)<=h["miss"] and coverage>=h["coverage"] and min(class_coverage)>=h["coverage"]
    metrics={"negativeCount":len(n),"positiveCount":len(p),"falseAcceptance":fp/len(n),"effectiveMiss":miss/len(p),"coverage":coverage,"classCoverage":class_coverage}
    if stage=="sealed_test":
        if dimension=="MOOD" and not data.get("annotationStabilityPassed",False): return {"state":"inconclusive","reason":"Fewer than four evaluable hidden repeats or more than one disagreement"}
        rules_miss=sum(r["rules"]!=positive for r in p)/len(p)
        net_sport=sum(r["decision"]==positive for r in p)-sum(r["rules"]==positive for r in p)
        if dimension=="MOOD": passed &= binomial_upper(miss,len(p))<=.25 and rules_miss-miss/len(p)>=.1 and net_sport>=2 and data.get("regressionPassed",False) and all(r["rules"] in (positive,negative) for r in predictions)
        else: passed = fp/len(n)<=(.15 if dimension=="CALMNESS" else .1) and miss/len(p)<=h["miss"] and coverage>=h["coverage"] and min(class_coverage)>=h["coverage"] and binomial_upper(fp,len(n))<=(.4 if dimension=="CALMNESS" else .35) and binomial_upper(miss,len(p))<=(.4 if dimension=="CALMNESS" else .35)
        passed &= stable_complete and not direct_flip
        metrics.update(falseAcceptanceUpper=binomial_upper(fp,len(n)),effectiveMissUpper=binomial_upper(miss,len(p)),netCorrectSport=net_sport,stabilityComplete=stable_complete,directFlip=direct_flip)
    return {"state":("development_passed" if stage=="development" else "pass_research_gate") if passed else "validation_failed","metrics":metrics,"predictions":predictions,"activation":"disabled_deployment_equivalence_and_ARM_budget_unverified"}


def extract(root, rows, deadline):
    import numpy as np
    import torch
    from transformers import ClapAudioConfig, ClapAudioModelWithProjection, ClapFeatureExtractor
    torch.set_num_threads(2); torch.set_num_interop_threads(1); torch.use_deterministic_algorithms(True)
    model_dir = root / "model"
    for name, meta in CONTRACT["files"].items():
        if sha(model_dir / name) != meta["sha256"]: raise ValueError("Component hash mismatch")
    config = json.loads((model_dir / "config.json").read_text())
    model = ClapAudioModelWithProjection(ClapAudioConfig(**config["audio_config"]))
    # Official checkpoint only; weights_only disables pickle code execution; mmap avoids a second full text model.
    weights = torch.load(model_dir / "pytorch_model.bin", map_location="cpu", weights_only=True, mmap=True)
    audio = {k: v for k, v in weights.items() if k.startswith(("audio_model.", "audio_projection."))}
    model.load_state_dict(audio, strict=True); del weights, audio
    model.eval()
    processor = ClapFeatureExtractor.from_pretrained(str(model_dir), local_files_only=True)
    cache = root / "vectors"; cache.mkdir(exist_ok=True)
    result = []
    for row in rows:
        if time.monotonic() >= deadline: raise TimeoutError("batch_budget_exhausted")
        if row.get("cue") or len(row.get("centersMs", [])) != 3: raise ValueError("Unsupported context contract")
        if sha(Path(row["path"])) != row["contentSignature"]: raise ValueError("Recording changed")
        key = hashlib.sha256((row["recordingId"] + EXTRACTOR + sha(__file__) + json.dumps(row["centersMs"])).encode()).hexdigest()
        target = cache / (key + ".json")
        if target.exists():
            saved = json.loads(target.read_text()); result.append({**row, "vector": saved["vector"], "perturbations": saved["perturbations"]}); continue
        perturbations = {}
        for condition, shift, gain in (("base",0,1.0),("minus6",0,10**(-6/20)),("plus6",0,10**(6/20)),("minus2",-2000,1.0),("plus2",2000,1.0)):
            vectors = []
            for center in row["centersMs"]:
                if time.monotonic() >= deadline: raise TimeoutError("batch_budget_exhausted")
                timeout = max(.1, deadline-time.monotonic())
                pcm = subprocess.run(["ffmpeg", "-v", "error", "-nostdin", "-threads", "2", "-ss", str((center+shift-5000)/1000), "-i", row["path"], "-t", "10", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1"], capture_output=True, check=True, timeout=timeout).stdout
                x = np.frombuffer(pcm, dtype="<f4").copy()
                if len(x) != 480000 or not np.isfinite(x).all(): raise ValueError("Incomplete 10-second audio")
                x *= gain
                rms = float(np.sqrt(np.mean(x.astype(float)**2)))
                if rms <= 1e-8: raise ValueError("Silent context")
                x *= .1/rms
                inputs = processor(x, sampling_rate=48000, return_tensors="pt", truncation="rand_trunc", padding="repeatpad")
                with torch.inference_mode(): vector = model(**inputs).audio_embeds[0].numpy().astype(float)
                vectors.append(unit(vector))
            value = unit(np.mean(vectors,axis=0)).tolist()
            if len(value) != 512: raise ValueError("Unexpected projection width")
            perturbations[condition] = value
        value = perturbations["base"]
        if sha(Path(row["path"])) != row["contentSignature"]: raise ValueError("Recording changed during extraction")
        atomic(target, {"extractor": EXTRACTOR, "vector": value, "perturbations": perturbations})
        result.append({**row, "vector": value, "perturbations": perturbations})
    return result


def main():
    parser = argparse.ArgumentParser(); parser.add_argument("action", choices=["prepare", "extract", "train", "evaluate"]); parser.add_argument("--root", type=Path, required=True); parser.add_argument("--input", type=Path); parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(); start = time.monotonic()
    try:
        if args.action == "prepare": result = prepare(args.root)
        else:
            data = json.loads(args.input.read_text(encoding="utf-8-sig"))
            if args.action == "extract":
                if not 1 <= len(data["rows"]) <= 6: raise ValueError("At most six recordings per extraction")
                rows = extract(args.root, data["rows"], start+240)
                result = {"state": "extraction_complete", "rows": [{k:v for k,v in r.items() if k != "path"} for r in rows], "extractor": EXTRACTOR}
            elif args.action == "train":
                if any(r.get("labelSource")!="explicit_human_blind_batch" and not (data.get("syntheticFixture",False) and r.get("labelSource")=="synthetic_fixture") for r in data["rows"]): raise ValueError("Only explicit human truth or a declared isolated synthetic validation archive may train")
                result = evaluate(data["rows"], data["attempted"],data.get("dimension","MOOD"))
                result["syntheticFixture"]=data.get("syntheticFixture",False)
            else: result = evaluate_frozen(data)
        result["elapsedSeconds"] = time.monotonic()-start; atomic(args.output,result)
    except Exception as error:
        atomic(args.output, {"state": "failed", "errorType": type(error).__name__, "error": str(error)[:300], "elapsedSeconds": time.monotonic()-start}); raise


if __name__ == "__main__": main()
