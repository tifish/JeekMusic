"""Compare the shipped ONNX model against Android's TFLite model on existing calibration PCM.

Run with the conversion venv described in yamnet-to-onnx.py:
  python desktop/tools/verify-yamnet.py mobile/build/calibration16 report.json
No audio or model files are modified.
"""
import importlib.util
import json
from pathlib import Path
import sys

import numpy as np
import onnxruntime as ort
import tensorflow as tf

root = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("calibration", root / "mobile/tools/calibration/evaluate.py")
calibration = importlib.util.module_from_spec(spec)
spec.loader.exec_module(calibration)
options = ort.SessionOptions()
options.intra_op_num_threads = 1
options.inter_op_num_threads = 1
onnx = ort.InferenceSession(str(root / "desktop/src/JeekMusic.App/Models/yamnet.onnx"), options)
lite = tf.lite.Interpreter(model_path=str(root / "mobile/app/src/main/assets/yamnet.tflite"), num_threads=1)
lite.allocate_tensors()
input_info, output_info = lite.get_input_details()[0], lite.get_output_details()[0]
differences, changed, tracks = [], [], 0
for path in sorted(Path(sys.argv[1]).glob("*.f32")):
    pcm = np.fromfile(path, dtype="<f4")
    onnx_scores, lite_scores = [], []
    for offset in range(0, len(pcm) - 15600 + 1, 15600):
        frame = pcm[offset:offset + 15600]
        output = onnx.run(None, {onnx.get_inputs()[0].name: frame})[0].reshape(-1)
        lite.set_tensor(input_info["index"], frame.reshape(input_info["shape"]))
        lite.invoke()
        reference = lite.get_tensor(output_info["index"]).reshape(-1)
        differences.append(float(np.abs(output - reference).mean()))
        onnx_scores.append(output)
        lite_scores.append(reference)
    if not onnx_scores:
        continue
    tracks += 1
    a = calibration.predict(np.array(onnx_scores), path.stem)
    b = calibration.predict(np.array(lite_scores), path.stem)
    if any(a[key] != b[key] for key in ("voice", "genre", "mood")):
        changed.append({"track": path.stem, "onnx": a, "tflite": b})
report = {"tracks": tracks, "frames": len(differences), "mean_absolute_score_error": float(np.mean(differences)),
          "classification_differences": changed}
Path(sys.argv[2]).write_text(json.dumps(report, indent=2, ensure_ascii=False, default=float), encoding="utf-8")
print(json.dumps({k: v for k, v in report.items() if k != "classification_differences"}))
print("Tracks with classification differences:", len(changed))
