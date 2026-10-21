#!/usr/bin/env bash
# End-to-end test of overstrike-cli against a synthetic Spider-Man 2 install (no real game needed).
# usage: tests/run_tests.sh [path/to/overstrike-cli]   (run ./build.sh first)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CLI="${1:-$ROOT/dist/overstrike-cli/overstrike-cli}"
LIB="$ROOT/native/libdeflate_gdeflate.so"
T="$(mktemp -d)"
trap 'rm -rf "$T"' EXIT

pass=0; fail=0
check() { if eval "$2"; then echo "  ok   $1"; pass=$((pass+1)); else echo "  FAIL $1"; fail=$((fail+1)); fi; }

# fake Steam layout with a second library, like a game on another drive
export HOME="$T/home"
mkdir -p "$HOME/.local/share/Steam/steamapps" "$T/lib/steamapps/common"
printf '"libraryfolders"\n{\n\t"0"\n\t{\n\t\t"path"\t\t"%s"\n\t}\n\t"1"\n\t{\n\t\t"path"\t\t"%s"\n\t}\n}\n' \
	"$HOME/.local/share/Steam" "$T/lib" > "$HOME/.local/share/Steam/steamapps/libraryfolders.vdf"
G="$T/lib/steamapps/common/Marvel's Spider-Man 2"
python3 "$ROOT/tests/make_fake_game.py" "$G" "$T/mods" "$LIB" >/dev/null
cp "$G/toc" "$T/toc.orig"

# reader for checking asset bytes through the toc
CLI_DLL="$(find "$ROOT/OverstrikeCLI/bin" -name overstrike-cli.dll 2>/dev/null | head -1)"
if [ -n "$CLI_DLL" ]; then
	dotnet build "$ROOT/tests/ReadCheck/ReadCheck.csproj" -c Release -o "$T/rc" -p:CliDir="$(dirname "$CLI_DLL")" >/dev/null 2>&1 || true
fi
python3 - "$T/mods/expected.json" <<'EOF'
import json, sys
e = json.load(open(sys.argv[1])); e["nested"] = (b"nested mod data " * 10).hex(); json.dump(e, open(sys.argv[1], "w"))
EOF

echo "detect + setup"
check "finds the game in the second Steam library" '"$CLI" games | grep -q "Spider-Man 2"'
"$CLI" setup >/dev/null
check "profile created" '"$CLI" profile list | grep -q "Marvel.s Spider-Man 2"'

echo "add + list"
"$CLI" add "$T/mods/test_stage.stage" "$T/mods/nested_pack.zip" >/dev/null
check "stage mod detected (Windows-style paths in zip)" '"$CLI" list | grep -q "Test Stage"'
check "stage inside .zip detected" '"$CLI" list | grep -q "Nested Stage"'
"$CLI" disable "suits menu" >/dev/null   # the fake game has no suits config

echo "install"
check "install succeeds" '"$CLI" install >/dev/null'
check "toc.BAK equals original toc" 'cmp -s "$G/toc.BAK" "$T/toc.orig"'
check "mod archive written to d/mods/mod0 (not a file named d\\mods\\mod0)" '[ -f "$G/d/mods/mod0" ] && ! ls "$G" | grep -q "\\\\"'
if [ -x "$T/rc/ReadCheck" ]; then
	cp "$LIB" "$T/rc/"
	check "assets read back correctly (GDeflate + LZ4 + modded)" \
		'"$T/rc/ReadCheck" "$G" "$T/mods/expected.json" 0:1111222233334444=new_a=36 0:9999AAAABBBBCCCC=vanilla_b 1:5555666677778888=new_c 1:AAAABBBBCCCCDDDD=nested >/dev/null'
fi

echo "modular"
"$CLI" add "$T/mods/test.modular" >/dev/null
check "modular default options install" '"$CLI" install >/dev/null'
"$CLI" options "Test Modular" color=blue extra=on >/dev/null
check "modular picked options install" '"$CLI" install >/dev/null'
if [ -x "$T/rc/ReadCheck" ]; then
	check "modular: chosen options land in the toc" \
		'"$T/rc/ReadCheck" "$G" "$T/mods/expected.json" 1:0000000000000C01=blue 1:0000000000000A11=always 1:000000000000E0E0=extra >/dev/null'
fi

echo "uninstall"
check "uninstall succeeds" '"$CLI" uninstall >/dev/null'
check "toc restored byte-for-byte" 'cmp -s "$G/toc" "$T/toc.orig"'

echo "game update handling"
"$CLI" install >/dev/null
python3 -c "import sys; open(sys.argv[1],'ab').write(b'\0'*16)" "$G/toc"
cp "$G/toc" "$T/toc.updated"
check "install --update-backup adopts the updated toc" '"$CLI" install --update-backup >/dev/null && cmp -s "$G/toc.BAK" "$T/toc.updated"'

echo "script dependencies"
python3 - "$T/mods" <<'EOF'
import json, sys, zipfile
def script(name, info):
    with zipfile.ZipFile(f"{sys.argv[1]}/{name}.script", "w") as z:
        z.writestr("info.json", json.dumps(dict(info, game="MSM2", type="script", version="1.0")))
        z.writestr(f"{name}.dll", b"MZ")
script("needs_lib", {"name": "NeedsLib", "dependencies": ["CoreLib"]})
script("core_lib", {"name": "CoreLib"})  # no "dependencies" key at all
script("needs_missing", {"name": "NeedsMissing", "dependencies": ["NotInstalled:2.0"]})
EOF
"$CLI" add "$T/mods/needs_lib.script" "$T/mods/core_lib.script" "$T/mods/needs_missing.script" >/dev/null
touch "$HOME/.local/share/overstrike-cli/scripts_proxy.dll"
"$CLI" scripts on >/dev/null
"$CLI" install >"$T/out" 2>&1 || true
check "unmet dependency is named in the error summary" 'grep -q "NotInstalled:2.0.* -- missing!" "$T/out"'
check "script without a dependencies key still counts as present" '! grep -q "CoreLib.* -- missing!" "$T/out"'
check "hint fits a dependency error" 'grep -q "Add the missing script mods" "$T/out" && ! grep -q reset-toc "$T/out"'
"$CLI" disable NeedsMissing >/dev/null
check "install succeeds once the dependency problem is gone" '"$CLI" install >/dev/null'
"$CLI" scripts off >/dev/null

echo
echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
