# Changelog

## 0.1.0 — unreleased

- Contract library (`RlValheimLink.Contract`): message model, JSON writer, pre-send validator covering
  schema.json and every CONTRACT.md §6 refinement, bitset/gzip/base64 encoder, PNG encoder; tests
  against the pinned golden and invalid fixtures.
- Client half: character, map (explored + shared layer + pins) and items (ObjectDB + icon atlas)
  captures on `Game.SavePlayerProfile`, hello on spawn, per-player opt-out, `rlvl` console command.
- Chunked routed-RPC transport (`RLVL_Chunk` / `RLVL_Control`) with caps and per-peer rate limits.
- Server half: logging stub only (pending operator answers to docs/design.md §9).
- Tooling: mock ingest receiver, ajv + C# fixture validation, contract drift check, CI and release workflows,
  Thunderstore layout.
