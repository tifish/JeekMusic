#!/usr/bin/env bash
# 模拟器 UI 自动化小工具（source 后使用），用于手动 / 脚本化的端到端验证。
#
#   source tools/ui.sh
#   ui_tap_text "添加音乐文件夹"     # 按文字点击
#   ui_tap_id add_folder             # 按 testTag（resource-id）点击
#   ui_wait_text "设置" 10           # 等待文字出现，最多 10 秒
#   ui_shot /tmp/a.png               # 截图
#
# 依赖 ANDROID_SERIAL；默认 emulator-5554。

export ANDROID_SERIAL=${ANDROID_SERIAL:-emulator-5554}
export MSYS_NO_PATHCONV=1

ui_dump() {
  adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1
  adb exec-out cat /sdcard/ui.xml
}

# 输出匹配节点的中心坐标 "x y"。$1 = 属性名（text / resource-id / content-desc），$2 = 值（完全匹配）
ui_find() {
  ui_dump | python -I -X utf8 -c '
import re, sys
attr, value = sys.argv[1], sys.argv[2]
xml = sys.stdin.read()
for node in re.finditer(r"<node [^>]*>", xml):
    n = node.group(0)
    m = re.search(attr + r"=\"([^\"]*)\"", n)
    if m and (m.group(1) == value or m.group(1).endswith(":id/" + value)):
        b = re.search(r"bounds=\"\[(\d+),(\d+)\]\[(\d+),(\d+)\]\"", n)
        x1, y1, x2, y2 = map(int, b.groups())
        print((x1 + x2) // 2, (y1 + y2) // 2)
        break
' "$1" "$2"
}

_ui_tap() {
  local pos
  pos=$(ui_find "$1" "$2")
  if [ -z "$pos" ]; then echo "未找到 $1=$2" >&2; return 1; fi
  adb shell input tap $pos
}

ui_tap_text() { _ui_tap text "$1"; }
ui_tap_id() { _ui_tap resource-id "$1"; }
ui_tap_desc() { _ui_tap content-desc "$1"; }

ui_wait_text() {
  local deadline=$((SECONDS + ${2:-10}))
  while [ $SECONDS -lt $deadline ]; do
    [ -n "$(ui_find text "$1")" ] && return 0
    sleep 1
  done
  echo "等待超时：$1" >&2
  return 1
}

ui_has_text() { [ -n "$(ui_find text "$1")" ]; }

ui_shot() { adb exec-out screencap -p > "$1"; }
