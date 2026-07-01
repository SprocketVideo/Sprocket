#!/usr/bin/env bash
#
# Installs Sprocket desktop integration for the current user (no root needed): registers an application
# launcher and installs the app icon into the freedesktop hicolor theme, so Sprocket shows up in the
# applications menu / activities and on the dock/taskbar with its proper icon (PLAN.md step 36).
#
# Run it from inside the extracted release folder (the one holding the `Sprocket` executable):
#     chmod +x install.sh && ./install.sh
#
# The launcher points at wherever you extracted the app; re-run this if you move the folder.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bin="$here/Sprocket"

if [ ! -f "$bin" ]; then
    echo "error: 'Sprocket' executable not found next to this script ($bin)." >&2
    echo "       Run install.sh from inside the extracted release folder." >&2
    exit 1
fi
# The .zip release does not preserve the Unix executable bit; set it so the launcher works.
chmod +x "$bin" 2>/dev/null || true

data="${XDG_DATA_HOME:-$HOME/.local/share}"
apps="$data/applications"
# The icon ships at 1024x1024; install into the matching hicolor bucket (the theme downscales as needed)
# and also into pixmaps as a broad fallback for panels/file managers that ignore icon-theme sizing.
icondir="$data/icons/hicolor/1024x1024/apps"
pixmaps="$data/pixmaps"
mkdir -p "$apps" "$icondir" "$pixmaps"

install -m 644 "$here/sprocket.png" "$icondir/sprocket.png"
install -m 644 "$here/sprocket.png" "$pixmaps/sprocket.png"

# Materialise the launcher with the resolved absolute Exec path.
sed "s|@EXEC@|$bin|g" "$here/sprocket.desktop" > "$apps/sprocket.desktop"
chmod 644 "$apps/sprocket.desktop"

# Refresh the desktop / icon caches so the entry and icon appear without a re-login (best-effort).
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$apps" >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -f -t "$data/icons/hicolor" >/dev/null 2>&1 || true

echo "Installed Sprocket desktop integration:"
echo "  launcher: $apps/sprocket.desktop"
echo "  icon:     $icondir/sprocket.png"
echo
echo "Sprocket should now appear in your applications menu. If the icon does not show up"
echo "immediately, log out and back in (or restart the desktop shell) to refresh the caches."
echo "To remove it later, run ./uninstall.sh."
