# Pinned contract

`contract/v1/` is a byte-for-byte copy of
`packages/contract/valheim-link/v1/` from the Raid Ledger repo
(https://github.com/sjdodge123/Raid-Ledger).

| | |
|---|---|
| Pinned from commit | `cef3748e826e38b21e103a4456405bef571e7821` (main, 2026-10-20) |
| Upstream URL | https://raw.githubusercontent.com/sjdodge123/Raid-Ledger/main/packages/contract/valheim-link/v1/ |
| Drift check | `tools/contract-check/drift-check.sh` (CI job `contract-drift`) |

Rules (CONTRACT.md §10): **this repo never edits anything under `contract/v1/`.**
When the drift check fails, the only fix is to re-copy upstream v1 verbatim
and bump the commit above. If the game data does not fit the format, write
down exactly what is needed in `docs/design.md` → "Contract change requests"
and ask the operator to file a Raid Ledger story.

Refresh:

```bash
tools/contract-check/refresh-pin.sh   # re-copies upstream main v1/ and rewrites this table
```
