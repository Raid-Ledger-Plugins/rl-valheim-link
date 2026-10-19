# AGENTS.md — RL Valheim Link

You are the engineer of a BepInEx mod for Valheim that pushes player data to Raid Ledger.
Story: ROK-1834 (Linear, operator's workspace). Sibling for conventions: `sjdodge123/ledger-link`.

## The one rule

**Raid Ledger owns the wire format.** `contract/v1/` is a verbatim copy of
`packages/contract/valheim-link/v1/` from `github.com/sjdodge123/Raid-Ledger` (commit in
`contract/PIN.md`). Never edit it. Never emit a field it does not define. If game data does
not fit, write what is needed under "Contract change requests" in `docs/design.md` and ask the
operator to file a Raid Ledger story. CI job `contract-drift` fails when the copy differs from
upstream main; the only fix is `tools/contract-check/refresh-pin.sh`.

## Layout

| Path | What |
|---|---|
| `src/RlValheimLink.Contract/` | netstandard2.0, no Unity: message model, JSON writer, **pre-send validator** (every schema rule + CONTRACT.md §6 refinements), bitset/gzip/base64 encoder, PNG encoder. Unit-tested. |
| `src/RlValheimLink.Core/` | netstandard2.0, no Unity: the server half's logic — `Enricher` (serverId, socket identity, playerId check, clock clamp, base-map attach, then validate), `Spool` (disk queue, latest-wins dedupe, blobs by sha256), `Uploader` (state machine: budgets, backoff, halt on 401/403) and `RaidLedgerClient` (HTTP). Tested end to end against the mock. |
| `src/RlValheimLink/` | net48 BepInEx plugin. `Client/` captures (character, map, items+atlas), `Net/LinkChannel.cs` chunked routed RPC, `Server/` the game glue of the server half (peer identity, hello/needItems, base-map render, ticking Core). |
| `tests/RlValheimLink.Contract.Tests/` | xunit (net9): golden fixtures pass, every `fixtures/invalid/*` is rejected with the `{path, code}` from `EXPECTED.json`, encoders round-trip, `fixtures/` reproduce byte for byte. |
| `fixtures/` | The mod's own serialised output for synthetic captures (no real ids, names or seeds). Regenerate: `RLVL_WRITE_FIXTURES=1 dotnet test tests/RlValheimLink.Contract.Tests`. |
| `tools/mock-ingest/` | Node stand-in for the ROK-1833 ingest endpoint (ajv, bearer token, serverId, gzip, caps → 413, rate limit → 429, blobs by sha256, §6 bitmap inflate). Acceptance target until a Raid Ledger env exists. |
| `tools/contract-check/` | ajv validation, drift check, pin refresh. |
| `tools/validate-cli/` | Runs the C# validator over JSON files. |
| `docs/design.md` | RPC shape, capture triggers, dedupe keys, upload state machine, size budget, config, **open questions**. Read it before touching the plugin. |
| `docs/operator-checklist.md` | Numbered in-game checks with exact log lines; the operator runs them. |
| `thunderstore.toml`, `thunderstore/` | tcli package config (version source of truth, namespace = the Thunderstore team owning `TCLI_AUTH_TOKEN`), icon and Thunderstore README. `release.yml` publishes to Thunderstore + tags + GitHub release on a push to main whose version is new. `tools/thunderstore/check.mjs` holds the version checks, `stage.sh` stages the DLLs. |

## Commands

```bash
dotnet test tests/RlValheimLink.Contract.Tests            # contract library tests
dotnet build src/RlValheimLink/RlValheimLink.csproj      # plugin (NuGet game libs; or VALHEIM_INSTALL=… for your install)
node tools/contract-check/validate.mjs fixtures          # ajv against schema.json
tools/contract-check/drift-check.sh                      # pinned contract vs Raid Ledger main
node tools/thunderstore/check.mjs consistent             # toml = props = Plugin.Version; also: changelog | published | deps | bumped <base.toml>
tools/thunderstore/stage.sh && tcli build                # the Thunderstore zip (dotnet tool install --global tcli --version 0.2.4)
dotnet run --project tools/validate-cli -- --inflate fixtures
cd tools/mock-ingest && npm test && npm start            # mock receiver
```

## Working rules

- Small commits, conventional prefixes: `feat(client):`, `feat(server):`, `feat(contract-lib):`, `chore(ci):`, `docs:`. One PR per slice; CI green before merge. A PR that changes shipped files (`src/`, `thunderstore.toml`, Thunderstore README/icon, CHANGELOG) must bump the version in all three places and add a CHANGELOG section; merging it publishes.
- Never commit secrets, game DLLs, real Steam ids, player or world names, seeds, real captures, or the operator's instance/env URLs (no `gamernight` hostnames outside the pinned contract, which is Raid Ledger's). Fixture ids are `765611980000000xx` / invented names.
- Log lines start with `RLVL:`; the token is masked by `Log` and must never reach a log, exception text, spool file or RPC. Pin labels are player free text: never log them.
- Every message goes through `Validator.Validate` before it is serialised for the wire; anything with issues is logged and **not sent**.
- Server-side trust: identity comes from the peer's socket, never from the client's claim (design §4).
- The game API this is compiled against is `Digitalroot.Valheim.Common.References` at the game's version (`GameRefsVersion` in the csproj, 1.0.20 = Valheim 1.0.20 on Unity 6000.0.75); members used are listed in `docs/design.md` §2. UNVERIFIED items there are confirmed by the operator checklist, not by assumption.
- Server-half logic lives in `RlValheimLink.Core` so it is unit-tested and driven against the mock (`UploaderMockTests` spawns `node tools/mock-ingest/server.mjs`); `Server/` in the plugin only touches game objects. Keep it that way.
