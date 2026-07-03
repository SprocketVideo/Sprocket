#!/usr/bin/env bash
#
# Bundle Homebrew's FFmpeg 8 dylibs (plus their full transitive dependency closure) into a Sprocket
# macOS publish folder, rewriting every install name to @loader_path so the bundle is self-contained
# (ARCHITECTURE.md §11; PLAN.md step 36). BtbN publishes no macOS builds, so brew is the macOS FFmpeg
# source — its dylibs reference other kegs (x264, x265, opus, …) by absolute /opt/homebrew or
# /usr/local path, which is why the whole closure must be walked, copied, and rewritten (the older
# release.ps1 Repair-MacosInstallNames only rewrote sibling refs and left keg deps dangling).
#
#   scripts/macos-bundle-ffmpeg.sh <publish-dir>
#
# Guards: requires macOS + brew + a formula providing libavcodec major 62 (FFmpeg 8 — what
# Sprocket.Media's FFmpegLoader version-guards). Tries the versioned `ffmpeg@8` formula first, then
# plain `ffmpeg`; fails loudly if neither yields avcodec 62 rather than shipping a broken bundle.
set -euo pipefail

PUBLISH_DIR="${1:?usage: macos-bundle-ffmpeg.sh <publish-dir>}"
[[ -d "$PUBLISH_DIR" ]] || { echo "error: publish dir not found: $PUBLISH_DIR" >&2; exit 1; }
[[ "$(uname)" == "Darwin" ]] || { echo "error: must run on macOS (needs otool/install_name_tool)" >&2; exit 1; }
command -v brew >/dev/null || { echo "error: Homebrew not found" >&2; exit 1; }

REQUIRED_AVCODEC_MAJOR=62

# Resolve the FFmpeg keg: the pinned ffmpeg@8 formula first, falling back to core ffmpeg (currently
# also 8.x). The avcodec-major assert below is what actually protects against drift.
FF_PREFIX="$(brew --prefix ffmpeg@8 2>/dev/null || true)"
if [[ -z "$FF_PREFIX" || ! -d "$FF_PREFIX/lib" ]]; then
  FF_PREFIX="$(brew --prefix ffmpeg 2>/dev/null || true)"
fi
if [[ -z "$FF_PREFIX" || ! -d "$FF_PREFIX/lib" ]]; then
  echo "error: no Homebrew ffmpeg keg found — brew install ffmpeg@8 (or ffmpeg)" >&2
  exit 1
fi
if [[ ! -e "$FF_PREFIX/lib/libavcodec.${REQUIRED_AVCODEC_MAJOR}.dylib" ]]; then
  echo "error: $FF_PREFIX has no libavcodec.${REQUIRED_AVCODEC_MAJOR}.dylib — Sprocket requires FFmpeg 8" >&2
  echo "       (libavcodec major ${REQUIRED_AVCODEC_MAJOR}). Homebrew's formula may have moved to a newer" >&2
  echo "       FFmpeg major; install the versioned formula instead: brew install ffmpeg@8" >&2
  exit 1
fi
echo "using FFmpeg keg: $FF_PREFIX"

# The libraries Sprocket's binding loads (FFmpegLoader preload order); the BFS pulls in everything
# they transitively need (x264/x265/opus/… and their own deps).
SEEDS=(libavcodec libavutil libavformat libswscale libswresample)

# Resolve a dependency reference from `otool -L` to an actual file on disk. References are usually
# absolute keg paths; @loader_path/@rpath ones are resolved against the referencing lib's directory.
resolve_dep() {
  local ref="$1" from_dir="$2"
  case "$ref" in
    @loader_path/*|@rpath/*|@executable_path/*) echo "$from_dir/${ref#*/}" ;;
    *) echo "$ref" ;;
  esac
}

