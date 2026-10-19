# Operator checklist: in-game checks (Pelican test world)

Numbered, with the exact log lines to expect. Log lines are prefixed `RLVL:` in
`BepInEx/LogOutput.log` (client: `<game>/BepInEx/LogOutput.log`; server: the Pelican console or
the same file under the server install). Every secret is masked as `***` by the mod itself.

Before you start: install the zip from the release (or the `RlValheimLink-<sha>` CI artifact) into
`BepInEx/plugins/RlValheimLink/` on **both** the test server and your client. On the server, set in
`BepInEx/config/RaidLedger.ValheimLink.cfg`: `BaseUrl`, `ServerToken`, `ServerId`. Until a Raid
Ledger env with ROK-1833 exists, run `tools/mock-ingest` somewhere the server can reach and point
`BaseUrl` at it.

## A. Load

1. Start the dedicated server. Expect:
   `RLVL: loaded v0.1.0 (contract v1); waiting for ZNet to decide client/server role`
   `RLVL: ZNet up: server=True client=False dedicated=True`
   `RLVL: server half active: serverId='<id>' baseUrl='<url>' token=set world=<uid> game=<version> spool=<dir> (pending=0 blobsPending=0 uploadedKeys=0)`
   If you see `token=MISSING` or the `must all be set` warning, fix the config and restart.
   Within ~15 s: `RLVL: base map: rendering 2048² at 12 m/px, 4 rows per tick`, then
   `RLVL: base map: sampled in <ms> ms; encoding PNG`, `RLVL: base map: ready, <bytes> bytes, sha256=<12 hex>… in <ms> ms`,
   `RLVL: base map queued for upload (<bytes> bytes)` and, once the uploader runs,
   `RLVL: PUT blob <12 hex>… → 201 stored in <ms> ms`. Paste back the four base-map lines and how long the
   server tick stalled while rendering (watch the console; design §2 says this is UNVERIFIED).
2. Start your client (modded profile) and join the test world. Client log:
   `RLVL: ZNet up: server=False client=True dedicated=False`
   `RLVL: client half active (sendCharacter=True sendMap=True iconSize=64)`
   then, once your character has spawned:
   `RLVL: queued hello: transfer #1 Hello <n> bytes in 1 chunk(s)` and `RLVL: sent hello: transfer #1 complete`.
   Server log: `RLVL: received transfer #1 Hello from peer <uid>: <n> bytes` and
   `RLVL: peer <uid>: hello mod=0.1.0 game=<version> modSet=<12 hex>… steam=<17 digits> admin=<True|False>`.
   If `steam=none`, the server could not read a SteamID64 from your connection (crossplay/PlayFab?) and
   will reject your data — paste the line and stop here.
   If you are in `adminlist.txt` (`Steam_7656…` format in Valheim 1.0; the mod also accepts the bare id):
   `RLVL: peer <uid>: asking for the items export (game <version>, mod set …)` follows, and your client
   starts the export of section D on its own. `admin=False` with an empty adminlist is correct behaviour.
   Paste back: the `ZNet up` line from both sides and the hello lines.

## B. Character capture (client)

3. In the F5 console type `rlvl status`. Paste back the line it prints.
4. Type `rlvl export now`. Client log, in this order:
   `RLVL: character (equipment=<n> items=<n> grid=8x4 skills=<n> trophies=<n> knownItems=<n> knownRecipes=<n>) captured in <ms> ms; <bytes> bytes JSON, <bytes> bytes gzip`
   `RLVL: queued character: transfer #2 Character <bytes> bytes in <k> chunk(s)`
   `RLVL: sent character: transfer #2 complete`
   then the same three for `map (size=2048 pixel=12m explored=<n>ch shared=<none|nch> pins=<n>)`.
   Paste back all six lines. **If `grid=` is not `8x4` with EquipmentAndQuickSlots installed**, also paste
   the `RLVL: EquipmentAndQuickSlots detected: IsEquipmentSlot=<bool> IsQuickSlot=<bool>` line
   (it says whether the mod's reflection into EAQS found the slot helpers — UNVERIFIED in design §2).
5. Anything starting `RLVL: character … NOT sent, <n> contract issue(s):` is a bug in the mod: paste the
   whole line (it names the field; it contains no pin labels).
6. Wait for the server's save broadcast (`-saveinterval`, 30 min on the operator's server) or log out to
   the main menu. Expect the same capture lines again without typing anything (the Harmony hook on
   `Game.SavePlayerProfile`). On logout, the map should NOT be re-sent if nothing changed:
   `RLVL: map unchanged since last send` (debug level; enable `[Logging.Console] LogLevels = All` in
   `BepInEx.cfg` to see it).

## C. Server receipt

7. Server log for step 4, in this order:
   `RLVL: received transfer #2 Character from peer <uid>: <bytes> bytes`
   `RLVL: peer <uid>: character character|<steamId>|<playerId> capturedAt=<ts> <bytes> bytes → Accepted`
   `RLVL: POST character character|<steamId>|<playerId> → 202 accepted in <ms> ms`
   and the same three for `Map` (`→ 202 accepted`; the map line ends with `refs=1` once the base map exists).
   A repeat of `rlvl export now` within 30 s produces `… → DroppedOlder` or the client-side throttle
   (no capture) — both are correct. Paste back the six lines.
8. Problems to paste verbatim if they appear:
   `RLVL: peer <uid>: character rejected: playerId claim … != the peer's character …` (identity cross-check),
   `RLVL: peer <uid>: … rejected: contract: …` (a mod bug),
   `RLVL: POST … → 400 schema_invalid [path:code]: dropped` (contract drift — run the drift check),
   `RLVL: uploads HALTED: 401 …` (token rotated: paste the new one into the config, uploads resume within 10 min),
   `RLVL: uploads HALTED: 403 …` (ServerId wrong), `RLVL: 429 rate limited …; backing off <n> s`.
   `rlvl status` on the server console prints `RLVL server: uploader=<state> pending=… uploaded=… baseMap=<state>`.

## D. Items export (client, GPU)

9. Type `rlvl export items`. Expect within a few seconds:
   `RLVL: items export: <n> item(s) from ObjectDB, locale English, reason console`
   `RLVL: queued atlas 2048x<h> sha256=<12 hex>…: transfer #4 Blob <bytes> bytes in <k> chunk(s)`
   `RLVL: items (count=<n> atlas=<bytes> bytes) exported in <ms> ms; <bytes> bytes JSON, <bytes> bytes gzip`
   `RLVL: queued items: transfer #5 Items …` and the two `sent …` lines.
   Paste back: the item count, the atlas size line, and whether the game hitched noticeably (how long).
   A line `RLVL: atlas at 64 px is <bytes> bytes (> Upload.MaxBlobBytes <n>); retrying smaller` is fine; paste it too.

## E. Opt-out

10. Set `[Client] OptOut = true` in the client config, rejoin. Expect
    `RLVL: client half OFF: Client.OptOut = true (nothing is sent from this game)` and no `queued` lines
    at all, even after `rlvl export now` (it prints nothing but the status). Set it back to `false`.

## F. Paste back

- Client `BepInEx/LogOutput.log` lines starting `RLVL:` (they never contain pin labels or the token).
- Server console lines starting `RLVL:`.
- `rlvl status` output.
- The game version from the title screen and the EAQS version from r2modman.
