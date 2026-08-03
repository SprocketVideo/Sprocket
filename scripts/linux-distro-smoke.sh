#!/usr/bin/env bash
#
# Sprocket multi-distro / multi-arch headless smoke.
# Runs a published, self-contained Sprocket linux zip inside a CLEAN distro container and confirms the
# bundled build actually runs there: it installs the DOCUMENTED runtime dependencies via whatever package
# manager the distro ships, unzips the bundle, and runs the headless self-checks with LD_LIBRARY_PATH
# unset (so native resolution must come from the bundled libs). This is what widens the release's Linux
# coverage beyond the single Ubuntu runner in .github/workflows/release.yml — see the Linux dependency
# table in RELEASE_NOTES.md, which this script's install lists mirror.
#
# It is deliberately best-effort / informational: `--doctor` runs first purely to report the host and any
# still-missing libraries, and only the hard checks (--ffmpeg-check / --audio-check / --mcp-check) decide
# PASS/FAIL. Intended to run in CI (one distro container per matrix leg); usable locally too, e.g.:
#   docker run --rm -v "$PWD:/repo" -w /repo ubuntu:20.04 bash /repo/scripts/linux-distro-smoke.sh /repo/dist
#
# $1 = directory holding the Sprocket-*-linux-{x64,arm64}.zip artifacts (default /repo/artifacts/linux).
set -uo pipefail

DIST="${1:-/repo/artifacts/linux}"
export ALSOFT_DRIVERS=null          # soundcard-less container: OpenAL Soft's null driver still loads the native
export DEBIAN_FRONTEND=noninteractive

echo "== distro =="
grep PRETTY_NAME /etc/os-release || true
arch="$(uname -m)"
echo "arch: $arch"

# Install the documented runtime deps plus `unzip` (bare images lack it), via the distro's package manager.
# Best-effort: a failure here is not fatal — `--doctor` reports whatever is still missing, and the hard
# checks below fail loudly if a genuinely-required lib is absent.
echo "== installing documented runtime deps =="
if command -v apt-get >/dev/null 2>&1; then
    apt-get update -qq && apt-get install -y -qq \
        unzip libva2 libva-drm2 libvdpau1 libdrm2 libxml2 libfontconfig1 libx11-6 || true
elif command -v dnf >/dev/null 2>&1; then
    dnf install -y -q unzip libva libvdpau libdrm libxml2 fontconfig libX11 || true
elif command -v zypper >/dev/null 2>&1; then
    zypper --non-interactive --gpg-auto-import-keys install -y \
        unzip libva2 libva-drm2 libvdpau1 libdrm2 libxml2-2 fontconfig libX11-6 || true
elif command -v pacman >/dev/null 2>&1; then
    pacman -Sy --noconfirm unzip libva libvdpau libdrm libxml2 fontconfig libx11 || true
else
    echo "[warn] no known package manager found — skipping dep install"
fi

# Select the zip matching this container's architecture.
case "$arch" in
    x86_64|amd64)  rid=linux-x64 ;;
    aarch64|arm64) rid=linux-arm64 ;;
    *) echo "[smoke] unsupported arch '$arch'"; echo "[smoke] RESULT: FAIL"; exit 1 ;;
esac

zip="$(ls "$DIST"/Sprocket-*-"$rid".zip 2>/dev/null | head -n1)"
if [[ -z "$zip" || ! -f "$zip" ]]; then
    echo "[smoke] No $rid zip found under $DIST" >&2
    echo "[smoke] RESULT: FAIL"
    exit 1
fi
echo "bundle: $zip"

dir="/tmp/smoke-$rid"
rm -rf "$dir" && mkdir -p "$dir"
unzip -q "$zip" -d "$dir"
chmod +x "$dir/Sprocket"
unset LD_LIBRARY_PATH

echo "== --doctor (informational) =="
"$dir/Sprocket" --doctor || true

rc=0
for check in --version --ffmpeg-check --audio-check --mcp-check; do
    echo "== $check =="
    if ! "$dir/Sprocket" "$check"; then
        echo "[smoke] $check FAILED"
        rc=1
    fi
done

if [[ $rc -eq 0 ]]; then
    echo "[smoke] RESULT: PASS"
else
    echo "[smoke] RESULT: FAIL"
fi
exit $rc
