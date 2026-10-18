# mock-ingest

A local stand-in for the Raid Ledger valheim-link ingest endpoint (ROK-1833), built only from
`contract/v1/`. It is the acceptance target for the mod's server half until a Raid Ledger
environment with ROK-1833 exists.

```bash
cd tools/mock-ingest && npm install
RLVL_MOCK_TOKEN=dev-token RLVL_MOCK_SERVER_ID=srv_dev npm start      # http://localhost:8787
npm test                                                             # node --test
```

| Env | Default | Meaning |
|---|---|---|
| `PORT` | 8787 | listen port |
| `RLVL_MOCK_TOKEN` / `RLVL_MOCK_SERVER_ID` | `mock-token` / `srv_mock_01` | the one per-server token and its server id |
| `RLVL_MOCK_MESSAGES_PATH` | `/api/valheim-link/messages` | POST route (ROK-1833 may change it) |
| `RLVL_MOCK_BLOBS_PATH` | `/api/valheim-link/blobs` | PUT route prefix; `/<sha256>` appended |
| `RLVL_MOCK_RATE` | 30 | requests per minute per server before 429 |
| `RLVL_MOCK_DATA` | (memory) | directory to persist accepted messages and blobs |

What it checks: bearer token → 401; `serverId` ≠ token's server → 403; `Content-Type` → 415;
`Content-Encoding: gzip` inflated with the largest cap as output limit; the kind's §7 cap on
the decoded body → 413; `schema.json` via ajv → 400; `capturedAt` more than 5 min ahead → 400;
map bitmaps inflated per §6 (bounded, exact length, one member) → 422; latest `capturedAt`
wins per key; blobs by sha256 with length and IHDR checked against any referencing message
→ 422; per-server rate limit → 429 with `Retry-After`.

Point the mod at it with `RaidLedger.BaseUrl = http://localhost:8787`,
`ServerToken = dev-token`, `ServerId = srv_dev`. Inspect with `GET /state`.