# Whether a dependency reference is a system library that must NOT be bundled.
is_system() {
  case "$1" in
    /usr/lib/*|/System/*) return 0 ;;
    *) return 1 ;;
  esac
}

# BFS over the dependency graph: queue holds absolute source paths; each is copied into the publish
# dir under its install-name leaf, and its non-system deps are enqueued.
declare -a QUEUE=()
for seed in "${SEEDS[@]}"; do
  # e.g. libavcodec.62.dylib — the version-suffixed name FFmpegLoader looks for beside the exe.
  path="$(ls "$FF_PREFIX/lib/$seed."*.dylib 2>/dev/null | grep -E "$seed\.[0-9]+\.dylib$" | head -n1)"
  [[ -n "$path" ]] || { echo "error: $seed not found in $FF_PREFIX/lib" >&2; exit 1; }
  QUEUE+=("$path")
done

declare -a COPIED=()   # leaf names copied into the publish dir
seen=" "
while ((${#QUEUE[@]})); do
  src="${QUEUE[0]}"; QUEUE=("${QUEUE[@]:1}")
  leaf="$(basename "$src")"
  [[ "$seen" == *" $leaf "* ]] && continue
  seen="$seen$leaf "

  real="$(python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$src")"
  [[ -f "$real" ]] || { echo "error: dependency not found on disk: $src" >&2; exit 1; }
  cp -f "$real" "$PUBLISH_DIR/$leaf"
  chmod u+w "$PUBLISH_DIR/$leaf"
  COPIED+=("$leaf")

  src_dir="$(dirname "$src")"
  while IFS= read -r line; do
    dep="$(echo "$line" | awk '{print $1}')"
    [[ -z "$dep" || "$dep" == *: ]] && continue     # skip the header line ("path:")
    is_system "$dep" && continue
    dep_path="$(resolve_dep "$dep" "$src_dir")"
    dep_leaf="$(basename "$dep_path")"
    [[ "$dep_leaf" == "$leaf" ]] && continue        # self-reference (the install-name id line)
    [[ "$seen" == *" $dep_leaf "* ]] && continue
    QUEUE+=("$dep_path")
  done < <(otool -L "$real" | tail -n +2)
done
echo "copied ${#COPIED[@]} dylibs (FFmpeg + transitive deps)"

# Rewrite: give every copied dylib an @loader_path id, point every non-system reference at the
# @loader_path sibling, and re-sign (mandatory on arm64 — install_name_tool invalidates the ad-hoc
# signature and unsigned/broken dylibs refuse to load).
for leaf in "${COPIED[@]}"; do
  path="$PUBLISH_DIR/$leaf"
  install_name_tool -id "@loader_path/$leaf" "$path" 2>/dev/null
  while IFS= read -r line; do
    dep="$(echo "$line" | awk '{print $1}')"
    [[ -z "$dep" ]] && continue
    is_system "$dep" && continue
    dep_leaf="$(basename "$dep")"
    [[ "$dep_leaf" == "$leaf" ]] && continue
    if [[ "$dep" != "@loader_path/$dep_leaf" ]]; then
      install_name_tool -change "$dep" "@loader_path/$dep_leaf" "$path" 2>/dev/null
    fi
  done < <(otool -L "$path" | tail -n +2)
  codesign -f -s - "$path" >/dev/null 2>&1
done
echo "rewrote install names to @loader_path and re-signed (ad-hoc)"

# Verify: no Homebrew/local absolute references may remain anywhere in the copied set.
bad=0
for leaf in "${COPIED[@]}"; do
  if otool -L "$PUBLISH_DIR/$leaf" | tail -n +2 | grep -qE '/(opt/homebrew|usr/local)/'; then
    echo "error: $leaf still references a Homebrew path:" >&2
    otool -L "$PUBLISH_DIR/$leaf" | grep -E '/(opt/homebrew|usr/local)/' >&2
    bad=1
  fi
done
[[ $bad -eq 0 ]] || exit 1
echo "verified: bundle is self-contained (no Homebrew/absolute refs)"
