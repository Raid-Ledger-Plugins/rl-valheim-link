#!/usr/bin/env bash
# Re-copy packages/contract/valheim-link/v1 from a Raid Ledger checkout (RAID_LEDGER_DIR) at
# its origin/main, verbatim, and rewrite contract/PIN.md's commit line.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
RL="${RAID_LEDGER_DIR:-$HOME/Documents/Projects/Raid-Ledger}"
[ -d "$RL/packages/contract/valheim-link/v1" ] || { echo "no contract at $RL (set RAID_LEDGER_DIR)" >&2; exit 2; }
git -C "$RL" fetch origin main --quiet
COMMIT="$(git -C "$RL" rev-parse origin/main)"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
git -C "$RL" archive "$COMMIT" packages/contract/valheim-link/v1 | tar -x -C "$TMP"
rm -rf "$REPO/contract/v1"
cp -R "$TMP/packages/contract/valheim-link/v1" "$REPO/contract/v1"
sed -i.bak -E "s/^\| Pinned from commit \| \`[0-9a-f]+\` \(main, [0-9-]+\) \|/| Pinned from commit | \`$COMMIT\` (main, $(date -u +%Y-%m-%d)) |/" "$REPO/contract/PIN.md"
rm -f "$REPO/contract/PIN.md.bak"
echo "pinned $COMMIT"
