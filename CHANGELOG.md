# Changelog

## 0.1.5 — 2026-10-19

- The server notices when its upload target changed (Raid Ledger instance rebuilt or moved, server id or
  token changed) and forgets its "already uploaded" markers, so the base map and the item catalogue are
  re-sent without any manual step; dedupe watermarks and pending messages are kept.
- Operator instructions: server setup, item catalogue by admins, token rotation and rebuilt-instance
  behaviour on the Thunderstore page; `docs/operations.md` runbook.

## 0.1.4 — 2026-10-19

- Fix: captures made on logout or quit were lost — the game saves the profile and closes the connection in
  the same frame, so chunks queued for later frames never left the client. The client now flushes every
  queued transfer synchronously (and flushes the socket) when the save is a logout/quit save.
- Fix: the admin check for the items export accepts both `Steam_<id>` (Valheim 1.0 list format) and bare
  SteamID64 entries in `adminlist.txt`.

## 0.1.3 — 2026-10-19

- Fix: the client never sent anything on Valheim 1.0.20 — the runtime enforces member visibility, and the
  build (compiled against publicized references) called the private `ZRoutedRpc.GetServerPeerID()`
  directly (`MethodAccessException` every tick, seen in the operator's client log). Every non-public game
  member the mod reads now goes through Harmony `AccessTools` in `GameAccess.cs` (equipment slots,
  trophies / known items / recipes, skills, minimap bitsets and pins); routed-RPC ids come from public API.
- A startup self-check logs `game member access ok|BROKEN`, and CI scans the built DLL against an
  unpublicized reference assembly so a direct private access can never ship again.

## 0.1.2 — 2026-10-19

- First version published to Thunderstore as `GamerSaloon-ValheimLink`, from `main` by `release.yml`.

- Fix: a spooled blob with no message referencing it yet (the base map, rendered at world load) was never
  uploaded; blobs now go up on their own, oldest first, before messages. Found on the first real run.
- No operator URLs in the repository or the built DLL: the `BaseUrl` config description and the design
  doc no longer carry an example instance URL (operator ruling). The pinned contract keeps Raid
  Ledger's own `$id`, which is theirs to change.

## 0.1.1 — 2026-10-19

- Build against the game build the operator runs: Valheim **1.0.20** on Unity **6000.0.75** via the
  `Digitalroot.Valheim.Common.References` and `UnityEngine.Modules` packages (ValheimGameLibs stopped at 0.221.4).
- Console command registration no longer binds one `ConsoleCommand` constructor signature (1.0.20 added a
  parameter and the old binding threw `MissingMethodException` at every Terminal init); it picks the
  `ConsoleEvent` overload by reflection.
- Steam id read through reflection into the game's own Steamworks.NET; no Steamworks reference shipped.
- First real-endpoint findings: the server half renders the 2048² base map in ~21 s on the dedicated server
  and queues it; error bodies are `{ code, message }` (fixed in 0.1.0's PR 4).

## 0.1.0 — unreleased

- Contract library (`RlValheimLink.Contract`): message model, JSON writer, pre-send validator covering
  schema.json and every CONTRACT.md §6 refinement, bitset/gzip/base64 encoder, PNG encoder; tests
  against the pinned golden and invalid fixtures.
- Client half: character, map (explored + shared layer + pins) and items (ObjectDB + icon atlas)
  captures on `Game.SavePlayerProfile`, hello on spawn, per-player opt-out, `rlvl` console command.
- Chunked routed-RPC transport (`RLVL_Chunk` / `RLVL_Control`) with caps and per-peer rate limits.
- Server half: `RlValheimLink.Core` (enricher with socket identity + playerId cross-check + clock clamp,
  disk spool with latest-wins dedupe and content-addressed blobs, uploader state machine honouring
  Raid Ledger's codes, budgets, 429 Retry-After, backoff and 401/403 halts, HTTP client with gzip
  bodies) plus the game glue (peer identity cache, hello → needItems, base-map render once per world
  and game version). Tested end to end against the mock receiver.
- Contract pin refreshed to Raid Ledger main after ROK-1833 (additive: optional `blob` on file icons);
  mock receiver matches the real routes, error codes, rate buckets and quotas.
- Thunderstore package name decided: `GamerSaloon-ValheimLink`.
- Tooling: mock ingest receiver, ajv + C# fixture validation, contract drift check, CI and release workflows,
  Thunderstore layout.
