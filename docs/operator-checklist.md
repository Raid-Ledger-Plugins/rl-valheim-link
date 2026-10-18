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
   `RLVL: server half active: serverId='<id>' baseUrl='<url>' token=set (uploads: NOT BUILT YET — pending design §9)`
   If you see `token=MISSING` or the `must all be set` warning, fix the config and restart.
2. Start your client (modded profile) and join the test world. Client log:
   `RLVL: ZNet up: server=False client=True dedicated=False`
   `RLVL: client half active (sendCharacter=True sendMap=True iconSize=64)`
   then, once your character has spawned:
   `RLVL: queued hello: transfer #1 Hello <n> bytes in 1 chunk(s)` and `RLVL: sent hello: transfer #1 complete`.
   Server log: `RLVL: received transfer #1 Hello from peer <uid>: <n> bytes` and
   `RLVL: server stub: Hello payload of <n> bytes from peer <uid> received and DROPPED`.
   Paste back: the `ZNet up` line from both sides and the three hello lines.

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

7. Server log for step 4: `RLVL: received transfer #2 Character from peer <uid>: <bytes> bytes` and the
   `server stub: … DROPPED` line; same for `#3 Map`. Paste back both `received` lines and the peer uid.
8. (After the server slice lands) the `received` lines are followed by validation, dedupe and upload
   lines; this section will be extended then.

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
