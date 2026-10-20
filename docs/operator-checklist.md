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
   `RLVL: peer <uid>: hello mod=<client version> protocol=1 game=<version> modSet=<12 hex>… steam=<17 digits> admin=<True|False>`.
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

## G. Chest dump spike (server only; `docs/design-chests.md` §7)

Answers the UNVERIFIED items of the chest-inventory design before any capture code is written. It
**logs only**: no upload, no new message kind, nothing changes for clients. Every line starts
`RLVL: chestdump[r<run>.c<check>]` (`r1` = first run, `c1`…`c7` = the seven checks of §7). It never
prints the token, pin labels or the contents of any ZDO string, only lengths, item prefab names and
counts, positions, player ids and Steam ids (the same ids the `hello` lines already log).

11. **Build the test chests (once, in the test world, as an admin with your normal client).** Place and
    fill, and write down what you put where:
    - chest **A**: a wood chest with exact amounts of the three dump materials (default `Wood`, `Stone`,
      `Resin`), e.g. 50 Wood, 30 Stone, 20 Resin;
    - chest **B**: a reinforced chest (or another wood chest) with a few other items, so there is a second
      prefab;
    - your **personal chest** with something in it;
    - chest **F**: any chest at least 300 m away from where you log in, with a few of the dump materials
      (residency check: nobody will have loaded that area when the first dump runs);
    - optional: a cart with items in it (vehicles are counted separately).
    Then wait for a world save (the server logs one every `-saveinterval`, or type `save` in the F5
    console as an admin) and log out.
12. **Install the build on the server only.** From this PR's checks, open the `build` job's run,
    download the artifact `GamerSaloon-ValheimLink-<sha>`, unzip it (it contains the Thunderstore zip;
    unzip that too) and copy the three DLLs from `plugins/RlValheimLink/` over the ones in the server's
    `BepInEx/plugins/RlValheimLink/`. Clients stay on the pack's version (same RPC protocol).
    In the server's `BepInEx/config/RaidLedger.ValheimLink.cfg` set, under `[Chests]` (add the section by
    hand if the file was written by an older version):
    `DumpOnStart = true` and, if you used other materials in step 11, `DumpMaterials = <A>,<B>,<C>`
    (item prefab names, e.g. `Wood,Stone,Resin`). Leave `DumpRuns = 2` and `DumpRerunMinutes = 10`.
13. **Silence Uptime Kuma, then restart the server.** Never restart without it: pause the server's Kuma
    monitor first (a Kuma maintenance window, or a power action with monitors suppressed), or Discord gets
    DOWN/UP posts. **Do not join for the first two minutes**, so the first dump reads every chest before
    any player has loaded its area. Expect:
    `RLVL: loaded v0.2.2 (contract v1); …`
    `RLVL: chestdump: ON (Chests.DumpOnStart) — 2 run(s), the first in 120 s, then 10 min apart; logs only, uploads nothing; materials Wood,Stone,Resin`
    two minutes later `RLVL: chestdump[r1] start (run 1 of 2): game=1.0.20 dedicated=True … playersConnected=0 …`,
    then the check lines below, and finally
    `RLVL: chestdump[r1] done in <s> s over <n> ticks` and
    `RLVL: chestdump: run 2 of 2 in 10 min — move some items in a chest you know before then (freshness check)`.
14. **Freshness (check 6).** After `chestdump[r1] done`, join, and within the 10 minutes: take exactly 10
    Wood out of chest A and put them into chest B; open your personal chest; stay next to chest A until
    `RLVL: chestdump[r2] done …` appears, then you may log out. Expect
    `RLVL: chestdump[r2.c6] since the previous run (<m> min ago): changed=2 unchanged=<n> added=0 removed=0 …`,
    `RLVL: chestdump[r2.c6] changed piece_chest_wood @(<x>, <y>, <z>): Wood q1 50→40` (and the same for B),
    `RLVL: chestdump[r2.c6] materials since the previous run: Wood <a>→<a>, …` (moving between two shared
    chests leaves the total unchanged) and a key list of each changed chest (what a 1.0.20 client writes).
    Also note whether the game stuttered for you while run 2 was going (it logs its busiest tick).
15. **Afterwards:** set `DumpOnStart = false` (it only acts at start, so no restart is needed now; it would
    run again on the next start). Lift the Kuma silencing as you normally would once you are done with
    the server.

What each check prints (paste back all of them; step 16):

