#!/usr/bin/env bash
#
# Removes the Sprocket desktop integration installed by install.sh for the current user. Does not touch the
# extracted application files themselves — delete the release folder separately if you want those gone too.
set -euo pipefail

data="${XDG_DATA_HOME:-$HOME/.local/share}"

rm -f "$data/applications/sprocket.desktop"
rm -f "$data/icons/hicolor/1024x1024/apps/sprocket.png"
rm -f "$data/pixmaps/sprocket.png"

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data/applications" >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -f -t "$data/icons/hicolor" >/dev/null 2>&1 || true

echo "Removed Sprocket desktop entry and icon for the current user."
