"""用人工标注（labels.tsv）评估智能标签的规则，与 App 中的 AudioTagger / TagClassifier 保持一致（docs/design.md §6.6）。

用法：
    python tools/calibration/evaluate.py build/calibration16/scores.npz [--report]
--report 时逐首打印得分和预测结果，方便调阈值。
曲名在 genre_labels.tsv（整个曲库的古典 / 非古典粗标注）中的歌另外统计"古典 / 非古典"。
"""
import os
import sys

import numpy as np

# 与 AudioTagger.kt 中的类别分组一致（编号来自 YAMNet 的 yamnet_class_map.csv）
VOICE = [24, 25, 29, 31, 32, 27, 249, 250, 233, 0, 266, 30]
INSTRUMENT = [133, 184, 186, 188, 148, 147, 179, 185, 180, 182, 190, 191, 189, 149, 187, 134, 135]
CLASSICAL = [232, 233]
POP = [*range(211, 232), *range(234, 249), *range(250, 261), *range(262, 266), *range(267, 271)]
CALM = [273, 272, 266, 248, 241, 262]
ENERGY = [274, 271, 269, 234, 212, 214, 157, 159, 31, 231, 211, 240]
ORCHESTRA = 179

# 与 TagClassifier.kt 一致（灵敏度"标准"）
VOCAL_THRESHOLD = -2.3
CLASSICAL_THRESHOLD, POP_THRESHOLD = -0.4, -1.5
SPORT_THRESHOLD, QUIET_THRESHOLD, ORCHESTRA_MAX = 3.25, 0.2, 0.1


def predict(frames):
    m = np.asarray(frames, dtype=np.float64).mean(axis=0)
    vocal = np.log((m[VOICE].sum() + 1e-3) / (m[INSTRUMENT].sum() + 1e-3))
    classical = np.log((m[CLASSICAL].sum() + 1e-3) / (m[POP].sum() + 1e-3))
    calm, energy, orchestra = m[CALM].sum(), m[ENERGY].sum(), m[ORCHESTRA]
    sport = np.log(energy + 0.003) - np.log(calm + 0.003)
    quiet = np.log(calm + 0.01) - np.log(energy + 0.01)
    return {
        "voice": "V" if vocal >= VOCAL_THRESHOLD else "I",
        "genre": "C" if classical >= CLASSICAL_THRESHOLD else "P" if classical <= POP_THRESHOLD else "-",
        "mood": "S" if sport >= SPORT_THRESHOLD else "Q" if quiet >= QUIET_THRESHOLD and orchestra <= ORCHESTRA_MAX else "-",
        "scores": f"vocal={vocal:.2f} classical={classical:.2f} sport={sport:.2f} quiet={quiet:.2f} orchestra={orchestra:.3f}",
    }


def main():
    data = np.load(sys.argv[1], allow_pickle=True)
    labels = {}
    for line in open(os.path.join(os.path.dirname(__file__), "labels.tsv"), encoding="utf-8"):
        if line.startswith("#") or line.startswith("name\t"):
            continue
        parts = line.rstrip("\n").split("\t")
        if len(parts) == 4:
            labels[parts[0]] = dict(voice=parts[1], genre=parts[2], mood=parts[3])
    classical = {}
    for line in open(os.path.join(os.path.dirname(__file__), "genre_labels.tsv"), encoding="utf-8"):
        if line.startswith("#") or line.startswith("name\t"):
            continue
        name, genre = line.rstrip("\n").split("\t")
        classical[name] = genre == "C"

    results = {"voice": [0, 0], "genre": [0, 0], "mood": [0, 0]}
    library = [0, 0, 0, 0]  # 古典判对、古典总数、非古典误判为古典、非古典总数
    for name, frames in zip(data["names"], data["scores"]):
        p = predict(frames)
        truth = labels.get(name, {})
        marks = []
        if name in classical:
            predicted = p["genre"] == "C"
            if classical[name]:
                library[0] += predicted
                library[1] += 1
            else:
                library[2] += predicted
                library[3] += 1
            if predicted != classical[name] and "--report" in sys.argv:
                marks.append("古典漏判" if classical[name] else "误判为古典")
        for dim in results:
            t = truth.get(dim)
            # 古典 / 流行只评估两类都有标注的歌；"?" 不参与评估
            if t in (None, "?") or (dim == "genre" and t not in ("C", "P")):
                continue
            results[dim][1] += 1
            if p[dim] == t:
                results[dim][0] += 1
            else:
                marks.append(f"{dim}: {t}→{p[dim]}")
        if "--report" in sys.argv or marks:
            print(f"{name[:50]:50s} {p['voice']}{p['genre']}{p['mood']}  {p['scores']}  {'  '.join(marks)}")
    for dim, (ok, total) in results.items():
        print(f"{dim}: {ok}/{total} = {ok / max(total, 1):.1%}")
    if library[1] + library[3]:
        ok = library[0] + library[3] - library[2]
        print(f"古典 / 非古典（genre_labels.tsv）：古典 {library[0]}/{library[1]}，非古典误判为古典 {library[2]}/{library[3]}，"
              f"准确 {ok / (library[1] + library[3]):.1%}")


main()
