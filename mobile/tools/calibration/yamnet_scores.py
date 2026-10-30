# LEGACY v4 only: not production-equivalent v5. Use desktop/tools/AnalysisProbe; see docs/analysis-v5.md.
"""对校准用的 PCM（16kHz 单声道 f32，tools/calibration-pcm.sh 生成）逐帧运行 YAMNet，保存每帧 521 类分数。

用法（需要 numpy 和 ai-edge-litert：python -m pip install numpy ai-edge-litert）：
    python tools/calibration/yamnet_scores.py app/src/main/assets/yamnet.tflite build/calibration16 build/calibration16/scores.npz
"""
import glob
import os
import sys

import numpy as np
from ai_edge_litert.interpreter import Interpreter

model, pcm_dir, out = sys.argv[1], sys.argv[2], sys.argv[3]
interp = Interpreter(model_path=model)
interp.allocate_tensors()
inp = interp.get_input_details()[0]
output = interp.get_output_details()[0]
frame = int(inp["shape"][-1])  # 15600 个采样，0.975 秒

names, scores = [], []
for path in sorted(glob.glob(os.path.join(pcm_dir, "*.f32"))):
    pcm = np.fromfile(path, dtype="<f4")
    frames = []
    # 与 App 一致：不重叠的帧，不足一帧的尾巴丢掉
    for start in range(0, len(pcm) - frame + 1, frame):
        interp.set_tensor(inp["index"], pcm[start:start + frame].astype(np.float32).reshape(inp["shape"]))
        interp.invoke()
        frames.append(interp.get_tensor(output["index"]).reshape(-1))
    names.append(os.path.splitext(os.path.basename(path))[0])
    scores.append(np.array(frames, dtype=np.float32))
    print(len(names), names[-1])
np.savez_compressed(out, names=np.array(names, dtype=object), scores=np.array(scores, dtype=object))
print("saved", out)
