# mock-ingest

A local stand-in for the Raid Ledger valheim-link ingest endpoint, shaped like the real one
(ROK-1833, merged 2026-10-18) and built only from `contract/v1/`. Use it to develop and test the
mod's server half offline; a Raid Ledger fleet env with a per-server token is the final target.

```bash
cd tools/mock-ingest && npm install
RLVL_MOCK_TOKEN=dev-token RLVL_MOCK_SERVER_ID=12 npm start      # http://localhost:8787
npm test                                                        # node --test
```

| Env | Default | Meaning |
|---|---|---|
| `PORT` | 8787 | listen port |
| `RLVL_MOCK_TOKEN` / `RLVL_MOCK_SERVER_ID` | `mock-token` / `srv_mock_01` | the one per-server token and its server id (the real one is the Pelican server id as a decimal string) |
| `RLVL_MOCK_MESSAGES_PATH` | `/api/plugins/valheim/link/messages` | `POST <path>/{character\|map\|items}` |
| `RLVL_MOCK_BLOBS_PATH` | `/api/plugins/valheim/link/blobs` | `PUT <path>/{sha256}` |
| `RLVL_MOCK_RATE_MESSAGES` / `RLVL_MOCK_RATE_BLOBS` | 60 / 20 | per-server requests per minute before 429 (the real defaults) |
| `RLVL_MOCK_DATA` | (memory) | directory to persist accepted messages and blobs |

What it mirrors from the real endpoint (error bodies are `{ code, message }`, plus `issues` for `schema_invalid`): bearer token → 401 `invalid_token`; `serverId` ≠ token's
server → 403; body `kind` ≠ route → 400 `kind_mismatch`; `Content-Type` / encoding → 415
`unsupported_media`; `Content-Encoding: gzip` on messages only, inflated under the kind's §7 cap;
over the cap (declared or streamed) → 413 `payload_too_large`; `schema.json` via ajv → 400
`schema_invalid` with `issues: [{ path, code }]`; `capturedAt` > 5 min ahead → 400
`captured_at_in_future`; map bitmaps inflated per §6 → 400 `bitmap_invalid`; a ref that differs
from a stored blob → 400 `blob_ref_mismatch`; latest `capturedAt` wins per key, an older one is
202 `{ status: "stale" }`; blobs by sha256 (201 `stored` / 200 `exists`) with hash, IHDR and
length checked against any referencing message, identity encoding only; per-server buckets →
429 `rate_limited` + `Retry-After`; per-server storage quotas → 413 `quota_exceeded`.

Point the mod at it with `RaidLedger.BaseUrl = http://localhost:8787`,
`ServerToken = dev-token`, `ServerId = 12`. Inspect with `GET /state`.
