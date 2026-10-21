"""把移动端的 YAMNet（mobile/app/src/main/assets/yamnet.tflite）转成桌面端用的 ONNX（desktop/src/JeekMusic.App/Models/yamnet.onnx）。

一次性的工具，模型文件已经提交在仓库里；只有移动端换了模型时才需要重新转换（桌面端设计 §10.2）。

步骤（在 %TEMP% 下的独立虚拟环境里，不污染系统的 Python）：

    uv venv -p 3.12 %TEMP%\\yamnet-venv
    uv pip install --python %TEMP%\\yamnet-venv\\Scripts\\python.exe "tensorflow==2.17.1" "tf2onnx==1.16.1" "protobuf==3.20.3" "numpy<2" onnx onnxruntime
    %TEMP%\\yamnet-venv\\Scripts\\python.exe desktop/tools/yamnet-to-onnx.py [校准用 PCM 目录]

版本必须这样搭配：tf2onnx 1.16.1 要求 protobuf 3.x，TensorFlow 2.17 是还能用 protobuf 3.20 的版本，只有 Python 3.12 及以下的轮子。

转换保留 int8 量化（--dequantize 去掉量化后和 TFLite 的差异反而更大，见设计 §10.2）。给了校准 PCM 目录
（mobile/tools/calibration-pcm.sh 导出的 16kHz 单声道 float32）时，逐帧对比 ONNX 和 TFLite 的得分，并保存 ONNX 的得分，
可以交给 mobile/tools/calibration/evaluate.py 评估准确度。
"""
import glob
import os
import subprocess
import sys

root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
tflite = os.path.join(root, "mobile", "app", "src", "main", "assets", "yamnet.tflite")
onnx_out = os.path.join(root, "desktop", "src", "JeekMusic.App", "Models", "yamnet.onnx")

subprocess.run([sys.executable, "-m", "tf2onnx.convert", "--tflite", tflite, "--output", onnx_out, "--opset", "17"], check=True)
print("written", onnx_out)

if len(sys.argv) > 1:
    import numpy as np
    import onnxruntime as ort
    import tensorflow as tf

    pcm_dir = sys.argv[1]
    sess = ort.InferenceSession(onnx_out, providers=["CPUExecutionProvider"])
    name = sess.get_inputs()[0].name
    interp = tf.lite.Interpreter(model_path=tflite)
    interp.allocate_tensors()
    ti, to = interp.get_input_details()[0], interp.get_output_details()[0]
    frame = 15600
    names, scores, diffs = [], [], []
    for path in sorted(glob.glob(os.path.join(pcm_dir, "*.f32"))):
        pcm = np.fromfile(path, dtype="<f4")
        frames = []
        for start in range(0, len(pcm) - frame + 1, frame):
            x = pcm[start:start + frame].astype(np.float32)
            o = sess.run(None, {name: x})[0].reshape(-1)
            interp.set_tensor(ti["index"], x.reshape(ti["shape"]))
            interp.invoke()
            diffs.append(np.abs(o - interp.get_tensor(to["index"]).reshape(-1)).mean())
            frames.append(o)
        names.append(os.path.splitext(os.path.basename(path))[0])
        scores.append(np.array(frames, dtype=np.float32))
    out = os.path.join(pcm_dir, "scores-onnx.npz")
    np.savez_compressed(out, names=np.array(names, dtype=object), scores=np.array(scores, dtype=object))
    print(f"{len(names)} 首、{len(diffs)} 帧，ONNX 与 TFLite 每帧平均差异 {np.mean(diffs):.6f}；得分保存在 {out}")
