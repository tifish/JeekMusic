#!/usr/bin/env bash
# 在模拟器上截取应用商店截图，写入 fastlane/metadata/android/<语言>/images/phoneScreenshots/。
# 前提：模拟器上已添加音乐文件夹（tools/push-testdata.sh + 散曲样本），"Lady Gaga - Poker Face" 有 .lrc 歌词。
#
# 用法：tools/store-screenshots.sh
set -euo pipefail
cd "$(dirname "$0")/.."
source tools/ui.sh

pkg=com.tinyfish.jeekmusic

shoot() { # shoot <locale> <dir> <标签:文件夹> <标签:艺术家> <标签:运动>
  local locale=$1 dir=$2 folders=$3 artists=$4 sport=$5
  local out="fastlane/metadata/android/$dir/images/phoneScreenshots"
  mkdir -p "$out"
  adb shell cmd locale set-app-locales $pkg --locales "$locale"
  adb shell am force-stop $pkg
  adb shell am start -W -n $pkg/.MainActivity >/dev/null
  sleep 3
  adb shell input swipe 100 370 1000 370 300 && sleep 1

  # 1. 文件夹里的歌曲列表（带封面）
  ui_tap_text "$folders"; sleep 1
  while ui_find resource-id folder_content >/dev/null 2>&1 && [ -n "$(ui_find resource-id folder_content)" ]; do adb shell input keyevent BACK; sleep 1; done
  ui_tap_text "Music"; sleep 1; ui_tap_text "散曲"; sleep 1
  adb shell input swipe 500 1600 500 900 300; sleep 2
  ui_shot "$out/1.png"

  # 2. 艺术家
  ui_tap_text "$artists"; sleep 2
  ui_shot "$out/2.png"

  # 3. 播放页（歌词）
  ui_tap_text "$folders"; sleep 1
  for i in 1 2 3 4 5 6 7 8; do ui_has_text "Lady Gaga" && break; adb shell input swipe 500 1800 500 700 300; sleep 1; done
  ui_tap_text "Lady Gaga"; sleep 1
  ui_tap_id mini_player; sleep 1
  adb shell input tap 540 1846   # 进度条上的某个位置，让歌词滚到中间
  sleep 4
  ui_shot "$out/3.png"
  adb shell input keyevent BACK; sleep 1

  # 4. 智能标签
  ui_tap_text "$sport"; sleep 2
  ui_shot "$out/4.png"
}

shoot en-US en-US "Folders" "Artists" "Workout"
shoot zh-CN zh-CN "文件夹" "艺术家" "运动"
adb shell cmd locale set-app-locales $pkg --locales ""
echo "截图已保存到 fastlane/metadata/android/*/images/phoneScreenshots/"
