#!/usr/bin/env bash
# Stage the Release DLLs where thunderstore.toml's [[build.copy]] expects them, then `tcli build`
# packs build/RaidLedger-ValheimLink-<version>.zip (manifest.json generated from the toml).
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
BIN="$REPO/src/RlValheimLink/bin/Release/net48"
for f in RlValheimLink.dll RlValheimLink.Contract.dll RlValheimLink.Core.dll; do
  [ -f "$BIN/$f" ] || { echo "missing $BIN/$f — run: dotnet build src/RlValheimLink/RlValheimLink.csproj -c Release" >&2; exit 1; }
done
rm -rf "$REPO/build/stage"
mkdir -p "$REPO/build/stage/plugins/RlValheimLink"
cp "$BIN"/RlValheimLink.dll "$BIN"/RlValheimLink.Contract.dll "$BIN"/RlValheimLink.Core.dll "$REPO/build/stage/plugins/RlValheimLink/"
echo "staged: $(ls "$REPO/build/stage/plugins/RlValheimLink" | tr '\n' ' ')"
