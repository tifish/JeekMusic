"""用 ICU4J 的结果生成 shared/testcases/ 里的 pinyin.json、chinese-text.json 和 sort-keys.json 的 sortKey 部分。

输入：shared/pinyin/handmade.txt（手造用例，每行一条，\\uXXXX 表示一个字符），
可选再从大语料的期望值文件（generate.sh expect 的输出）里抽样。

用法：python build-testcases.py <icu4j.jar> [语料期望值.tsv]
"""
import json
import random
import re
import subprocess
import sys
import tempfile
from pathlib import Path

here = Path(__file__).resolve().parent
testcases = here.parent / "testcases"
jar = sys.argv[1]
corpus = Path(sys.argv[2]) if len(sys.argv) > 2 else None


def escape(s):
    return s.replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n")


def unescape(s):
    out, i = [], 0
    while i < len(s):
        if s[i] == "\\" and i + 1 < len(s):
            n = s[i + 1]
            out.append("\t" if n == "t" else "\n" if n == "n" else n)
            i += 2
        else:
            out.append(s[i])
            i += 1
    return "".join(out)


handmade = []
for line in (here / "handmade.txt").read_text(encoding="utf-8").split("\n"):
    if line == "":
        continue
    handmade.append(re.sub(r"\\u([0-9A-Fa-f]{4})", lambda m: chr(int(m.group(1), 16)), line))

with tempfile.TemporaryDirectory() as tmp:
    src = Path(tmp) / "in.txt"
    dst = Path(tmp) / "out.tsv"
    src.write_text("\n".join(escape(s) for s in handmade) + "\n", encoding="utf-8")
    subprocess.run(["java", "-cp", jar, str(here / "Generate.java"), "expect", str(src), str(dst)], check=True)
    rows = [[unescape(c) for c in line.split("\t")] for line in dst.read_text(encoding="utf-8").split("\n") if line]

samples = []
if corpus:
    all_rows = [[unescape(c) for c in line.split("\t")] for line in corpus.read_text(encoding="utf-8").split("\n") if line]
    rnd = random.Random(7)
    samples = rnd.sample(all_rows, 300)


def has_cjk(text):
    # 和移动端一样按 UTF-16 码元判断（扩展区汉字是代理对，不算）
    return any(0x3400 <= ord(c) <= 0x9FFF or 0xF900 <= ord(c) <= 0xFAFF or 0x2E80 <= ord(c) <= 0x2FDF or c in "〇々〻" for c in text)


pinyin = [{"description": "手造用例", "input": r[0], "expected": r[1]} for r in rows]
pinyin += [{"description": "语料抽样", "input": r[0], "expected": r[1]} for r in samples]
(testcases / "pinyin.json").write_text(json.dumps({
    "description": "拼音转写：结果应该和 ICU 的 \"Han-Latin; Latin-ASCII\" 完全一致（由 shared/pinyin/build-testcases.py 生成，不要手改）",
    "cases": pinyin,
}, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")

chinese = []
for desc, src_rows in (("手造用例", rows), ("语料抽样", samples)):
    for r in src_rows:
        chinese.append({"description": desc, "input": r[0], "simplified": r[2], "traditional": r[3]})
(testcases / "chinese-text.json").write_text(json.dumps({
    "description": "繁简转换：simplified 是 ICU \"Traditional-Simplified\" 的结果，traditional 是 \"Simplified-Traditional\" 的结果"
                   "（由 shared/pinyin/build-testcases.py 生成，不要手改）。移动端的 toSimplified 在没有汉字（按 UTF-16 码元判断）时直接返回原文，测试时先判断",
    "cases": chinese,
}, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")


def sort_key(transliterated):
    # 同 SortKeys.sortKey：转写后小写、合并连续空白、去掉首尾空白（Python 的 lower 和 Java 的 Locale.ROOT 对这些用例一样）
    return re.sub(r"\s+", " ", transliterated.lower()).strip()


sort_inputs = ["周杰伦", "  The   Beatles ", "G.E.M. 邓紫棋", "Beyoncé", "晴天.mp3", "重庆森林", "陈奕迅 feat. 王菲", "01. 周杰伦 - 晴天.flac",
               "米津玄師", "IU (아이유)", "Ærø"]
with tempfile.TemporaryDirectory() as tmp:
    src = Path(tmp) / "in.txt"
    dst = Path(tmp) / "out.tsv"
    # SortKeys 先 trim 再转写
    src.write_text("\n".join(escape(s.strip()) for s in sort_inputs) + "\n", encoding="utf-8")
    subprocess.run(["java", "-cp", jar, str(here / "Generate.java"), "expect", str(src), str(dst)], check=True)
    sort_rows = [[unescape(c) for c in line.split("\t")] for line in dst.read_text(encoding="utf-8").split("\n") if line]

sort_keys = json.loads((testcases / "sort-keys.json").read_text(encoding="utf-8"))
sort_keys["sortKey"] = [{"description": "ICU 转写后小写、合并空白", "input": original, "expected": sort_key(r[1])}
                        for original, r in zip(sort_inputs, sort_rows)]
(testcases / "sort-keys.json").write_text(json.dumps(sort_keys, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"pinyin {len(pinyin)}, chinese-text {len(chinese)}, sortKey {len(sort_keys['sortKey'])}")
