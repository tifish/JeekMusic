"""用人工标注（labels.tsv）评估智能标签的规则（曲风分古典 / 非古典，氛围分运动 / 非运动），与 App 中的 AudioTagger / TagClassifier 保持一致（docs/design.md §6.6）。

用法：
    python tools/calibration/evaluate.py build/calibration16/scores.npz [--report]
--report 时逐首打印得分和预测结果，方便调阈值。
曲名在 genre_labels.tsv（整个曲库的古典 / 非古典粗标注）中的歌另外统计"古典 / 非古典"。
"""
import os
import re
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

# 与 ClassicalTitles.kt 一致：曲名里有作品编号、带编号或调性的曲式、乐章速度标记等时算古典（补救模型漏判）
CLASSICAL_TITLE = re.compile("|".join(f"(?:{p})" for p in [
    r"\bBWV\s*\.?\s*\d", r"\bOp\.?\s*\d", r"\bOpus\s*\d", r"\bK\.?\s*V?\s*\d{2,3}\b", r"\bKV\s*\d", r"\bRV\s*\d",
    r"\bHob\.?\s*[IVX]", r"\bHWV\s*\d", r"\bWoO\s*\d", r"\bD\.?\s*\d{2,3}\b", r"\bS\.\s*\d{2,3}\b", r"\bL\.\s*\d{2,3}\b",
    r"\bMWV\b", r"\bTWV\b", r"\bBuxWV\b", r"\bSz\.?\s*\d",
    r"\b(Symphony|Symphonie|Sinfonie|Concerto|Konzert|Sonata|Sonate|Quartet|Quintet|Trio|Suite|Partita|Mass|Requiem|Cantata|Etude|Étude|"
    r"Nocturne|Prelude|Prélude|Fugue|Mazurka|Polonaise|Ballade|Scherzo|Impromptu|Rhapsody|Overture|Ouvertüre|Serenade|Divertimento|"
    r"Variations?|Waltz|Valse)\s+(No\.?\s*\d|Nr\.?\s*\d|in\s+[A-G]\b|op\.?\s*\d)",
    r"\b(in|en)\s+[A-G](\s*(flat|sharp|♭|♯|-flat|-sharp))?\s+(major|minor|Dur|moll|majeur|mineur)\b",
    r"(?:\b[IVX]+\.|\b\d+\.|[:：–-])\s*(Allegro|Adagio|Andante|Largo|Presto|Moderato|Allegretto|Larghetto|Vivace|Lento|Grave|Menuetto|Minuet|Rondo)\b",
    r"(交响曲|协奏曲|奏鸣曲|前奏曲|赋格|练习曲|弦乐四重奏|安魂曲|作品\s*\d)",
]), re.IGNORECASE)

# 与 TagClassifier.kt 一致（灵敏度"标准"）
VOCAL_THRESHOLD = -2.3
CLASSICAL_THRESHOLD = -0.4
SPORT_THRESHOLD = 3.25


def predict(frames, name=""):
    m = np.asarray(frames, dtype=np.float64).mean(axis=0)
    vocal = np.log((m[VOICE].sum() + 1e-3) / (m[INSTRUMENT].sum() + 1e-3))
    classical = np.log((m[CLASSICAL].sum() + 1e-3) / (m[POP].sum() + 1e-3))
    calm, energy, orchestra = m[CALM].sum(), m[ENERGY].sum(), m[ORCHESTRA]
    sport = np.log(energy + 0.003) - np.log(calm + 0.003)
    return {
        "voice": "V" if vocal >= VOCAL_THRESHOLD else "I",
        # 文件名是"艺术家 - 歌名"，App 里匹配的是歌名
        "genre": "C" if classical >= CLASSICAL_THRESHOLD or CLASSICAL_TITLE.search(name.split(" - ", 1)[-1]) else "N",
        "classical": classical,
        "mood": "S" if sport >= SPORT_THRESHOLD else "N",
        "scores": f"vocal={vocal:.2f} classical={classical:.2f} sport={sport:.2f} orchestra={orchestra:.3f}",
    }


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    data = np.load(sys.argv[1], allow_pickle=True)
    labels = {}
    for line in open(os.path.join(os.path.dirname(__file__), "labels.tsv"), encoding="utf-8"):
        if line.startswith("#") or line.startswith("name\t"):
            continue
        parts = line.rstrip("\n").split("\t")
        if len(parts) == 4:
            # 曲风只分古典 / 非古典：标注里的流行（P）和都不是（-）都算非古典
            genre = {"C": "C", "P": "N", "-": "N"}.get(parts[2], parts[2])
            # 氛围只分运动 / 非运动：标注里的安静（Q）和都不是（-）都算非运动
            mood = {"S": "S", "Q": "N", "-": "N"}.get(parts[3], parts[3])
            labels[parts[0]] = dict(voice=parts[1], genre=genre, mood=mood)
    classical = {}
    for line in open(os.path.join(os.path.dirname(__file__), "genre_labels.tsv"), encoding="utf-8"):
        if line.startswith("#") or line.startswith("name\t"):
            continue
        name, genre = line.rstrip("\n").split("\t")
        classical[name] = genre == "C"

    results = {"voice": [0, 0], "genre": [0, 0], "mood": [0, 0]}
    library = [0, 0, 0, 0]  # 古典判对、古典总数、非古典误判为古典、非古典总数
    for name, frames in zip(data["names"], data["scores"]):
        p = predict(frames, str(name))
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
            # "?" 不参与评估
            if t in (None, "?"):
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
