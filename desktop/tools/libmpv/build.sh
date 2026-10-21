#!/usr/bin/env bash
# 编译 LGPL、只含音频的 libmpv-2.dll（x86_64）。桌面端需求 UP-3，设计 §15.3。
#
# 在 MSYS2 的 UCRT64 环境里运行（CI 见 .github/workflows/libmpv.yml；本机装了 MSYS2 也可以直接跑）：
#   pacman -S git make diffutils tar xz zip mingw-w64-ucrt-x86_64-{gcc,meson,ninja,pkgconf,nasm,python}
#   bash desktop/tools/libmpv/build.sh
#
# 做法：所有依赖从源码编成静态库，装到 $WORK/prefix，最后把它们全部静态链接进一个 libmpv-2.dll，
# 只依赖 Windows 自带的 DLL。
#   - FFmpeg：不开 --enable-gpl / --enable-nonfree；--disable-everything 之后只开需要的音频解封装器、解码器、
#     解析器、file 协议和几个音频滤镜；不要视频编解码器、硬件加速、网络。libswscale、libavfilter 是 mpv 必须的库，
#     只编库本身（swscale 没有可选组件，avfilter 只开几个音频滤镜）。
#   - mpv：-Dgpl=false -Dlibmpv=true，--auto-features=disabled 之后只开 wasapi、win32 线程、向量指令；
#     不要 cplayer、lua、javascript、视频输出、硬件解码，也不要 mpv 自己的 SMTC（win32-smtc；JeekMusic 有自己的）。
#   - libplacebo、libass 是 mpv 必须的依赖：都用最小配置（libplacebo 不带 vulkan / opengl / d3d11 / shaderc，
#     libass 只用 DirectWrite 找字体），它们在纯音频播放时不会被用到。
#
# 输出（$OUT）：libmpv-2.dll、libmpv-lgpl-x86_64.zip（DLL + 许可证 + BUILDINFO.txt）、sources-lgpl.tar.xz（LGPL 组件的源码）。
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=versions.sh
source "$here/versions.sh"

WORK="${WORK:-$PWD/build-libmpv}"
PREFIX="$WORK/prefix"
SRC="$WORK/src"
OUT="${OUT:-$WORK/out}"
mkdir -p "$PREFIX" "$SRC" "$OUT"
export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig"
export PATH="$PREFIX/bin:$PATH"
JOBS="$(nproc)"
# 依赖都是静态库，最后链接成 DLL 时连 libgcc / libstdc++ / winpthread 也静态链接进去
export CFLAGS="-O2 -pipe"
export CXXFLAGS="-O2 -pipe"

log() { echo "::group::$*" 2>/dev/null || true; echo "== $*"; }
endlog() { echo "::endgroup::" 2>/dev/null || true; }

# 源码：浅克隆指定的标签（libplacebo 要带子模块：fast_float、jinja 等）
fetch() {
    local name=$1 repo=$2 ref=$3
    if [ -d "$SRC/$name/.git" ]; then return; fi
    log "下载 $name $ref"
    git clone --quiet --depth 1 --branch "$ref" --recurse-submodules --shallow-submodules "$repo" "$SRC/$name"
    endlog
}

# 已经编过同一版本（CI 缓存了 prefix）就跳过
stamp() { echo "$PREFIX/.stamp-$1-$2"; }
built() { [ -f "$(stamp "$1" "$2")" ]; }
done_() { touch "$(stamp "$1" "$2")"; }

meson_static() {
    local name=$1; shift
    rm -rf "$SRC/$name/_build"
    meson setup "$SRC/$name/_build" "$SRC/$name" --prefix="$PREFIX" --libdir=lib --buildtype=release \
        --default-library=static --wrap-mode=nodownload "$@"
    ninja -C "$SRC/$name/_build"
    ninja -C "$SRC/$name/_build" install
}

fetch ffmpeg "$FFMPEG_REPO" "$FFMPEG_REF"
fetch mpv "$MPV_REPO" "$MPV_REF"
fetch libplacebo "$LIBPLACEBO_REPO" "$LIBPLACEBO_REF"
fetch libass "$LIBASS_REPO" "$LIBASS_REF"
fetch freetype "$FREETYPE_REPO" "$FREETYPE_REF"
fetch fribidi "$FRIBIDI_REPO" "$FRIBIDI_REF"
fetch harfbuzz "$HARFBUZZ_REPO" "$HARFBUZZ_REF"

