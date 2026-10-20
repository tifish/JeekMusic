#!/usr/bin/env bash
# 用 ICU4J 生成拼音、繁简对照表（shared/pinyin/*.tsv），见 Generate.java 和 desktop/docs/design.md §6.4。
# ICU4J 的版本和 Android 16 的 ICU 一致（76）。jar 下载到单独的临时目录，用完删除。需要 curl 和 JDK 17+。
#
#   ./generate.sh                         重新生成表
#   ./generate.sh expect <语料> <输出>     用 ICU 转写语料（每行一条），输出"原文\t拼音\t繁转简\t简转繁"，供两端对比
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
version=76.1
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
curl -fsSL -o "$tmp/icu4j.jar" "https://repo1.maven.org/maven2/com/ibm/icu/icu4j/$version/icu4j-$version.jar"
if [ "${1:-}" = "expect" ]; then
  java -cp "$tmp/icu4j.jar" "$here/Generate.java" expect "$2" "$3"
else
  java -cp "$tmp/icu4j.jar" "$here/Generate.java" tables "$here"
fi
