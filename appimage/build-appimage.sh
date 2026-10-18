#!/usr/bin/env bash
# Packs overstrike-cli + the Qt GUI into one AppImage.
#
# Needs: a finished ./build.sh (dist/overstrike-cli), python3 with PySide6 and pyinstaller
#   (pip install PySide6-Essentials pyinstaller), and appimagetool
#   (https://github.com/AppImage/appimagetool/releases -- set APPIMAGETOOL=/path/to/it,
#    or it's downloaded next to this script).
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"
VERSION="${VERSION:-0.2.0}"
ARCH="${ARCH:-x86_64}"
WORK="$ROOT/appimage/build"
APPDIR="$WORK/Overstrike.AppDir"
OUT="$ROOT/dist/Overstrike-Linux-$VERSION-$ARCH.AppImage"

[ -x dist/overstrike-cli/overstrike-cli ] || { echo "run ./build.sh first"; exit 1; }

echo "==> GUI (PyInstaller)"
rm -rf "$WORK"
mkdir -p "$WORK"
python3 -m PyInstaller --noconfirm --clean --log-level WARN --windowed \
	--name overstrike-gui \
	--distpath "$WORK/pyi-dist" --workpath "$WORK/pyi-work" --specpath "$WORK" \
	--add-data "$ROOT/gui/check.svg:." --add-data "$ROOT/gui/overstrike-linux.png:." \
	--exclude-module tkinter --exclude-module PySide6.QtNetwork --exclude-module PySide6.QtQml \
	--exclude-module PySide6.QtQuick --exclude-module PySide6.QtOpenGL --exclude-module PySide6.QtPdf \
	--exclude-module PySide6.QtSvgWidgets \
	"$ROOT/gui/overstrike_gui.py"

echo "==> Pruning libraries the host system must provide"
INTERNAL="$WORK/pyi-dist/overstrike-gui/_internal"
# The GTK3 platform theme drags in all of GTK; Fusion + the desktop portal are enough.
rm -f "$INTERNAL"/PySide6/Qt/plugins/platformthemes/libqgtk3.so
for lib in libgtk-3 libgdk-3 libgdk_pixbuf-2.0 libatk-1.0 libatk-bridge-2.0 libatspi libcairo libcairo-gobject \
		libpango-1.0 libpangocairo-1.0 libpangoft2-1.0 libepoxy libdatrie libthai; do
	rm -f "$INTERNAL/$lib".so*
done
# AppImage's excludelist (https://github.com/AppImageCommunity/pkg2appimage/blob/master/excludelist):
# graphics drivers, X11/Wayland client libs, fonts and the C/C++ runtime must come from the host.
for lib in libstdc++.so.6 libgcc_s.so.1 libGL.so.1 libEGL.so.1 libGLdispatch.so.0 libGLX.so.0 libOpenGL.so.0 \
		libdrm.so.2 libglapi.so.0 libgbm.so.1 libxcb.so.1 libX11.so.6 libX11-xcb.so.1 libwayland-client.so.0 \
		libasound.so.2 libfontconfig.so.1 libfreetype.so.6 libharfbuzz.so.0 libcom_err.so.2 libexpat.so.1 \
		libgpg-error.so.0 libICE.so.6 libSM.so.6 libusb-1.0.so.0 libuuid.so.1 libz.so.1 libjack.so.0 \
		libpipewire-0.3.so.0 libxcb-dri3.so.0 libxcb-dri2.so.0 libfribidi.so.0 libgmp.so.10; do
	rm -f "$INTERNAL/$lib"
done

echo "==> AppDir"
mkdir -p "$APPDIR/usr/lib" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps" "$APPDIR/usr/share/metainfo"
cp -a "$WORK/pyi-dist/overstrike-gui" "$APPDIR/usr/lib/overstrike-gui"
cp -a dist/overstrike-cli "$APPDIR/usr/lib/overstrike-cli"

cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
# Overstrike for Linux. Runs the GUI; "cli" as the first argument runs overstrike-cli instead:
#   ./Overstrike-Linux.AppImage cli install
HERE="$(dirname "$(readlink -f "$0")")"
export OVERSTRIKE_CLI="$HERE/usr/lib/overstrike-cli/overstrike-cli"
case "${1:-}" in
	cli|--cli) shift; exec "$OVERSTRIKE_CLI" "$@" ;;
esac
exec "$HERE/usr/lib/overstrike-gui/overstrike-gui" "$@"
EOF
chmod +x "$APPDIR/AppRun"

cat > "$APPDIR/overstrike-linux.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Overstrike for Linux
GenericName=Mod manager
Comment=Install mods for Insomniac's PC ports (Spider-Man 2, Spider-Man Remastered, Miles Morales, Ratchet & Clank)
Exec=AppRun
Icon=overstrike-linux
Terminal=false
Categories=Game;Utility;
Keywords=mods;spider-man;insomniac;overstrike;
EOF
cp "$APPDIR/overstrike-linux.desktop" "$APPDIR/usr/share/applications/"
cp gui/overstrike-linux-256.png "$APPDIR/overstrike-linux.png"
cp gui/overstrike-linux-256.png "$APPDIR/usr/share/icons/hicolor/256x256/apps/overstrike-linux.png"
ln -sf overstrike-linux.png "$APPDIR/.DirIcon"

cat > "$APPDIR/usr/share/metainfo/overstrike-linux.appdata.xml" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<component type="desktop-application">
  <id>overstrike-linux</id>
  <name>Overstrike for Linux</name>
  <summary>Mod manager for Insomniac's PC ports</summary>
  <metadata_license>CC0-1.0</metadata_license>
  <project_license>GPL-3.0-only</project_license>
  <description>
    <p>An unofficial Linux port of Overstrike, the mod manager for Marvel's Spider-Man 2, Spider-Man Remastered,
    Miles Morales and Ratchet &amp; Clank: Rift Apart. Mods are installed by Overstrike's own code.</p>
  </description>
  <launchable type="desktop-id">overstrike-linux.desktop</launchable>
  <url type="homepage">https://github.com/Tkachov/Overstrike</url>
  <releases><release version="$VERSION" date="$(date +%Y-%m-%d)"/></releases>
  <content_rating type="oars-1.1"/>
</component>
EOF

echo "==> AppImage"
TOOL="${APPIMAGETOOL:-$ROOT/appimage/appimagetool-$ARCH.AppImage}"
if [ ! -x "$TOOL" ]; then
	curl -fsSL -o "$TOOL" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$ARCH.AppImage"
	chmod +x "$TOOL"
fi
mkdir -p dist
ARCH="$ARCH" "$TOOL" --appimage-extract-and-run --no-appstream "$APPDIR" "$OUT"
echo
echo "Done: $OUT"
