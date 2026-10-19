# Changelog

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
- Thunderstore package name decided: `RaidLedger-ValheimLink`.
- Tooling: mock ingest receiver, ajv + C# fixture validation, contract drift check, CI and release workflows,
  Thunderstore layout.