# ── FFmpeg ──
FFMPEG_DECODERS="mp1,mp1float,mp2,mp2float,mp3,mp3float,flac,aac,aac_fixed,aac_latm,alac,vorbis,opus,ape,wmav1,wmav2,wmapro,wmalossless,tak,wavpack,mpc7,mpc8"
FFMPEG_DECODERS="$FFMPEG_DECODERS,pcm_s8,pcm_u8,pcm_s16le,pcm_s16be,pcm_s16le_planar,pcm_s24le,pcm_s24be,pcm_s32le,pcm_s32be,pcm_f32le,pcm_f32be,pcm_f64le,pcm_f64be,pcm_alaw,pcm_mulaw,adpcm_ms,adpcm_ima_wav"
FFMPEG_DEMUXERS="mp3,flac,aac,mov,ogg,wav,w64,aiff,ape,asf,tak,wv,mpc,mpc8,matroska,caf"
FFMPEG_PARSERS="mpegaudio,flac,aac,aac_latm,vorbis,opus,tak"
FFMPEG_FILTERS="abuffer,abuffersink,aformat,anull,aresample,atrim,asetnsamples,volume,buffer,buffersink,format,null"
FFMPEG_ARGS=(
    --prefix="$PREFIX" --pkg-config-flags=--static
    --enable-static --disable-shared --disable-programs --disable-doc --disable-debug
    --disable-everything --disable-autodetect --disable-network --disable-iconv --enable-w32threads
    --disable-avdevice --enable-avcodec --enable-avformat --enable-avfilter --enable-swresample --enable-swscale
    --enable-protocol=file
    --enable-demuxer="$FFMPEG_DEMUXERS" --enable-decoder="$FFMPEG_DECODERS"
    --enable-parser="$FFMPEG_PARSERS" --enable-filter="$FFMPEG_FILTERS"
)
if ! built ffmpeg "$FFMPEG_REF"; then
    log "编译 FFmpeg $FFMPEG_REF"
    cd "$SRC/ffmpeg"
    ./configure "${FFMPEG_ARGS[@]}"
    make -j"$JOBS"
    make install
    cd - >/dev/null
    done_ ffmpeg "$FFMPEG_REF"
    endlog
fi

# ── libass 和它的依赖（freetype、fribidi、harfbuzz）──
if ! built freetype "$FREETYPE_REF"; then
    log "编译 freetype"
    meson_static freetype --auto-features=disabled
    done_ freetype "$FREETYPE_REF"; endlog
fi
if ! built fribidi "$FRIBIDI_REF"; then
    log "编译 fribidi"
    meson_static fribidi -Ddocs=false -Dbin=false -Dtests=false
    done_ fribidi "$FRIBIDI_REF"; endlog
fi
if ! built harfbuzz "$HARFBUZZ_REF"; then
    log "编译 harfbuzz"
    meson_static harfbuzz --auto-features=disabled -Dfreetype=enabled
    # harfbuzz 是 C++ 写的：静态链接进 C 程序时要带上 libstdc++
    pc="$PREFIX/lib/pkgconfig/harfbuzz.pc"
    if grep -q '^Libs.private:' "$pc"; then sed -i 's/^\(Libs.private:.*\)$/\1 -lstdc++/' "$pc"; else echo 'Libs.private: -lstdc++' >> "$pc"; fi
    done_ harfbuzz "$HARFBUZZ_REF"; endlog
fi
if ! built libass "$LIBASS_REF"; then
    log "编译 libass"
    meson_static libass --auto-features=disabled -Ddirectwrite=enabled -Dasm=enabled -Drequire-system-font-provider=false -Dtest=disabled
    done_ libass "$LIBASS_REF"; endlog
fi

# ── libplacebo（不带任何 GPU 后端）──
if ! built libplacebo "$LIBPLACEBO_REF"; then
    log "编译 libplacebo"
    meson_static libplacebo --auto-features=disabled -Ddemos=false -Dtests=false -Dbench=false -Dfuzz=false
    done_ libplacebo "$LIBPLACEBO_REF"; endlog
fi

# ── mpv（每次都编，几分钟）──
log "编译 mpv $MPV_REF"
MPV_ARGS=(
    --prefix="$WORK/mpv-install" --libdir=lib --buildtype=release --prefer-static --default-library=shared
    --wrap-mode=nodownload --auto-features=disabled
    -Dgpl=false -Dlibmpv=true -Dcplayer=false -Dtests=false -Dfuzzers=false -Dbuild-date=false
    -Dwasapi=enabled -Dwin32-threads=enabled -Dvector=enabled -Dgl=disabled
    "-Dc_link_args=-static -static-libgcc -static-libstdc++"
)
rm -rf "$SRC/mpv/_build"
meson setup "$SRC/mpv/_build" "$SRC/mpv" "${MPV_ARGS[@]}"
ninja -C "$SRC/mpv/_build"
ninja -C "$SRC/mpv/_build" install
endlog

