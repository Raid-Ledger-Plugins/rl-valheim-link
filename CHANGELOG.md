# Changelog

## 0.2.1 — 2026-10-20

- **`[Server] WorldAllowlist`** (comma-separated world names, case-insensitive; empty = every world).
  When the loaded world is not listed the server half stays idle: no base-map render, every client's
  hello is answered with `reject hello: world 'x' is not linked …` so a 0.2.1 client sends nothing that
  session (an older client keeps sending and the server drops it), and nothing is spooled or uploaded.
  For one dedicated server that is sometimes booted into a test world: the character key has no world
  in it, so a test-world session would otherwise become the character's latest snapshot in Raid Ledger.
- Client: a `reject` whose reason starts with `hello: ` now stops the session's captures (older
  servers never send one).
- **`[Chests] DumpOnStart`** (server only, default `false`): a diagnostic for the planned chest-inventory
  capture; nothing changes unless an operator turns it on. Two minutes after the world loads, the server
  reads every container from the world's ZDOs with the game's own `Inventory.Load` and logs
  `RLVL: chestdump[r<run>.c<check>]` lines: the keys on a few known chests (`items` / `itemData` hashes),
  which data shape and which `Inventory.Load` variant decode a 1.0.20 chest (and whether the temporary
  inventory avoids instantiating item objects), milliseconds per stack, row counts, counts per prefab
  (duplicates, creator 0, gone, dropped), totals of `DumpMaterials`, `creator` / `creatorIndex` on personal
  chests, every container and processing-station prefab with its values, and the 1.0.20-only recipe and
  station fields. `DumpRuns` (default 2) and `DumpRerunMinutes` (default 10) repeat it and report what
  changed (freshness). Logs only: no upload, no new message kind, no ZDO string contents in the log.
  Because it uploads nothing it also runs on a world that is not in `WorldAllowlist` (a test world is
  where it is meant to run) and without a Raid Ledger target.
- Members that are new in 1.0.20 are read by name through `GameAccess`, so a wrong guess shows up as a
  dump line, never as a failure of the captures.

## 0.2.0 — 2026-10-19

Bundled release after the first full in-game runs (operator ruling: fewer, better-tested iterations).

- **Compatibility by protocol, not version.** The server accepts any client that speaks the same RPC
  protocol and logs the client's mod version; a server-side update no longer forces a modpack update.
  `Server.RequireExactModVersion = true` restores the strict check.
- **Equipment & Quick Slots rows named through its official API** (`API.IsSlotCell`), so the extra
  inventory rows arrive as `equipment` / `quickslot` instead of `other`.
- **Progress lines** at 25/50/75% for large transfers (the icon atlas is ~6 MB and Steam sends it at
  ~150 KB/s, about 40 s).
- Includes 0.1.6: the logout flush sends only the small snapshots and leaves large transfers to a live
  session.

## 0.1.6 — 2026-10-19

- Fix: logging out while an item-catalogue export was queued lost everything, including the character and
  map — the multi-megabyte atlas sat ahead of them and the connection closed before any of it drained. At
  logout/quit the client now sends only the small snapshots (character first, then map) and drops large
  transfers; the server asks for the catalogue again on the next join. Seen on the operator's second
  admin join.

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
