# Operations runbook (operator)

The server half keeps its state under `<game>/BepInEx/RLValheimLink/spool/` (`index.json`, `pending/`,
`blobs/`, `basemap/`). Everything below is driven from `BepInEx/config/RaidLedger.ValheimLink.cfg`
and the `RLVL:` lines in `BepInEx/LogOutput.log`.

| Situation | What happens | What you do |
|---|---|---|
| First install | Server renders the base map (~20 s of ticks), spools it, uploads it once `BaseUrl`, `ServerId`, `ServerToken` are set | Set the three keys, restart |
| Token rotated in Raid Ledger | Next request gets 401 → `uploads HALTED: 401 …`, retried every 10 min; the token is read from the config on every request | Paste the new token, save; no restart |
| Wrong `ServerId` | 403 → halted the same way | Fix the id (shown next to the token in the admin dialog) |
| Raid Ledger instance rebuilt / URL or server id changed | The server detects the new target (URL + server id + token hash) at start and forgets its "already uploaded" markers: base map re-queued, items export re-requested; player snapshots re-send on the next save | Paste the new token if there is one, restart once |
| Item catalogue missing on the site | It is produced by an admin client once per game version / mod set | Make sure an admin (`adminlist.txt`, `Steam_7656…`) joins; or set `[Server] ItemsExportBy = anyone` |
| Link idle on a test world | Start logs `world 'x' not in Server.WorldAllowlist; link idle`: no base map, each joining client's hello is answered `reject hello: world 'x' is not linked …` (its log: `server declined this client; nothing is sent this session`), nothing is spooled or uploaded | Intended when the server was booted into a test world. If `x` IS the production world, fix the spelling in `[Server] WorldAllowlist` (case does not matter) or clear it to link every world; restart |
| Player sends nothing | `[Client] OptOut = true`, mod not in their profile, or an older RPC protocol than the server (`reject protocol …` in their log; mod versions may differ) | Check their profile has the mod; update the pack if the protocol changed |
| Rate limited (429) / Raid Ledger down (5xx) | Backoff with `Retry-After` / exponential up to 10 min, nothing lost (spooled) | Nothing |
| Bad request (400 schema_invalid …) | The message is dropped and logged with the failing path | Report it: contract drift or a mod bug |
| Storage quota full (413 quota_exceeded) | Waits 10 min and retries | Raise the quota or prune on the Raid Ledger side |

Logs never contain the token or pin labels. Deleting `spool/` is always safe: the next captures
rebuild it (the base map re-renders).
