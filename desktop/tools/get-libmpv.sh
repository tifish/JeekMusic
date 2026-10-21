#!/usr/bin/env bash
# 下载 libmpv-2.dll 到 desktop/native/（设计 §15.3）。
#
#   bash desktop/tools/get-libmpv.sh          开发用：shinchiro/mpv-winbuild-cmake 最新的现成版本（GPL、约 121MB、含视频功能）
#   bash desktop/tools/get-libmpv.sh --lgpl   发布用：本仓库 CI（.github/workflows/libmpv.yml）自己编的 LGPL、只含音频的版本，
#                                            取最新的 libmpv-lgpl-* Release；找不到时以非 0 退出
#
# 需要 curl、python；现成版本还要 7z（压缩包解到单独的临时目录，用完删除）。
# 设置了 GITHUB_TOKEN（CI 里）时带上它，避免 GitHub API 的匿名频率限制。
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$here/native"
GITHUB_TOKEN="${GITHUB_TOKEN:-${GH_TOKEN:-}}"
auth=()
if [ -n "${GITHUB_TOKEN:-}" ]; then auth=(-H "Authorization: Bearer $GITHUB_TOKEN"); fi

if [ "${1:-}" = "--lgpl" ]; then
    repo="${JEEK_REPO:-tifish/JeekMusic}"
    tmp=$(mktemp -d)
    trap 'rm -rf "$tmp"' EXIT
    curl -fsSL "${auth[@]}" "https://api.github.com/repos/$repo/releases?per_page=100" -o "$tmp/releases.json"
    # 最新的 libmpv-lgpl-* 发布（是 prerelease，不会出现在 releases/latest 里）
    read -r tag url < <(python -c "
import json, sys
sys.stdout.reconfigure(newline='\n')
with open(sys.argv[1], encoding='utf-8') as f: releases = json.load(f)
rels = [r for r in releases if r['tag_name'].startswith('libmpv-lgpl-') and not r['draft']]
rels.sort(key=lambda r: r['created_at'], reverse=True)
for r in rels:
    for a in r['assets']:
        if a['name'] == 'libmpv-lgpl-x86_64.zip':
            print(r['tag_name'], a['browser_download_url']); sys.exit(0)
sys.exit(1)" "$tmp/releases.json") || { echo "找不到 libmpv-lgpl-* 发布（先运行 .github/workflows/libmpv.yml）" >&2; exit 1; }
    echo "下载 $tag"
    curl -fL --progress-bar "${auth[@]}" -o "$tmp/libmpv.zip" "$url"
    python - "$tmp/libmpv.zip" "$here/native" <<'PY'
import pathlib, sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as archive:
    names = archive.namelist()
    for required in ('libmpv-2.dll', 'BUILDINFO.txt', 'licenses/mpv-LICENSE.LGPL'):
        if required not in names: raise SystemExit('Incomplete LGPL package: ' + required)
    for name in names:
        if name.endswith('/') or name not in ('libmpv-2.dll', 'BUILDINFO.txt') and not name.startswith('licenses/'):
            continue
        target = pathlib.Path(sys.argv[2]) / name
        if '..' in pathlib.PurePosixPath(name).parts: raise SystemExit('Invalid archive path')
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(archive.read(name))
PY
    printf 'Corresponding source and build scripts:\nhttps://github.com/%s/releases/tag/%s\nhttps://github.com/%s/releases/download/%s/sources-lgpl.tar.xz\n\nReplace libmpv-2.dll with an ABI-compatible version to use a modified library.\n' "$repo" "$tag" "$repo" "$tag" > "$here/native/licenses/SOURCE.txt"
    echo "$tag" > "$here/native/VERSION"
    ls -la "$here/native"
    exit 0
fi

url=$(curl -fsSL "${auth[@]}" https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest | python -c "
import json, re, sys
for a in json.load(sys.stdin)['assets']:
    if re.fullmatch(r'mpv-dev-x86_64-\d+-git-\w+\.7z', a['name']): print(a['browser_download_url']); break")
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
echo "下载 $url"
curl -fL --progress-bar -o "$tmp/mpv-dev.7z" "$url"
7z e -y -o"$tmp/x" "$tmp/mpv-dev.7z" libmpv-2.dll > /dev/null
cp "$tmp/x/libmpv-2.dll" "$here/native/"
basename "$url" > "$here/native/VERSION"
ls -la "$here/native"