- **c1 storage:** `RLVL: chestdump[r1.c1] hashes: "items"=<h> ZDOVars.s_items=<h> (same); "itemData"=<h> ZDOVars.s_itemData=<h|missing> (…); "creator"=…; "creatorIndex"=…`,
  `RLVL: chestdump[r1.c1] probes: <n> (<prefabs>)…`, and per probe
  `RLVL: chestdump[r1.c1.p<n>] keys of <prefab> @(<x>, <y>, <z>) zdoid=… owner=… dataRevision=… persistent=… via ZDOExtraData.GetData (<visibility>): floats=… ints=… longs=… strings=… byteArrays=…`
  followed by one `string` / `bytes` / `int` / `long` line per key (`[= "items"]` / `[= "itemData"]` mark the two candidates).
- **c2 decode:** `RLVL: chestdump[r1.c2] visibility <Type>: <member>=<public|private|missing> …` (the real
  1.0.20 visibility of every member the design relies on); per probe
  `RLVL: chestdump[r1.c2.p<n>] <prefab> source=<string:items|bytes:items|bytes:itemData|string:itemData> <n> bytes; header int32 version=<v> (<name>) int32 count=<c> …`,
  six `loader <variant>: stacks=<n> in <ms> ms (<ms> ms/stack …) itemDrop.Awake=<n> itemDropObjects+=<n> inventory=<w>x<h> maxRow=<n> read=<pos>/<size> bytes …` lines
  (`itemDrop.Awake=0` on an `Inventory(true)` variant = the temporary inventory does not instantiate item
  objects), up to eight `item <prefab> x<stack> q<quality> …` lines, a `rows:` line, and once
  `RLVL: chestdump[r1.c2] decision: counting with <variant> (…)`.
- **c3 counts:** `RLVL: chestdump[r1.c3] discovery: <n> container prefabs, … duplicates removed, <ms> ms over <n> ticks (busiest tick <ms> ms …); zdosResident=<n> playersConnected=0`,
  one line per prefab found (`found= unique= duplicates= gone= creator0= counted= empty= unreadable= stacks= dropped= headerMismatch= maxRow= rowsBeyondPrefab= …`),
  `none in this world: …`, `totals with <variant>: … <ms> ms/stack`, `data sources: …`,
  `material <name>: <n> in <k> containers (shared <n>, personal <n>)` per material and `top items: …`.
  **Compare the material totals with what you placed in step 11** (chest F included: it was not loaded).
- **c4 creator:** `RLVL: chestdump[r1.c4] personal (privacy Private) containers with a creator: <n> …`, one line per personal chest
  `… creator=<playerId> creatorIndex=<int:… | absent> placer=<…> livePiece=<…>`, a key list of the first,
  and in run 2 `RLVL: chestdump[r2.c4] peer <uid>: playerId=<id> steam=<id> … personalChestsCreated=<n>` for you.
  `creator` should equal the `playerId` in your server line `RLVL: peer <uid>: character character|<steamId>|<playerId> …`.
- **c5 prefabs:** `RLVL: chestdump[r1.c5] Container <prefab>: size=<w>x<h> privacy=<…> buildable=<…> vehicle=<…> …` for every
  prefab with a Container, `Smelter|Fermenter|CookingStation|Incinerator|CraftingStation <prefab>: …` with
  their values and conversions, and two summary lines.
- **c6 freshness:** step 14.
- **c7 1.0.20 fields:** `RLVL: chestdump[r1.c7] Smelter.m_noSourceConversion (<visibility>): …`,
  `CookingStation <prefab>: m_canOvercookItems=… m_canGiveBonusYield=… m_useFueldWhileEmpty=…`,
  `CraftingStation <prefab>: m_upgrader=… m_canRepair=… m_hasCraftTab=…`,
  `Recipe.m_noCraftOnlyUpgrade (…): true on <n> of <m> recipes: …`,
  `Piece.Requirement.m_upgraderResource (…): true on <n> requirements: …`, `recipes per crafting station: …`.
- **Problems:** any `RLVL: chestdump[…] <what> failed: <exception …>` or `RLVL: chestdump[r<n>] aborted` line is
  exactly what the spike is for; the dump carries on with the next check. Paste them whole.

16. **Send back:** the output of
    `grep 'RLVL: chestdump' BepInEx/LogOutput.log > chestdump.txt` on the server (the whole file, a few
    hundred lines; attach it rather than pasting), your server line
    `RLVL: peer <uid>: character character|<steamId>|<playerId> …` from step 14, what you placed in step 11
    (chest, prefab if you know it, contents), what you moved in step 14 and roughly when, and whether
    anything in the chests looked different in game afterwards.
