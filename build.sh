#!/usr/bin/env bash
# Builds overstrike-cli from source on Linux.
#
# Needs: git, make, a C compiler (gcc or clang), and the .NET 10 SDK
#   Arch/SteamOS:  sudo pacman -S git base-devel dotnet-sdk
#   Fedora:        sudo dnf install git make gcc dotnet-sdk-10.0
#   Ubuntu/Debian: sudo apt install git make gcc dotnet-sdk-10.0
#
# Usage:
#   ./build.sh                 self-contained build in dist/overstrike-cli/ (no .NET needed to run it)
#   ./build.sh --portable      framework-dependent build (needs the .NET 10 runtime to run)
#   ./build.sh --offline       build SharpCompress/K4os.LZ4 from git sources instead of NuGet
#   RID=linux-arm64 ./build.sh other architectures
set -euo pipefail
cd "$(dirname "$0")"

OVERSTRIKE_REPO=https://github.com/Tkachov/Overstrike.git
OVERSTRIKE_COMMIT=9f906ca09ea5ce4b84528410171c235bc4b0bfe6
LIBDEFLATE_REPO=https://github.com/NVIDIA/libdeflate.git
LIBDEFLATE_COMMIT=8ba9502fb30d2bf728592d121f0d402e40c8cb05
SHARPCOMPRESS_REPO=https://github.com/adamhathcock/sharpcompress.git
SHARPCOMPRESS_TAG=0.50.1
LZ4_REPO=https://github.com/MiloszKrajewski/K4os.Compression.LZ4.git
LZ4_TAG=1.3.8

PORTABLE=0
OFFLINE=0
for arg in "$@"; do
	case "$arg" in
		--portable) PORTABLE=1 ;;
		--offline) OFFLINE=1 ;;
		*) echo "unknown option: $arg"; exit 1 ;;
	esac
done
RID="${RID:-linux-x64}"

checkout() { # repo dir ref
	if [ ! -d "$2/.git" ]; then
		git clone --quiet "$1" "$2"
	fi
	git -C "$2" fetch --quiet --tags origin || true
	git -C "$2" checkout --quiet --force "$3"
}

echo "==> Overstrike source ($OVERSTRIKE_COMMIT)"
checkout "$OVERSTRIKE_REPO" upstream/Overstrike "$OVERSTRIKE_COMMIT"
git -C upstream/Overstrike clean --quiet -fdx
for p in patches/*.patch; do
	echo "    applying $p"
	git -C upstream/Overstrike apply --whitespace=nowarn "../../$p"
done

echo "==> libdeflate with GDeflate support"
checkout "$LIBDEFLATE_REPO" upstream/libdeflate "$LIBDEFLATE_COMMIT"
make -s -C upstream/libdeflate libdeflate.so >/dev/null
mkdir -p native
cp -L upstream/libdeflate/libdeflate.so native/libdeflate_gdeflate.so

DEPS_ARGS=()
if [ "$OFFLINE" = 1 ]; then
	echo "==> SharpCompress and K4os.Compression.LZ4 from source"
	checkout "$SHARPCOMPRESS_REPO" vendor/src/sharpcompress "$SHARPCOMPRESS_TAG"
	checkout "$LZ4_REPO" vendor/src/lz4 "$LZ4_TAG"
	DEPS_ARGS+=(-p:UseVendoredDeps=true)
fi

echo "==> dotnet publish ($RID)"
rm -rf dist/overstrike-cli
if [ "$PORTABLE" = 1 ]; then
	dotnet publish OverstrikeCLI/OverstrikeCLI.csproj -c Release --no-self-contained -p:UseAppHost=false \
		-p:DebugType=none "${DEPS_ARGS[@]}" -o dist/overstrike-cli
	cat > dist/overstrike-cli/overstrike-cli <<'EOF'
#!/bin/sh
exec dotnet "$(dirname "$(readlink -f "$0")")/overstrike-cli.dll" "$@"
EOF
	chmod +x dist/overstrike-cli/overstrike-cli
else
	dotnet publish OverstrikeCLI/OverstrikeCLI.csproj -c Release -r "$RID" --self-contained true \
		-p:PublishSingleFile=true -p:DebugType=none "${DEPS_ARGS[@]}" -o dist/overstrike-cli
fi
cp LICENSE.txt README.md dist/overstrike-cli/ 2>/dev/null || true

echo
echo "Done: dist/overstrike-cli/overstrike-cli"
echo "Try:  dist/overstrike-cli/overstrike-cli setup"
