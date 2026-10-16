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

# CUE 整轨：6 分钟，三段不同频率的音调，每段 2 分钟
mkdir -p "$out/cue"
cue_tone() { # cue_tone <output> [ffmpeg 参数...]
  local file=$1
  shift
  ffmpeg -loglevel error -y -f lavfi \
    -i "aevalsrc='0.3*sin(2*PI*(if(lt(t,120),440,if(lt(t,240),660,880)))*t)':d=360:s=44100" "$@" "$file"
}
cue_text() { # cue_text <FILE 行中的文件名> <专辑名>
  cat <<CUE
REM DATE 2003
PERFORMER "测试歌手"
TITLE "$2"
FILE "$1" WAVE
  TRACK 01 AUDIO
    TITLE "第一首"
    INDEX 01 00:00:00
  TRACK 02 AUDIO
    TITLE "第二首"
    PERFORMER "测试歌手 feat. 嘉宾"
    INDEX 00 01:59:00
    INDEX 01 02:00:00
  TRACK 03 AUDIO
    TITLE "第三首"
    INDEX 01 04:00:00
CUE
}
# 外部 CUE，GBK 编码；CUE 里写的是 .wav，实际文件是 .flac（需求 CUE-4、CUE-5）
cue_tone "$out/cue/整轨专辑.flac"
cue_text "整轨专辑.wav" "整轨专辑" | iconv -f UTF-8 -t GBK > "$out/cue/整轨专辑.cue"
# 内嵌 cuesheet（需求 CUE-1）
cue_tone "$out/cue/Embedded.flac" -metadata CUESHEET="$(cue_text "Embedded.flac" "内嵌CUE专辑")"

# 歌词：同名 .lrc（GBK、双语、offset、一行多个时间戳）、内嵌歌词、纯文本歌词、CUE 虚拟歌曲的 .lrc
mkdir -p "$out/lrc"
lrc_lines() { for i in $(seq 0 13); do printf '[00:%02d.00]第 %d 行歌词\n[00:%02d.00]Line %d translation\n' $((i * 3)) $((i + 1)) $((i * 3)) $((i + 1)); done; }
tone 523 45 "$out/lrc/测试歌手 - 双语歌词.mp3" -ac 2 -b:a 128k -metadata title="双语歌词" -metadata artist="测试歌手"
{ echo "[ti:双语歌词]"; echo "[offset:0]"; lrc_lines; echo "[00:42.00][00:44.00]结尾（重复两次）"; } | iconv -f UTF-8 -t GBK > "$out/lrc/测试歌手 - 双语歌词.lrc"
tone 587 45 "$out/lrc/测试歌手 - 内嵌歌词.mp3" -ac 2 -b:a 128k -metadata title="内嵌歌词" -metadata artist="测试歌手" \
  -metadata lyrics="$(for i in $(seq 0 14); do printf '[00:%02d.00]内嵌第 %d 行\n' $((i * 3)) $((i + 1)); done)"
tone 659 45 "$out/lrc/测试歌手 - 纯文本歌词.mp3" -ac 2 -b:a 128k -metadata title="纯文本歌词" -metadata artist="测试歌手"
printf '没有时间轴的歌词\n\n第二段\n第三段\n' > "$out/lrc/测试歌手 - 纯文本歌词.lrc"
printf '[00:00.00]CUE 第二首的歌词\n[00:05.00]第二行\n' > "$out/cue/02. 第二首.lrc"

adb shell rm -rf "$remote"
adb shell mkdir -p "$remote"
adb push "$out/." "$remote/" >/dev/null
# 让 MediaProvider 扫描外部存储
adb shell content call --uri content://media --method scan_volume --arg external_primary >/dev/null

echo "已推送 $(find "$out" -type f | wc -l) 个文件到 $ANDROID_SERIAL:$remote"
