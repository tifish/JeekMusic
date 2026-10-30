#!/usr/bin/env bash
# LEGACY v4 only: not production-equivalent v5. Use desktop/tools/AnalysisProbe; see docs/analysis-v5.md.
# 为智能标签校准准备 PCM：对每首歌取 30%、50%、70% 处各 10 秒（与 App 中的 PcmDecoder 一致），
# 下混为单声道 16kHz 的 32 位浮点（YAMNet 的输入），写入 build/calibration16/*.f32。
# 然后（docs/design.md §6.6）：
#   python tools/calibration/yamnet_scores.py app/src/main/assets/yamnet.tflite build/calibration16 build/calibration16/scores.npz
#   python tools/calibration/evaluate.py build/calibration16/scores.npz --report
#
# 用法：tools/calibration-pcm.sh <音乐目录>...
# 环境变量 CALIBRATION_OUT 可以换输出目录（比如整个曲库导出到 build/library16，不覆盖校准集）。
set -euo pipefail

out="${CALIBRATION_OUT:-$(dirname "$0")/../build/calibration16}"
mkdir -p "$out"
rm -f "$out"/*.f32

find "$@" -type f \( -iname '*.mp3' -o -iname '*.flac' -o -iname '*.ogg' -o -iname '*.m4a' \) -print0 |
while IFS= read -r -d '' file; do
  duration=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$file" < /dev/null | cut -d. -f1)
  [ -z "$duration" ] && continue
  name=$(basename "${file%.*}" | tr '/\\' '__')
  target="$out/$name.f32"
  : > "$target"
  if [ "$duration" -lt 40 ]; then
    ffmpeg -nostdin -loglevel error -y -i "$file" -t 60 -ac 1 -ar 16000 -f f32le - >> "$target"
  else
    for p in 30 50 70; do
      ffmpeg -nostdin -loglevel error -y -ss $((duration * p / 100)) -t 10 -i "$file" -ac 1 -ar 16000 -f f32le - >> "$target"
    done
  fi
done
echo "已生成 $(ls "$out"/*.f32 | wc -l) 个文件到 $out"
