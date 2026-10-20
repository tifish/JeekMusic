#!/usr/bin/env bash
# 下载 shinchiro/mpv-winbuild-cmake 最新的 libmpv（x86_64 通用版，不要求 AVX2），只取出 libmpv-2.dll 放到 native/。
# 压缩包解到单独的临时目录，用完删除。需要 curl、python、7z。
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
url=$(curl -fsSL https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest | python -c "
import json, re, sys
for a in json.load(sys.stdin)['assets']:
    if re.fullmatch(r'mpv-dev-x86_64-\d+-git-\w+\.7z', a['name']): print(a['browser_download_url']); break")
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
echo "下载 $url"
curl -fL --progress-bar -o "$tmp/mpv-dev.7z" "$url"
7z e -y -o"$tmp/x" "$tmp/mpv-dev.7z" libmpv-2.dll > /dev/null
mkdir -p "$here/native"
cp "$tmp/x/libmpv-2.dll" "$here/native/"
basename "$url" > "$here/native/VERSION"
ls -la "$here/native"
