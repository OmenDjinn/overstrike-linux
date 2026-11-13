#!/usr/bin/env bash
# Fails if any ELF file under DIR needs a newer glibc than MAX (default 2.31).
# Usage: appimage/check-glibc.sh DIR [MAX]
set -euo pipefail
DIR="$1"
MAX="${2:-2.31}"
max_minor="${MAX#2.}"
bad=0
worst=0
while IFS= read -r -d '' f; do
	[ "$(head -c4 "$f" 2>/dev/null | tr -d '\0')" = $'\x7fELF' ] || continue
	need=$(objdump -T "$f" 2>/dev/null | grep -o 'GLIBC_2\.[0-9]*' | sed 's/GLIBC_2\.//' | sort -n | tail -1 || true)
	[ -n "$need" ] || continue
	[ "$need" -gt "$worst" ] && worst="$need"
	if [ "$need" -gt "$max_minor" ]; then
		echo "  needs GLIBC_2.$need: ${f#"$DIR"/}"
		bad=1
	fi
done < <(find "$DIR" -type f -print0)
if [ "$bad" = 1 ]; then
	echo "ERROR: files above need a newer glibc than $MAX. Build on an older distro (see appimage/build-in-container.sh)."
	exit 1
fi
echo "glibc check OK: highest requirement is 2.$worst (limit $MAX)"
