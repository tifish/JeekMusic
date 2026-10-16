#!/usr/bin/env bash
# 测量冷启动耗时（TTID / TTFD），对比"不编译"和"Baseline Profile"两种编译状态。
#
# 为什么不用 Macrobenchmark：OriginOS 等国产 ROM 会拦截 Macrobenchmark 发给 App 的
# profileinstaller 广播，导致基准测试无法运行。这个脚本只用 adb shell 命令，任何 ROM 都能用。
#
# 用法：ANDROID_SERIAL=<设备序列号> tools/measure-startup.sh [次数，默认 10] [--no-install]
# 先执行 ./gradlew :app:assembleRelease 生成带 Baseline Profile 的 release 包。
set -euo pipefail

: "${ANDROID_SERIAL:?请设置 ANDROID_SERIAL}"
export ANDROID_SERIAL MSYS_NO_PATHCONV=1

runs=${1:-10}
pkg=com.tinyfish.jeekmusic
activity=$pkg/.MainActivity
apk="$(dirname "$0")/../app/build/outputs/apk/release/app-release.apk"

if [[ "${2:-}" != "--no-install" ]]; then
  echo "安装 $apk（真机上可能需要确认安装）"
  adb install -r "$apk" >/dev/null
fi
adb shell pm grant $pkg android.permission.READ_MEDIA_AUDIO || true

median() { sort -n | awk '{a[NR]=$1} END {if (NR==0) print "-"; else if (NR%2) print a[(NR+1)/2]; else print int((a[NR/2]+a[NR/2+1])/2)}'; }

measure() { # measure <标签>
  local label=$1 ttid=() ttfd=()
  for _ in $(seq 1 "$runs"); do
    adb shell am force-stop $pkg
    sleep 1
    adb logcat -c
    local out
    out=$(adb shell am start -W -n $activity)
    ttid+=("$(echo "$out" | tr -d '\r' | awk -F': ' '/TotalTime/ {print $2}')")
    sleep 2
    # 例：ActivityTaskManager: Fully drawn com.tinyfish.jeekmusic/.MainActivity: +612ms
    ttfd+=("$(adb logcat -d -s ActivityTaskManager:I | tr -d '\r' | grep -oE "Fully drawn $pkg[^+]*\+[0-9ms]+" | tail -1 | sed -E 's/.*\+//; s/([0-9]+)s([0-9]+)ms/\1\2/; s/ms//')")
  done
  printf "%-16s TTID 中位数 %4s ms（%s）\n" "$label" "$(printf '%s\n' "${ttid[@]}" | median)" "${ttid[*]}"
  printf "%-16s TTFD 中位数 %4s ms（%s）\n" "" "$(printf '%s\n' "${ttfd[@]}" | grep . | median)" "${ttfd[*]}"
}

echo "设备：$(adb shell getprop ro.product.model | tr -d '\r')，每种模式 $runs 次冷启动"

# 1. 不编译：只做字节码校验，相当于刚安装、没有任何 profile 的最差情况
adb shell cmd package compile -m verify -f $pkg >/dev/null
measure "不编译"

# 2. Baseline Profile：先启动一次，让 profileinstaller 把 APK 内的 profile 写入系统，
#    再按 profile 编译（相当于从应用商店安装后、系统完成后台编译的状态）
adb shell am start -W -n $activity >/dev/null
sleep 8
adb shell am force-stop $pkg
adb shell cmd package compile -m speed-profile -f $pkg >/dev/null
measure "Baseline Profile"