DLL="$WORK/mpv-install/bin/libmpv-2.dll"
cp "$DLL" "$OUT/libmpv-2.dll"
strip --strip-unneeded "$OUT/libmpv-2.dll"

# 只应该依赖 Windows 自带的 DLL（不能有 libgcc、libstdc++、libwinpthread、avcodec 之类的）
log "检查依赖的 DLL"
imports="$(objdump -p "$OUT/libmpv-2.dll" | sed -n 's/^\s*DLL Name: //p' | sort -u)"
echo "$imports"
bad="$(echo "$imports" | grep -viE '^(kernel32|user32|gdi32|advapi32|shell32|ole32|oleaut32|uuid|winmm|avrt|ws2_32|bcrypt|crypt32|secur32|shlwapi|version|ntdll|dwrite|imm32|comdlg32|setupapi|ksuser|propsys|mf|mfplat|mfuuid|dxgi|d3d11|dbghelp|psapi|userenv|api-ms-win-.*|ucrtbase|msvcrt)\.dll$' || true)"
if [ -n "$bad" ]; then
    echo "依赖了不该依赖的 DLL：$bad"
    exit 1
fi
endlog

# ── 说明和许可证（LGPL 的要求：附上许可证、配置和对应的源码）──
log "打包"
PKG="$WORK/pkg"
rm -rf "$PKG"; mkdir -p "$PKG/licenses"
cp "$OUT/libmpv-2.dll" "$PKG/"
cp "$SRC/mpv/LICENSE.LGPL" "$PKG/licenses/mpv-LICENSE.LGPL"
cp "$SRC/mpv/Copyright" "$PKG/licenses/mpv-Copyright"
cp "$SRC/ffmpeg/COPYING.LGPLv2.1" "$PKG/licenses/ffmpeg-COPYING.LGPLv2.1"
cp "$SRC/ffmpeg/LICENSE.md" "$PKG/licenses/ffmpeg-LICENSE.md"
cp "$SRC/libplacebo/LICENSE" "$PKG/licenses/libplacebo-LICENSE"
cp "$SRC/libass/COPYING" "$PKG/licenses/libass-COPYING"
cp "$SRC/fribidi/COPYING" "$PKG/licenses/fribidi-COPYING"
cp "$SRC/harfbuzz/COPYING" "$PKG/licenses/harfbuzz-COPYING"
cp "$SRC/freetype/LICENSE.TXT" "$PKG/licenses/freetype-LICENSE.TXT"
cp "$SRC/freetype/docs/FTL.TXT" "$PKG/licenses/freetype-FTL.TXT"
{
    echo "JeekMusic 用的 libmpv（LGPL、只含音频）"
    echo "编译时间：$(date -u '+%Y-%m-%d %H:%M UTC')"
    echo "编译器：$(gcc --version | head -1)"
    echo "编译脚本：desktop/tools/libmpv/build.sh（https://github.com/tifish/JeekMusic）"
    echo
    echo "源码版本："
    for v in MPV FFMPEG LIBPLACEBO LIBASS FREETYPE FRIBIDI HARFBUZZ; do
        repo="${v}_REPO"; ref="${v}_REF"
        echo "  ${!repo} ${!ref} ($(git -C "$SRC/$(echo "$v" | tr 'A-Z' 'a-z')" rev-parse HEAD))"
    done
    echo
    echo "FFmpeg configure："
    printf '  %s\n' "${FFMPEG_ARGS[@]}"
    echo
    echo "mpv meson："
    printf '  %s\n' "${MPV_ARGS[@]}"
    echo
    echo "依赖的 DLL："
    echo "$imports" | sed 's/^/  /'
    echo
    echo "LGPL 组件（mpv、FFmpeg、libplacebo、fribidi）的完整源码在同一个 Release 的 sources-lgpl.tar.xz 里。"
} > "$PKG/BUILDINFO.txt"
cat "$PKG/BUILDINFO.txt"
(cd "$PKG" && rm -f "$OUT/libmpv-lgpl-x86_64.zip" && zip -qr "$OUT/libmpv-lgpl-x86_64.zip" .)
tar --exclude=.git --exclude=_build -cJf "$OUT/sources-lgpl.tar.xz" -C "$SRC" mpv ffmpeg libplacebo fribidi -C "$here" build.sh versions.sh
cp "$PKG/BUILDINFO.txt" "$OUT/"
ls -la "$OUT"
endlog
