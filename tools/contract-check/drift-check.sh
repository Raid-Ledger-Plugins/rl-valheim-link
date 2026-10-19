#!/usr/bin/env bash
# Fail loudly when contract/v1/ differs from Raid Ledger main. CONTRACT.md §10: this repo
# never edits the pinned copy; the only fix is refresh-pin.sh.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
BASE="${RL_CONTRACT_BASE_URL:-https://raw.githubusercontent.com/sjdodge123/Raid-Ledger/main/packages/contract/valheim-link/v1}"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

# Every file in the pinned copy must match upstream byte for byte.
status=0
while IFS= read -r -d '' f; do
  rel="${f#"$REPO/contract/v1/"}"
  mkdir -p "$TMP/$(dirname "$rel")"
  if ! curl -fsSL --retry 3 -o "$TMP/$rel" "$BASE/$rel"; then
    echo "DRIFT  $rel: not found upstream (removed or renamed?)"; status=1; continue
  fi
  if ! cmp -s "$f" "$TMP/$rel"; then
    echo "DRIFT  $rel differs from upstream"; diff -u "$f" "$TMP/$rel" | head -40 || true; status=1
  else
    echo "same   $rel"
  fi
done < <(find "$REPO/contract/v1" -type f -print0 | sort -z)

# Files added upstream that the pin lacks: the fixture list is the one thing we can enumerate
# (raw.githubusercontent has no directory listing), via the EXPECTED.json names.
for name in $(node -e 'const e=require(process.argv[1]);console.log(Object.keys(e).join(" "))' "$TMP/fixtures/invalid/EXPECTED.json" 2>/dev/null || true); do
  [ -f "$REPO/contract/v1/fixtures/invalid/$name.json" ] || { echo "DRIFT  fixtures/invalid/$name.json exists upstream but not in the pin"; status=1; }
done

if [ $status -ne 0 ]; then
  echo
  echo "contract/v1 has DRIFTED from $BASE"
  echo "Fix: tools/contract-check/refresh-pin.sh, then re-run the fixture tests; never hand-edit contract/v1."
  exit 1
fi
echo "contract/v1 matches upstream"
