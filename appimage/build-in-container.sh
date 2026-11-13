#!/usr/bin/env bash
# Release build of the AppImage: freezes the GUI inside an Ubuntu 20.04 container (glibc 2.31)
# instead of on your own system, so the AppImage runs on older distros than the one you build on.
#
# Needs docker or podman, and a finished ./build.sh (the CLI only needs glibc 2.27 wherever it's built).
set -euo pipefail
cd "$(dirname "$0")/.."
[ -x dist/overstrike-cli/overstrike-cli ] || { echo "run ./build.sh first"; exit 1; }
ENGINE="${ENGINE:-$(command -v podman || command -v docker)}"
IMAGE=overstrike-linux-build:focal
"$ENGINE" build -t "$IMAGE" -f appimage/Containerfile appimage
"$ENGINE" run --rm -v "$PWD:/src:Z" -w /src -e VERSION="${VERSION:-1.0}" -e HOST_ID="$(id -u):$(id -g)" \
	${ENGINE_RUN_ARGS:-} "$IMAGE" \
	bash -c 'trap "chown -R \$HOST_ID /src/dist /src/appimage 2>/dev/null || true" EXIT
		appimage/build-appimage.sh'
