#!/usr/bin/env bash
# 生成一组合成测试音频，推送到设备的 Music/JeekTest/ 并触发媒体扫描。
# 用于模拟器上的功能验证和 Baseline Profile 生成，不要对日常使用的真机运行。
#
# 用法：ANDROID_SERIAL=emulator-5554 tools/push-testdata.sh
# 依赖：ffmpeg、adb
set -euo pipefail

: "${ANDROID_SERIAL:?请设置 ANDROID_SERIAL，例如 emulator-5554}"
export ANDROID_SERIAL
# Git Bash 会把 /sdcard/... 这类参数改写成 Windows 路径，关掉这个转换
export MSYS_NO_PATHCONV=1

out="$(dirname "$0")/../build/testdata"
remote=/sdcard/Music/JeekTest
rm -rf "$out"
mkdir -p "$out/pop" "$out/hires"

tone() { # tone <freq> <seconds> <output> [ffmpeg 参数...]
  local freq=$1 secs=$2 file=$3
  shift 3
  ffmpeg -loglevel error -y -f lavfi -i "sine=frequency=${freq}:duration=${secs}" "$@" "$file"
}

# 普通 MP3：中文、英文、多艺术家（分隔符）
artists=("周杰伦" "陈奕迅" "Queen" "A; B" "G.E.M. 邓紫棋" "Simon & Garfunkel")
for i in $(seq 1 60); do
  artist=${artists[$((i % ${#artists[@]}))]}
  n=$(printf "%02d" "$i")
  tone $((200 + i * 10)) 3 "$out/pop/$n. Song $n.mp3" -ac 2 -b:a 128k \
    -metadata title="测试歌曲 $n" -metadata artist="$artist" \
    -metadata album="测试专辑 $((i % 5 + 1))" -metadata track="$i"
done

# 无标签，用于验证从文件名推断艺术家
tone 440 3 "$out/pop/许巍 - 曾经的你.mp3" -ac 2 -b:a 128k -map_metadata -1

# 24-bit / 96kHz FLAC，用于验证浮点输出
for i in 1 2 3; do
  tone $((300 + i * 100)) 5 "$out/hires/Hi-Res $i.flac" -ac 2 -ar 96000 -sample_fmt s32 \
    -bits_per_raw_sample 24 -metadata title="Hi-Res $i" -metadata artist="Test Artist"
done

adb shell rm -rf "$remote"
adb shell mkdir -p "$remote"
adb push "$out/." "$remote/" >/dev/null
# 让 MediaProvider 扫描外部存储
adb shell content call --uri content://media --method scan_volume --arg external_primary >/dev/null

echo "已推送 $(find "$out" -type f | wc -l) 个文件到 $ANDROID_SERIAL:$remote"
