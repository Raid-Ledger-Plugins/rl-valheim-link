# Chest inventory, base materials and crafting planner — scouting design

Status: **scouting draft, 2026-10-19; operator rulings applied and the design
independently reviewed the same day (§8). Nothing here is built.** No mod code
and no contract file changes accompany this document. It proposes two new
message kinds, which per `AGENTS.md` ("Raid Ledger owns the wire format") must
start as Raid Ledger contract stories. §4 and §5 are written so they can be
pasted into Linear; §6.1 sketches the other stories.

The operator's request, paraphrased: record every item in every chest on the
server; show a Raid Ledger dashboard of the base's materials (totals across all
chests); and add a search on Raid Ledger that works like a crafting table, so
players can look up what things cost and plan the next material spend against
what is in the chests.

Reader's contract: `contract/v1/CONTRACT.md` and `docs/design.md` remain the
rules for everything already shipped. Bare section numbers (§3.2) refer to
this document, "design §" to `docs/design.md`, and "CONTRACT §" to
`contract/v1/CONTRACT.md`.

## 0. Recommendation in one page

| Piece | Where it runs | Trigger | New kind | Snapshot key |
|---|---|---|---|---|
| Chest scan | **dedicated server**, no client needed | **every 10 minutes** on a server timer (operator ruling), always sent; an admin can force one | `containers` | (`serverId`, `worldUid`) |
| Recipe catalogue | **admin client**, same lease/marker flow as the items export | server sends `needRecipes` after `hello` when it has no recipes export for (`gameVersion`, mod-set hash) | `recipes` | (`serverId`, `gameVersion`) |
| Base materials dashboard | Raid Ledger web, Valheim hub tab | reads the current world's `containers` snapshot + the items catalogue | — | — |
| Crafting planner | Raid Ledger web, hub tab or route | reads `recipes` + shared totals + items catalogue | — | — |

Key decisions, each argued below:

1. **Read chests straight from the server's ZDOs** (§1.1). Every placed chest is
   a ZDO in `ZDOMan` whether or not anyone is near it. No client and no loaded
   zone are required.
2. **Decode with the game's own public `Inventory.Load`, not a hand parser**
   (§1.3). The inventory format moved from version 106 (0.221.x) to **110** in
   1.0.20, and 1.0.20 adds world-load migrations for container storage, so the
   chest's data may now be a byte array rather than a base64 string, and under
   a different key. **Settled by the spike on 2026-10-20 (§0.1):** 1.0.20
   stores it as bytes under `items`, and the game's temporary inventory
   decodes it on a dedicated server without spawning item objects.
3. **Only player-built containers count** (§1.4): the prefab is a buildable
   piece that carries a `Container` (derived at runtime, so modded chests are
   included) **and** the ZDO's `creator` is non-zero. Loot chests, tombstones,
   dungeon crates and world-generated furniture drop out; the obliterator is
   excluded by default.
4. **Key a container by prefab + position** (§1.5). ZDO ids are reassigned in
   load order on every world load, so they are not stable.
5. **Send per-container stacks aggregated by (prefab, quality); Raid Ledger
   computes totals** (§4). One source of truth, smaller body.
6. **Always send every 10 minutes**, not only on change (§1.7). The dashboard's
   "updated N min ago" then means the last scan, and a change in a hidden
   personal chest cannot be inferred from a moving timestamp.
7. **Recipes come from `ObjectDB.m_recipes`, the build tools' piece tables and
   the processing stations**, exported by an admin client alongside the items
   catalogue, so modded and ServerSync-overridden recipes appear as players see
   them (§2). Smelting, kiln, windmill, spinning wheel, refinery, fermenting,
   cooking and the obliterator are in the first story (operator ruling).
   Upgrade costs per quality come from the raw requirement fields and the
   game's formula; the export flags recipes a mod has changed (§2.3).
8. **Visibility (operator rulings, §8):** everything is visible to anyone who
   can see the server hub (totals, per-chest contents, positions on the map),
   except **personal chests**, which only their owner and admins see. Enforced
   by Raid Ledger when reading, never by precomputed totals (§3.2).
9. **Pure logic in `RlValheimLink.Core`**, game reads in
   `src/RlValheimLink/Server` and `Client` (§1.9), per `AGENTS.md`.
10. **On by default, nothing to configure** (operator ruling 6: no flags to
    set). Chest scanning, the recipes export and everything else in this design
    work with default settings. `Chests.Enabled` defaults to `true` and exists
    only to switch scanning off; every other setting is a tuning knob whose
    default is right. Uploads still need the Raid Ledger settings, so a server
    without them only spools.

Rough effort: mod ≈ 5.5–7 days including the spike; Raid Ledger ≈ 14.5–17
days across contract, ingest, importer, read routes, the visibility rule,
dashboard and planner (§6.2).

### 0.1 Spike results (test server, 2026-10-20, PR 16 build v0.2.2-rc.2)

Two dump runs, ten minutes apart, on the test world (19 800 ZDOs; one player
built two wood chests and moved items between the runs). These replace the
UNVERIFIED notes in §1 and §2 where they differ.

| Question | Answer |
|---|---|
| Where a 1.0.20 chest keeps its inventory | **Bytes under `items`** (`ZDO.GetByteArray(ZDOVars.s_items)`), no string, nothing under `itemData` |
| Package header | `int32 version = 110`, then the stack count as `ZPackage.WriteNumItems` writes it: **one byte below 128, else two bytes** (`(n >> 8) \| 0x80`, then `n & 0xFF`; read back by `ReadNumItems`). Taken from the 0.221.4 IL; 1.0.20 has the same signatures. Not an `int32`: the spike's "dropped" numbers came from reading it as one (fixed in 0.2.1) |
| Decoder | `new Inventory(true)` + `Load(pkg, true)` (both public) decodes every stack and instantiates **no** item objects; 0.04–0.10 ms per stack. The sized `Inventory(w, h)` variants also work but create one GameObject per stack |
| Dedicated server, no local player | Works; nothing threw |
| Creator | `creator` = the placing character's `playerId` (the same id on both sides); `creatorIndex` = 0. The personal-chest rule stands. No personal chest was built, so `privacy: private` itself was only seen on the prefab (`piece_chest_private`) |
| Residency | Every container ZDO was found with no player connected |
| Freshness | Changes made by a connected client (ZDO owned by the client, data revision 28) were read live ten minutes later without a world save |
| Duplicates from the iterator | 0, but this world needed only 2 calls per prefab; untested at scale |
| Discovery cost | 64 container prefabs, **67 ms over 2 ticks, busiest tick 24 ms**, on a small world. One tick runs all 64 prefab passes, so a production world would stutter: the scanner must budget discovery (§1.1). Measured on production below |
| Member visibility in 1.0.20 | Public: `GetAllZDOsWithPrefabIterative`, `ZDOMan.GetZDO`, `ZDO.GetByteArray`/`GetLong`/`GetPrefab`/`GetPosition`, `ZNetScene.GetPrefab`/`m_prefabs`, `Container.m_privacy`/`m_width`/`m_height`, `Inventory(bool)`, `Inventory.Load(ZPackage, bool)`, `GetAllItems`, `Piece.GetCreator`, `ZDOExtraData.GetData`. Private: `m_objectsBySector`, `m_objectsByID`, `ConvertContainers`, `Container.CheckAccess` |
| Container prefabs | 64 carry a `Container`; **18 are buildable** (chests, barrel, wardrobe, grausten chest, buildable pots and the like), 7 are vehicles (cart, sled, ships), 1 is private |
| Processing stations | Smelter ×9 (incl. `BatteringRam`, a siege tool, `piece_bathtub` with no conversions, and `piece_FrostKiln` with no input), CookingStation ×4 (incl. `piece_FrostFoundry`, fuel `FrozenFuel`), Fermenter ×1, Incinerator ×1; CraftingStation ×11 |
| 1.0.20 upgrades | `UpgradeStation` is a crafting station with `m_upgrader = true`; 271 requirements carry `m_upgraderResource` (items such as `Upgrader1Armor`), and 28 recipes (gold gear) are `m_noCraftOnlyUpgrade`. 481 recipes in total |
| Production world (0.2.1-rc.2, 2026-10-20) | 361 143 ZDOs, discovery one prefab pass per tick: 832 containers in **19.2 s over 576 ticks, busiest tick 1.3 ms** (up to 9 iterator calls per prefab, 0 duplicates). Decode: 111 player-built containers (4 on ships), 530 stacks in 68 ms wall over 3 ticks, busiest tick 1.6 ms, 0.004 ms per stack; header count and decoded count agree everywhere (`dropped=0 headerMismatch=0`). The dump's two single-frame diagnostic steps (prefab survey 70 ms, decoder probes 107 ms) are not part of the scanner |

**Design changes that follow** (applied below):

- Decode with `new Inventory(true)` + `Load(pkg, true)` from `GetByteArray(s_items)`;
  the base64-string path stays as a fallback only. Dropped stacks =
  header count (`ReadNumItems`) − decoded count.
- Discovery is budgeted: one prefab pass per tick, or one pass over all
  sectors through a `GameAccess` accessor for `m_objectsBySector` with a
  sector budget per tick. Measured on production with 0.2.1: one prefab
  pass per tick keeps the busiest tick at 1.3 ms, so that is the default and
  the `m_objectsBySector` route is not needed.
- The recipes export skips conversions with a null input or output
  (`BatteringRam`, `piece_FrostKiln`, `piece_bathtub`) and carries two
  additive fields: `stations[].upgrader` and `resources[].upgraderResource`.

## 1. Game side: enumerating containers on the dedicated server

All member names below were read from the reference assemblies the repo already
uses (`docs/reference-assemblies.md`): the **1.0.20 publicized reference**
(`Digitalroot.Valheim.Common.References` 1.0.20, signatures only, every method
body is `throw null`, every member looks public) and the **unpublicized
CoffeeNova copy** (`CoffeeNova.Valheim.ManagedReferences` 1.2214.6, which
reports `Version.CurrentVersion = 0.221.4` and has real IL and real
visibility). Decompiled with `ilspycmd` into a scratch directory; nothing
decompiled is committed. Behaviour known only from 0.221.4 IL is marked
**(0.221.4 IL)**; anything about 1.0.20 that the stubs cannot show is marked
**UNVERIFIED** and is checked by the spike (§7), not assumed.

### 1.1 Where chests live

- `ZDOMan.instance` holds the world's ZDOs on the server. In 0.221.4,
  `ZDOMan.Load` reads every ZDO into `m_objectsByID` and the per-sector lists
  `m_objectsBySector` (both private) at world load **(0.221.4 IL)**. 1.0.20
  adds chunked saving and loading (`GetSaveClonePerChunk`, `SaveChunks`,
  `Version.World.ChunkedSave` = 40; the 1.0.20 world version is
  `CheatCleanSlate` = 42). Every ZDO is still resident after load: the spike and
  0.2.1 found every container with no player connected (all 361 143 ZDOs
  resident on production, §0.1).
- The public way to find them is
  `ZDOMan.GetAllZDOsWithPrefabIterative(string prefab, List<ZDO> zdos, ref int index)`
  (public in 0.221.4, present in 1.0.20). It returns `true` once the whole
  world has been visited, so it is time-sliced by design: call it once per
  `Update` tick per prefab until it returns `true`.
- **It returns duplicates (0.221.4 IL).** Each call stops after 400 non-empty
  sectors with a `break` that skips `index++`, so the next call re-scans the
  last sector and adds its matches again. The scanner keeps a
  `HashSet<ZDOID>` and counts each ZDO once.
- **ZDOs are pooled.** A chest destroyed during a scan has its ZDO reset and
  reused for a new object (`ZDOPool.Release` / `Get`, 0.221.4 IL). Discovery
  records `(ZDOID, prefab hash)`; decoding re-resolves each id with
  `ZDOMan.instance.GetZDO(id)` and skips it when it is gone or its prefab
  changed.
- Cost: one pass over all ZDOs per container prefab. The spike found 64
  container prefabs and measured 67 ms over 2 ticks with a 24 ms tick on a
  19 800-ZDO world (§0.1), so discovery is budgeted: at most one prefab pass
  per tick, or a single pass over `m_objectsBySector` (private; through a new
  `GameAccess` accessor, which CI's `tools/visibility-check` covers) with a
  sector budget per tick. On production (361 143 ZDOs) one prefab pass per
  tick took 19.2 s over 576 ticks with a 1.3 ms busiest tick (§0.1), so the
  per-prefab budget is the default.
- Per ZDO (public): `GetPrefab()` (stable hash of the prefab name),
  `GetPosition()` (`Vector3`, world metres), `GetString(int)`,
  `GetByteArray(int)`, `GetLong(int)`.

### 1.2 ZDO keys (`ZDOVars`; names exist in 0.221.4 and 1.0.20)

| `ZDOVars` field | String hashed | Type | Meaning for us |
|---|---|---|---|
| `s_items` | `"items"` | string (0.221.4) | base64 of the `ZPackage` written by `Inventory.Save`: `Container.Save` → `zPackage.GetBase64()` → `GetZDO().Set(ZDOVars.s_items, base64)` **(0.221.4 IL)**; empty or absent = empty chest. **1.0.20: bytes** (`GetByteArray(s_items)`), verified by the spike (§0.1) |
| `s_creator` | `"creator"` | long | the placing character's `playerId`, written by `Piece.SetCreator` on placement **(0.221.4 IL)**; **0 for anything not placed by a player** (world-gen locations, dungeons) |
| `s_addedDefaultItems` | `"addedDefaultItems"` | bool | set on **any** container after its first owner `Awake` (default items added, possibly none); not a loot signal and not used |
| `s_inUse` | `"InUse"` | int | 1 while someone has it open (vanilla). Not used: MultiUserChest changes ownership semantics (§1.6) |

**Creator on 1.0.20 (verified by the spike, §0.1).** 1.0.20 changes
`Piece.SetCreator(long)` to `SetCreator(long uid, PlatformUserID platformUserID)`
and adds `ZDOVars.s_creatorIndex` and `Piece.m_creatorPlatformUserIDIndex`.
`creator` still holds the placing character's `playerId` (the same id as in its
`character` message), so the personal-chest rule stands. Not yet seen: a chest
with `privacy: private` built in a world (only the prefab was inspected).

**1.0.20 storage (verified by the spike, §0.1).** The 1.0.20 signatures add
world-load migrations next to the existing ones
(`ZDOMan.ConvertContainers(List<ZDO>, Version.World)`,
`ZDOMan.ConvertInventories(List<ZDOID>, Version.World, int)`,
`ZDOMan.ConvertPrefabStrings(List<ZDO>)`, `GetConvertHash(int, string, int)`),
a new `ZDOVars.s_itemData` key and `Version.Item.Smaller`. On 1.0.20 a chest
keeps its inventory as a **byte array under `items`**; no chest had a string
or anything under `itemData` (test world, and 102 of 111 containers on the
production world; the other 9 had no data, i.e. empty). The scanner tries, in
order, `GetByteArray(s_items)`, then `GetString(s_items)` as base64 (a chest
not yet migrated from an older world), then `GetByteArray(s_itemData)`.

### 1.3 Inventory serialisation and decoding

0.221.4 (real IL), format version 106:

```
int  version            (106)
int  count
repeat count:
  string prefabName     ("" = missing prefab, skipped)
  int    stack
  float  durability
  Vector2i gridPos      (two ints)
  bool   equipped
  int    quality
  int    variant
  long   crafterID
  string crafterName
  int    customDataCount, then that many (string key, string value)
  int    worldLevel
  bool   pickedUp
```

1.0.20 (signatures only): `Version.Item` runs `… 106 Stable, 107 AbandonedDN,
108 Smaller, 109 ChunksNCheats, 110 NewCheats` with
`Version.c_ItemDataVersion = NewCheats`. `ItemDrop.ItemData` gained
`m_cheated`. `Inventory` gained `AddItem(int prefabHash, …, bool cheated, …)`,
`Load(ZPackage, bool)`, `LoadOld(ZPackage, Version.Item)`, `OldSave`,
`SetHeight(int)`, and a temporary-inventory API (`Inventory(bool)`,
`m_temoraryInventory`, `AddTempItem(int prefabHash, …)`). So 1.0.20 very
likely stores a prefab **hash** per item and a cheat flag; we cannot read those
layouts from a stub.

**Decision:** do not parse the package ourselves. Sketch:

```
// verified on 1.0.20 by the spike (§0.1): bytes under "items", temporary inventory, no item objects
var inv = new Inventory(true);                       // public; temporary inventory
var pkg = zdo.GetByteArray(ZDOVars.s_items) is { } a ? new ZPackage(a)                           // 1.0.20
        : zdo.GetString(ZDOVars.s_items) is var b64 && b64.Length > 0 ? new ZPackage(b64) : null; // older worlds
if (pkg != null) inv.Load(pkg, true);                // public
foreach (var it in inv.GetAllItems()) …              // public; m_dropPrefab.name, m_stack, m_quality
```

`w`/`h` come from the prefab: `ZNetScene.instance.GetPrefab(hash)` →
`GetComponentInChildren<Container>(true)` → `m_width`, `m_height` (public).
`ZPackage(string)` and `ZPackage(byte[])` are public in both versions.
What `Inventory.Load` does **(0.221.4 IL)**, and what the design does about it:

- It **instantiates and destroys a GameObject per stack** (`AddItem` →
  `Object.Instantiate(itemPrefab)` … `Object.Destroy`), so decoding is budgeted
  by stacks, not chests (§1.7), and the spike measures milliseconds per stack.
  If 1.0.20's temporary inventory (`new Inventory(true)` with `Load(pkg, true)`)
  decodes without instantiating, the scanner uses it instead.
- It **clamps each stack** to the server's `m_maxStackSize`, so a stack-size
  mod must also be installed on the server (ServerSync-style mods are).
- It **drops**, silently, a stack whose grid position is outside the
  inventory's size (size is fixed at construction; no setter in 0.221.4) or
  collides with another item. Hence scratch inventories with generous height;
  1.0.20's `Container.UpdateRows()` and `Inventory.SetHeight(int)` suggest a
  chest's live row count can exceed the prefab's.
- It resolves items through `ObjectDB.instance.GetItemPrefab` and skips an
  unknown prefab (a removed mod) with its own log line. The 1.0.20 package
  starts with (int32 version, `ReadNumItems` count) (§0.1), so the scan
  reports `dropped = count − GetAllItems().Count` per scan.

**Verified for 1.0.20 by the spike and 0.2.1 (§0.1):** the data shape (bytes
under `items`), the decoder (`new Inventory(true)` + `Load(pkg, true)`, no item
objects), that it runs on a dedicated server without a local player, and that
the signatures above are public in the real assembly. **Still open:** whether a
chest's live row count can exceed the prefab's. No decoded chest has had a row
beyond its prefab (`rowsBeyondPrefab=0` on the test and production worlds), so
`slots` comes from the prefab until one does.

### 1.4 Which containers count (player-built only)

Two filters, both cheap:

1. **Prefab is a buildable container piece.** At server start, after
   `ObjectDB` and `ZNetScene` are ready, collect every piece prefab reachable
   from a tool: for each `ObjectDB.instance.m_items` entry whose
   `m_itemData.m_shared.m_buildPieces` (a `PieceTable`) is set, take
   `PieceTable.m_pieces`, keep those with a `Container` in children. In
   vanilla that should yield `piece_chest_wood`, `piece_chest` (reinforced),
   `piece_chest_blackmetal`, `piece_chest_private` (personal),
   `piece_chest_barrel`, the obliterator (`incinerator`, whose `Incinerator`
   holds a `Container`), and vehicles whose container sits on a child with
   `m_rootObjectOverride`: `Cart`, `Karve`, `VikingShip` and later ships.
   Prefab names are from game knowledge, not from the assemblies (they live in
   asset bundles), so the spike dumps the real list. Modded chests registered
   as build pieces (Jotunn) are picked up automatically. All fields read are
   public in real IL. Config: `Chests.ExcludePrefabs` (default `incinerator`:
   its contents are about to be destroyed or are uncollected output) and
   `Chests.ExtraPrefabs`, comma-separated.
2. **ZDO has a creator:** `zdo.GetLong(ZDOVars.s_creator) != 0`. World-generated
   pieces of a buildable prefab (a wood chest in an abandoned house) have
   creator 0 and are skipped.

Excluded by construction: `TreasureChest_*` and other loot chests, dungeon
crates, `Player_tombstone` (a `Container`, but not a buildable piece),
`CargoCrate` and loot bags. Vehicles are included but flagged, and
`Chests.IncludeVehicles` (default `true`) turns them off.

Privacy per container is read from the prefab, not the ZDO: `Container.m_privacy`
(public field: `Public`, `Private`, `Group`), reached through
`ZNetScene.instance.GetPrefab(zdo.GetPrefab())`. Vanilla `piece_chest_private`
is `Private`: only its creator may open it (`Container.CheckAccess`, private,
compares `Piece.GetCreator()` with the opener's player id, **0.221.4 IL**;
1.0.20 `creator` still holds the placing `playerId`, §1.2). `Group` returns `false` there in
0.221.4 (no vanilla group chests); the message passes it through and Raid
Ledger treats it as shared. Chests that check a ward (`m_checkGuardStone`) are
unaffected: wards gate opening, not what the server can read.

### 1.5 Identity and grouping

- **ZDO ids are not stable.** `ZDO.Load` assigns ids sequentially in load order
  (`m_uid.SetID(++ZDOID.m_loadID)`, **0.221.4 IL**), so a chest's `ZDOID` can
  change whenever anything before it in the save changes. The message does not
  carry it.
- A container is identified by `prefab`, position and creator. The mod rounds
  `x`/`y`/`z` to 0.1 m; two containers with the same prefab, rounded position
  **and creator** (overlapping pieces of one builder) are merged by the mod
  (items and slots summed, then the §1.8 clamps applied). Containers of
  different creators are never merged, so one player's personal chest can
  never absorb another's. The contract makes (`prefab`, `x`, `y`, `z`,
  `creatorPlayerId`) unique within a message (§4). Chests do not move;
  vehicles do, so a vehicle's identity only holds for one snapshot, which is
  fine for latest-wins.
- Positions use the same frame as map pins (`x` east-west, `z` north-south,
  world metres, |v| ≤ 32 768) plus `y` (height), so the dashboard can place
  chests on the existing base map and group them into bases (§3.3). Wards
  (`guard_stone`) are not exported (open question, §8).

### 1.6 MultiUserChest 0.6.2 (on the operator's server)

Read from its source (`github.com/MSchmoecker/No-Chest-Block`, master as of
2026-09-12, the repository the Thunderstore page links). It patches
`Container.Awake`, `RPC_RequestOpen` and `RPC_RequestStack` to hand **ZDO
ownership** to the player who opens the chest (`ZDOMan.ForceSendZDO`,
`ZDO.SetOwner`), and routes item moves through its own request/response RPCs.
It does not patch `Container.Save`/`Load`, does not write any ZDO key, and does
not change the `Inventory` format. Consequence for us: the chest's data is
written by whichever client owns the chest and reaches the server through
normal ZDO sync (§1.7), so the server's copy can lag by a second or so while a
chest is in use. Irrelevant at a 10-minute cadence. `s_inUse` is not a reliable
signal under this mod, which is why we never read it.

### 1.7 When to scan (every 10 minutes, operator ruling)

- **Trigger:** a timer in the server half's existing `Tick` (`ServerHalf.Tick`,
  driven from `Plugin.Update`). The first scan runs 2 minutes after the server
  half starts, so `ObjectDB`, `ZNetScene` and the world's ZDOs are loaded;
  then on a fixed grid every `Chests.IntervalMinutes` (default **10**, range
  5–120) measured from that first start, not from each scan's end. A new scan
  never starts while one is running (a grid slot that finds one running is
  skipped). `capturedAt` is the scheduled start of the scan, so the gaps
  between snapshots do not vary with how long decoding took.
- **Always sent.** Every completed scan produces a message, even when nothing
  changed. Cost: at the default, six messages an hour of 20–80 KB gzip each
  (§1.10), negligible against the self-imposed 30 messages/min (design §6, §8).
  In exchange the snapshot's `capturedAt` is the last scan time, so the
  dashboard's age is honest, and an idle server looks different from a broken
  one. A change check could come back later together with a heartbeat if Raid
  Ledger wants fewer imports.
- **Freshness (0.221.4 IL; 1.0.20 believed the same, spike checks):** a client
  that owns a chest writes its data locally (`Container.Save` → `ZDO.Set` →
  `IncreaseDataRevision` → `ZDOMan.ClientChanged`); `ZDOMan.SendZDOs` ships the
  changed ZDO to the server within a send round (0.05 s cadence, paused only
  while the socket queue is congested), and the server's `ZDOMan.RPC_ZDOData`
  deserialises it into the live ZDO when its data revision is newer.
  `ZNet.SaveWorld` only clones persistent ZDOs for the save thread
  (`ZDOMan.PrepareSave` → `GetSaveClone`) and writes nothing back. So no world
  save is needed before a scan, and the dashboard is at most about 10 minutes
  behind the game: one interval, plus a few seconds of upload, plus the Raid
  Ledger importer's 30-second cron.
- **Manual scan:** `rlvl export chests` typed in an **admin's client** F5
  console. A dedicated server has no F5 console and the existing `rlvl`
  commands are client-local, so this needs a new client → server request: a
  new `PayloadKind.Command` carrying `{ "op": "scanChests" }`. The server
  checks `PeerFacts.IsAdmin`, allows one forced scan per minute, and starts a
  scan. Older servers drop an unknown kind (`LinkChannel.MaxPayloadBytes`
  returns 0), so no RPC protocol bump is needed.
- **Spike trigger:** the spike build (PR 16) runs the dump two minutes after
  every start, on by default (`[Chests] Dump`); nothing to configure, no RPC.
- **Budget:** per tick, at most **one** `GetAllZDOsWithPrefabIterative` call in
  total while discovering (one prefab at a time; on production the busiest tick
  was 1.3 ms, §0.1), then decoding up to `Chests.StacksPerTick` (default 200)
  stacks. Decoding measured 0.004 ms per stack on production, so 200 stacks
  stay well under a millisecond.
- **Failures:** each chest decodes inside its own `try`; a chest that throws
  is still sent, with `unreadable: true` and no items (§1.8), so it stays
  under its own privacy. The "scan running" flag is cleared in a `finally`, so
  one bad chest can never stop later scans. Decode failures are counted in the
  per-scan INFO line; after three consecutive scans with failures, one WARN
  per hour.
- **Restarts:** nothing special. A message already spooled survives a restart
  (design §5); the first scan after a restart picks up the rest.
- **Upload:** the server builds the message itself (§1.9), runs it through
  `Validator`, puts it in the `Spool` under `containers|{worldUid}` (latest
  wins), and the existing `Uploader` sends it.

### 1.8 Over-cap policy and partial data

The contract's caps (§4) bind; the mod never sends an invalid message. Every
signal about a partly read container travels **on that container**, so Raid
Ledger shows it only to viewers allowed to see that container (§3.2):

- **Slots:** `slots` = max(prefab width × height, width × (highest decoded
  row + 1)), because 1.0.20 rows may grow at runtime (§1.3); then clamped to
  4 096, with `usedSlots` clamped to `slots`; WARN once per scan if clamped.
- **More than 256 distinct (prefab, quality) entries in one container**
  (possible only in big modded chests): keep the 256 largest stacks and put
  the number dropped in that container's `omittedEntries`; WARN once per
  scan.
- **Decode failure:** the container is sent with `unreadable: true`,
  `usedSlots: 0` and no items.
- **More than 4 096 containers or 50 000 entries in total:** drop vehicles
  first, then containers from the end of the sort order (`prefab`, `x`, `z`,
  `y`, `creatorPlayerId`), until within both caps. The dropped count goes in
  `omittedShared` or `omittedPrivate` by the container's privacy; WARN once
  per scan. Raid Ledger shows `omittedPrivate` to admins only.

### 1.9 Where the code lives

Per `AGENTS.md` (server-half logic in `RlValheimLink.Core`, unit-tested;
`Server/` only touches game objects):

| Piece | Project | Does |
|---|---|---|
| `Server/ChestScanner.cs` | plugin (game glue) | prefab discovery, time-sliced ZDO iteration with ZDOID dedupe, re-resolution, `Inventory.Load`; yields raw records `{ prefab, x, y, z, creator, privacy, vehicle, slots, stacks[(prefab, quality, stack)] }` |
| `ContainersBuilder` | `RlValheimLink.Core` (pure) | rounding, aggregation per (prefab, quality), same-creator collision merge, sort, slots rule and over-cap policy (§1.8), header (`serverId`, `capturedAt` = scheduled start, versions), spool key; the scan scheduler (fixed grid, forced-scan rate limit) |
| `ContainersMessage`, `RecipesMessage` + validator rules | `RlValheimLink.Contract` | after the pin refresh brings the new kinds |
| `Client/RecipesCapture.cs` | plugin (game glue) | reads recipes, pieces, stations and processors; piece atlas via the existing `AtlasBuilder` |
| recipes normalisation | `RlValheimLink.Contract` (pure) | id fallback, sorting, formula check, caps |

`Core/Enricher` assumes a sending peer; the server-built `containers` message
bypasses it and gets its header from `ContainersBuilder`.

### 1.10 Payload estimate (~500 chests)

Per container: a header of about 200 bytes (prefab, `x`/`y`/`z` to 0.1 m,
`creatorPlayerId`, privacy, vehicle and unreadable flags, slot counts) plus
one entry per
distinct (prefab, quality), about 45 bytes each
(`{"prefab":"Wood","quality":1,"stack":50}`).

| Scenario | Entries per chest | JSON | gzip on the wire (≈ 8–12×) |
|---|---|---|---|
| Sorted base (one or two materials per chest) | ~4 | ~180 KB | ~20 KB |
| Typical mixed base | ~8–20 | 270–540 KB | 30–60 KB |
| Every chest a full black-metal chest of distinct items | 32 | ~810 KB | ~80 KB |

Well inside the 10 MiB body cap proposed in §4 and the 10 MiB nginx limit
(ROK-1850). At six messages an hour that is at most ~0.5 MB of upload per hour.
The scan holds one chest's decoded items at a time. The temporary inventory
(`new Inventory(true)`) creates no item GameObjects (§0.1); only the sized
`Inventory(w, h)` variants would, one per stack, living until the end of the
frame.

## 2. Recipe catalogue (for the crafting planner)

### 2.1 Why an admin client, alongside the items export

The data itself (`ObjectDB.m_recipes`, piece tables, stations) exists on the
dedicated server too. The export still runs on a client because:

- **piece icons** (portals, walls, stations) are sprites and need the same GPU
  path as the item atlas (design §2), and build pieces are not in the items
  catalogue;
- the client sees the crafting table **after** ServerSync-style config mods
  have applied the server's recipe overrides, i.e. exactly what players see;
- it reuses the proven lease, marker and chunked-transfer flow of the items
  export: `RLVL_Control needRecipes`, a `RecipesExports` marker keyed by
  (`gameVersion`, mod-set hash) in `spool/index.json`, `Server.ItemsExportBy`
  governs who may answer, and `rlvl export recipes` forces it.

Details:

- Separate control op and marker (not folded into `needItems`), so an existing
  items export does not block the first recipes export.
- **Capability, not version:** the client's `hello` (internal RPC JSON, not the
  Raid Ledger contract) gains `features: ["recipes"]`, and the server sends
  `needRecipes` only to clients that advertise it. Since 0.2.0 clients on other
  mod versions are accepted, so an older admin client must not hold the export
  lease. The lease still expires after 10 minutes, like the items lease.
  While no connected admin client advertises the feature, the server logs
  `RLVL: recipes export waiting: no connected admin client advertises recipes`
  once per hour, so an empty planner has a visible cause.
- **Target reset:** `Spool.ResetIfTargetChanged` clears `RecipesExports` as it
  clears `ItemsExports`, so a rebuilt Raid Ledger receives recipes again.
- The new payload kind (`PayloadKind.Recipes`) is dropped by older servers
  (`MaxPayloadBytes` returns 0). **No RPC protocol bump is needed.**

### 2.2 What is read

All fields below are public in real 0.221.4 IL and present in 1.0.20 unless
marked.

- **Recipes:** `ObjectDB.instance.m_recipes` (`List<Recipe>`): per recipe
  `name` (the ScriptableObject name, e.g. `Recipe_Bronze5`), `m_item` (result
  `ItemDrop`), `m_amount`, `m_enabled`, `m_craftingStation`,
  `m_repairStation`, `m_minStationLevel`, `m_requireOnlyOneIngredient`,
  `m_qualityResultAmountMultiplier`, `m_listSortWeight`, and new in 1.0.20
  `m_noCraftOnlyUpgrade`; `m_resources` (`Piece.Requirement[]`): `m_resItem`,
  `m_amount`, `m_amountPerLevel`, `m_extraAmountOnlyOneIngredient`,
  `m_recover`, and new in 1.0.20 `m_upgraderResource`.
- **Build pieces:** for every tool (`m_shared.m_buildPieces`), each piece's
  prefab, `Piece.m_name` (localised), `m_category`, `m_craftingStation`,
  `m_resources` (pieces have no quality), `m_icon`.
- **Crafting stations:** each distinct `CraftingStation` referenced above:
  prefab, `m_name`, icon, and max level. `CraftingStation.GetLevel()` is
  `1 + extension count`, where `StationExtension.FindExtensions` counts
  distinct extension pieces whose `m_craftingStation.m_name` matches, every
  copy counting only when the extension has `m_stack` **(0.221.4 IL)**. The
  export computes 1 + the number of distinct extension prefabs, capped at 20,
  and flags a station with a stackable extension.
- **1.0.20 upgrade stations (seen by the spike, §0.1):** 1.0.20 adds
  `CraftingStation.m_upgrader`, `m_canRepair`, `m_hasCraftTab`.
  `UpgradeStation` is the one station with `m_upgrader = true`; 271
  requirements carry `Requirement.m_upgraderResource` (upgrade items such as
  `Upgrader1Armor`) and 28 gold-gear recipes are `Recipe.m_noCraftOnlyUpgrade`.
  Exported additively as `stations[].upgrader` and
  `resources[].upgraderResource` (§5).
- **Processing steps (in the first story, operator ruling).** What the planner
  needs to expand Bronze → Copper + Tin → Copper ore + Tin ore + Coal, or
  Mead → Mead base (fermenter) → cauldron ingredients. Found by walking the
  public `ZNetScene.instance.m_prefabs` (and `m_nonNetViewPrefabs`) with
  `GetComponentsInChildren<T>(true)`, because a station component may sit on a
  child object. Values are read from the live prefab components, never from
  code defaults (Unity serialisation overrides them). Semantics below are from
  0.221.4 method bodies; the 1.0.20 stubs keep every field listed.

  | Component | Vanilla stations (prefab names BELIEVED, spike confirms) | Per conversion (public) | Station fields (public) | One conversion means |
  |---|---|---|---|---|
  | `Smelter` | smelter, charcoal kiln, blast furnace, windmill, spinning wheel, eitr refinery | `m_conversion[]`: `m_from`, `m_to` | `m_fuelItem`, `m_maxFuel` (0 = no fuel), `m_fuelPerProduct`, `m_secPerProduct`, `m_maxOre`, `m_requiresRoof`, `m_windmill` (wind-powered) | 1 × from → 1 × to; `m_fuelPerProduct` fuel when `m_maxFuel > 0`; `m_secPerProduct` seconds (divided by wind power for a windmill) |
  | `Fermenter` | fermenter | `m_conversion[]`: `m_from`, `m_to`, `m_producedItems` | `m_fermentationDuration` | 1 × from → `m_producedItems` × to after the duration, covered and under a roof; one batch at a time; no fuel |
  | `CookingStation` | cooking station, iron cooking station, oven | `m_conversion[]`: `m_from`, `m_to`, `m_cookTime` | `m_useFuel`, `m_fuelItem`, `m_secPerFuel`, `m_overCookedItem`, slot count | 1 × from → 1 × to after `m_cookTime`; burns after 2 × `m_cookTime`; fuel, when used, burns 1 per `m_secPerFuel` seconds while lit, so it is not a per-product cost; cooking skill can add a bonus item |
  | `Incinerator` | obliterator | `m_conversions[]`: `m_requirements[]` (`m_resItem`, `m_amount`), `m_result`, `m_resultAmount`, `m_priority`, `m_requireOnlyOneIngredient` | `m_defaultResult`, `m_defaultCost` | every full set of requirements → `m_resultAmount` × result, conversions tried by `m_priority` (highest first; the prefab asset is unsorted, so the exporter sorts); leftovers become `floor(stack count / m_defaultCost)` × default result |

  1.0.20 adds `Smelter.m_noSourceConversion` and several `CookingStation`
  toggles (`m_canOvercookItems`, `m_canGiveBonusYield`, `m_useFueldWhileEmpty`
  [sic]) whose meaning needs the spike; the export carries them only once
  read. The exporter never calls `IncineratorConversion.AttemptCraft` (public,
  but it mutates an inventory). `Game.m_damageTypeDropConversions` (burnt
  drops) is a world rule, not a station, and is not exported. Producers with no
  input (beehive, sap collector, crops) are not processes; the planner treats
  any item with no recipe and no process as **gathered**.

### 2.3 Upgrade cost per quality

From 0.221.4 IL:

```
Requirement.GetAmount(q)          = q <= 1 ? m_amount : (q - 1) * m_amountPerLevel
Recipe.GetRequiredStationLevel(q) = max(1, m_minStationLevel) + (q - 1)
Recipe.GetRequiredStation(q)      = m_craftingStation ?? (q > 1 ? m_repairStation : null)
```

All three are pure and are evaluated by the exporter. `Recipe.GetAmount` is
never called: it dereferences `Player.m_localPlayer` for
`requireOnlyOneIngredient` recipes.

Proposal: export the raw fields and let Raid Ledger apply this formula for
qualities 1…`maxQuality` (from the items catalogue). That keeps the body
bounded (precomputed per-quality tables multiply the size by up to 10). To stay
honest about mods that patch these methods, the exporter evaluates the live
`Requirement.GetAmount(q)`, `GetRequiredStationLevel(q)` and
`GetRequiredStation(q)` for every quality and sets `formula: "custom"` on any
recipe where they disagree with the formula (the web then shows "upgrade cost
may differ in game"). Recipes with `requireOnlyOneIngredient` list
alternatives rather than a sum; the planner treats their resources as "any one
of".

### 2.4 Size

Vanilla 1.0.x has roughly 600–700 item recipes and 400–500 pieces, about 3–4
requirements each, ~120 bytes per requirement with keys: about 0.5 MB JSON
before the piece atlas. Processing adds roughly 150–250 conversions at ~150
bytes, under 40 KB. The piece atlas uses the same `AtlasBuilder` (64 px, 32 per
row): ~500 icons → 2048 × 1024, 1–2 MB PNG.

## 3. Raid Ledger side (read-only scouting of `sjdodge123/Raid-Ledger` main)

Paths are relative to the Raid Ledger repo.

### 3.1 Ingest and storage

- The endpoint dispatches by route kind:
  `api/src/plugins/valheim/link/ingest/valheim-link-ingest.controller.ts`
  (`POST /plugins/valheim/link/messages/:kind`). Per-kind body caps are a
  `Record<Kind, number>` in `ingest/valheim-link-message.parse.ts`, so a new
  kind is a compile error until it has a cap.
- Every message lands verbatim in `valheim_link_snapshots`
  (`api/src/drizzle/schema/valheim-link.ts`), upserted with
  `ON CONFLICT (pelican_server_id, kind, snapshot_key) … WHERE captured_at < excluded.captured_at`.
  The CHECK constraint `valheim_link_snapshots_kind_chk` lists the kinds
  (`'character','map','items'`), so each new kind needs a migration in its
  contract/ingest story.
- Snapshot keys: `valheimSnapshotKey` (`ingest/valheim-link-snapshot.helpers.ts`)
  returns `gameVersion` for `items` and `steamId:playerId[:worldUid]`
  otherwise; it gains `worldUid` for `containers` and `gameVersion` for
  `recipes`. (`containers|{worldUid}` is only the mod's spool key.) The
  importer's `keyMatches` (`armory/valheim-armory.mappers.ts`) compares the
  snapshot row's `steam_id`/`player_id`/`world_uid` columns, which
  `valheimSnapshotRow` fills from `msg.character`; both gain a `containers`
  branch that fills `world_uid` from the message. `recipes` gets items-like
  branches (no steam, player or world) in both.
- `valheimBlobRefs` must learn the `recipes` atlas, or ingest never reports it
  in `missingBlobs` and blob upload never links it (§5).
- A 30-second importer cron (`armory/valheim-armory-import.service.ts`,
  `apply()` switch per kind) projects snapshots into normalised tables
  (`api/src/drizzle/schema/valheim-armory.ts`: `valheim_profiles`,
  `valheim_maps`, `valheim_item_catalogs` keyed by server + game version).
- Proposed new tables, in one migration in the importer story, numbered at
  merge time:
  - `valheim_containers`: one row per (server, world, prefab, x, y, z,
    creator) with
    `creator_player_id` **text** (the same canonical int64 spelling as
    `valheim_profiles.player_id`, so ownership checks need no cast), privacy
    (CHECK `public|private|group`), vehicle flag, slots, used slots, `items`
    jsonb, `unreadable`, `omitted_entries`, `game_version`, `captured_at`,
    and the conventions every armory
    table carries: `source_snapshot_id` (FK through the `snapshotFk` helper,
    because drizzle's default constraint name would exceed Postgres's 63
    characters) and `imported_at`. Replaced wholesale per world on each import,
    in one transaction. The importer stores every chest as sent and **does not
    resolve owners**: ownership changes without a new snapshot (§3.2).
  - `valheim_recipe_catalogs`: modelled on `valheim_item_catalogs`: unique
    (server, game version), jsonb for recipes, pieces, stations, processors
    and processes, the piece atlas sha, `source_snapshot_id`, `imported_at`.
  - `valheim_base_names`: (server, world, anchor `x`/`z`, name, created_at),
    written by admins (§3.3).
  - Indexes: (`pelican_server_id`, `privacy`) on containers, and a new
    (`pelican_server_id`, `player_id`) on `valheim_profiles` for the ownership
    check.
- **Which world:** a server keeps one `containers` snapshot per world. The read
  routes use the world of the server's newest `containers` snapshot (the world
  it runs now, since snapshots arrive every 10 minutes); rows of other worlds
  stay but are not shown. A world picker can come later.
- **Which catalogue:** containers carry `gameVersion` in the header. Totals,
  `/catalog` and `/recipes` resolve the catalogue for that version; when it has
  not arrived yet (the game just updated and no admin client has exported), they
  fall back to the server's newest catalogue and say so. A prefab missing from
  the catalogue still counts in totals and renders through the existing
  fallback ladder (`resolveItem`: icon → initials → prefab id); in the planner
  its `maxQuality` is unknown, so it offers crafting only, no upgrades.
- Quotas: one `containers` snapshot per world and one `recipes` snapshot per
  game version, far below the 2 000-snapshot per-server quota.

### 3.2 Read API and visibility

Reads live in `api/src/plugins/valheim/armory/read/` behind
`AuthGuard('jwt')` + `PluginActiveGuard` + `@RequirePlugin('valheim')`. Today
the catalogue is only projected for the prefabs one character references;
there is **no full-catalogue route**. New routes, same guard stack, all taking
the viewer as the actor:

- `GET plugins/valheim/armory/servers/:serverId/catalog`: full items catalogue
  + atlas sha, for the containers' game version;
- `GET …/servers/:serverId/materials`: shared totals, the shared chest list
  with positions and contents, base clusters, and the viewer's visible
  personal chests with their subtotal;
- `GET …/servers/:serverId/recipes`: recipes, pieces, stations, processors and
  processes;
- admin: `PUT/DELETE …/servers/:serverId/base-names` (AdminGuard).

Blobs (item and piece atlases) already stream from
`…/servers/:serverId/blobs/:sha256`.

**Who can see the hub.** Ruling 1 makes everything except personal chests
visible to "anyone who can see the server hub". In code that is: signed in,
and the server's hub is not hidden from the viewer. The service resolves the
hub with `ValheimArmoryReadRepository.findServerHub(serverId)` and answers 404
when `hub.hidden && !isStaff(actor)`, the rule `ValheimArmoryReadService.hubIdFor`
already uses (`isHiddenFrom` in `api/src/guilds/hub-hidden.helpers.ts` is
equivalent). A server with no hub answers 404. `HubVisibleGuard` does not
apply: it keys on a `:id` hub param and passes routes without one. The
existing armory routes only check sign-in (ROK-1835 ruling R4); applying the
hub check to them too is a separate question (§8).

**Shared data has no viewer term.** Shared totals, the shared chest list, map
markers and clusters use `privacy <> 'private'` and nothing else, so every
viewer, admin included, sees the same shared view and the same totals.

**Personal chests** (`privacy = 'private'`) are queried separately, for the
"Personal chests" section and its subtotal and for the planner's "include my
personal chests" toggle, and only through one helper. `:scope` is `visible`
for the section (admins see all) and `own` for the planner toggle (everyone,
admins included, adds only their own):

```sql
c.privacy = 'private' AND (
  (:isAdmin AND :scope = 'visible')                      -- users.role = 'admin'
  OR (:notImpersonated AND EXISTS (
        SELECT 1 FROM valheim_profiles p JOIN users u ON u.id = p.claimed_by
        WHERE p.pelican_server_id = c.pelican_server_id   -- same server only
          AND p.player_id         = c.creator_player_id
          AND p.claimed_by        = :viewerId
          AND (p.claim_source IN ('auto', 'admin')
               OR (p.claim_source = 'manual' AND u.steam_id = p.steam_id))
          AND NOT EXISTS (                                 -- ambiguous creator: admins only
                SELECT 1 FROM valheim_profiles q
                WHERE q.pelican_server_id = p.pelican_server_id
                  AND q.player_id         = p.player_id
                  AND q.steam_id         <> p.steam_id))))
```

- **Owner** = the Raid Ledger user who holds the creator's character **on that
  server** through a Steam-matched (`auto`) claim, an admin assignment, or a
  manual claim made by the user whose verified Steam id is the profile's own.
  A playerId repeats across servers (it lives in the local character file), so
  a claim elsewhere grants nothing. A manual claim on someone else's character
  (`POST …/characters/:id/claim` needs no Steam check) still shows the
  character sheet but grants no personal chests (open question §8). `EXISTS`,
  not a join, so two profiles cannot double-count.
- **Ambiguous creator:** a playerId is client-controlled; a member could edit
  their character file to reuse a victim's playerId, get that profile
  auto-claimed under their own Steam id, and pass a plain ownership check.
  When one playerId on a server belongs to profiles of more than one Steam id,
  its personal chests go to admins only. (In game the same spoof would open the
  chest too: `CheckAccess` compares player ids, 0.221.4 IL.) Spike check 4 also
  looks at whether 1.0.20's `creatorIndex` identifies the placer's platform id,
  which would allow a stronger match.
- **No owner yet:** no profile, an unclaimed profile, a manual claim by someone
  else, an ambiguous creator, or a deleted user → admins only. A profile exists
  only once that player's client has sent a `character` message (mod
  installed, not opted out, Steam identity), so players without the client mod
  never see their own personal chests until they install it; admins cannot
  assign a character that has no profile. A character claimed manually by
  someone else first never auto-attaches (auto-attach fills only unclaimed
  profiles), so its owner needs an admin to reassign it. The personal
  section's empty state says so. Visibility appears by itself once a valid
  claim exists, with no re-import.
- **Admin** = `users.role = 'admin'` (`AdminGuard`, web `isAdmin`), as on every
  other Valheim admin surface.
- **Impersonation:** an impersonated session (`req.user.impersonatedBy` set)
  sees no personal chests at all. Without this, an operator impersonating a
  member would see that member's personal chests.
- **No side channels:** personal chests never feed clusters, shared totals,
  chest counts, warnings or map markers; members never see a count of personal
  chests they cannot open (`omittedPrivate` and the flags on personal
  containers reach only viewers allowed to see them); routes identify chests
  by (prefab, position) and never return table row ids (sequential ids would
  reveal how many hidden rows sit between visible ones) or `creatorPlayerId`
  (which would hand out the ids the ambiguity rule guards); a personal chest
  the viewer may not see answers 404, like a missing one; the snapshot age is
  shown at minute precision. Raw snapshot payloads (`valheim_link_snapshots`)
  stay internal; no route serves them.
- **Caching:** responses depend on the viewer, so they carry
  `Cache-Control: private, no-store`; the web scopes their query keys with
  `useViewerCacheScope()` and invalidates them on claim and owner changes
  (`use-valheim-claims.ts`).
- **Residual:** whoever holds the ingest token controls `privacy` and
  `creatorPlayerId` on the wire, the same accepted residual as for the other
  kinds.
- **Tests** (mirroring `valheim-armory-read.integration.spec.ts`): owner with
  an auto claim sees their personal chest in the personal section, subtotal
  and planner toggle; another member gets 404 and no count; admin sees all in
  the section but only their own through the planner toggle; a manual claim on
  someone else's character grants nothing, a manual claim matching the
  claimer's verified Steam id does; an unclaimed creator → admin only; a
  playerId shared by two Steam ids → admin only; admin assign and Steam
  auto-attach flip visibility without a re-import; the same playerId claimed
  on another server grants nothing; an impersonated session sees no personal
  chests; two personal chests of different creators at one position each go
  to their own owner; an unreadable personal chest gives a member no warning;
  a personal chest between two shared groups does not merge them; shared
  totals are identical for member and admin; responses contain no sequential
  ids and no `creatorPlayerId`; hidden hub → 404 for a member, 200 for an
  admin; a missing catalogue version falls back to the newest; an unknown
  prefab still counts in totals.

### 3.3 Web

- Valheim registers hub tabs through `registerServerHubGameTab('valheim', …)`
  (`web/src/plugins/valheim/register.ts`; seam in
  `web/src/plugins/pelican/hub/server-hub-game-tabs.ts`). Characters is order
  15, Map 20. A hub is one Pelican server, so no server picker is needed.
- **Base materials** = a new hub tab (`id: 'materials'`, order 25):
  - search box and category filter (`components/ui/search-input.tsx`,
    `filter-entry.tsx`, `filter-panel.tsx`) over a sortable table of item icon,
    name, category, total by quality and number of chests;
  - a "where is it" drawer listing chests by base with their contents, and a
    toggle that drops chest markers on the existing map component (everything
    shared is visible to everyone who can see the hub, ruling 1). Markers
    overlay only a map whose `worldUid` equals the containers' world;
    otherwise the toggle is disabled with "map of this world not available";
  - an "include carts and ships" toggle (default on) over totals, list and
    markers;
  - bases = single-link clusters of **shared** chests within 64 m, the same
    for every viewer; an admin anchor belongs to a cluster when it lies within
    64 m of one of its chests, and with several anchors the oldest name wins;
    otherwise "Base near (x, z)"; a named anchor with no shared chests near it
    is not shown;
  - a "Personal chests" section below, shown only when the viewer has any to
    see (ruling 2, §3.2);
  - the snapshot's age at minute precision ("scanned 4 min ago") and a
    warning when a visible container is `unreadable` or has `omittedEntries`,
    or `omittedShared` is above zero (admins also see `omittedPrivate`);
  - empty state: "No chest data yet. The server sends it every 10 minutes; the
    first arrives a few minutes after it starts." (Only if an operator switched
    scanning off does it never come.)
  - Item icons reuse `web/src/plugins/valheim/character/item-icon.tsx` and
    `item-tile.tsx`, but `character/use-item-icons.ts` is tied to a character
    and needs a `(serverId, items, atlas)` variant.
- **Crafting planner** = a second hub tab or a route
  (`/valheim/servers/:serverId/planner`, via `web/src/lazy-routes.ts` and
  `web/src/app-routes.tsx`):
  1. search any craftable item or piece (`combobox.tsx` over recipes ∪ pieces,
     with the items catalogue for names and icons);
  2. pick target quality (craft, or upgrade from q to q′) and quantity;
  3. the recipe tree: each requirement shows **have** (shared totals, plus the
     viewer's own personal chests behind an "include my personal chests"
     toggle) / **need** / **short**, with station and station level, and an
     expand control on intermediates that have their own recipe (Bronze →
     Copper + Tin) or a processing step (Copper → Copper ore + Coal at a
     smelter, Mead → Mead base at a fermenter), including fuel and time; items
     with neither are leaf "gathered" materials. Expansion stops at a cycle
     (modded recipes can loop) and at an item already covered by stock;
  4. a multi-item shopping list summing needs across targets, minus stock,
     kept in local storage per server for now (a shared list is an open
     question, §8);
  5. optional per-character view: grey out recipes the character has not
     discovered, using `knownRecipes` from the `character` snapshot (already
     stored, not yet returned by the API);
  6. without chest data (scanning off on that server) "have" shows as unknown,
     not zero, and the planner still prices recipes;
  7. without a recipes snapshot: "Recipes arrive when an admin with the mod
     joins the server."
- No virtualisation library exists in the web app. ~1 000 catalogue rows render
  fine without one; adding one would be a "New pattern" line in the PR.

## 4. Linear story draft — contract: `containers` kind (filed as ROK-1853)

**Shipped** in Raid Ledger `c7914ec` (2026-10-20), pinned in mod 0.2.2: CONTRACT.md §5a is
now the source of truth for the field table, caps and refinements; this draft is kept for the
reasoning. The mod 0.2.2 validator knows the kind; nothing sends it until the chest scanner ships.

**Title:** Valheim Link contract v1 (additive): `containers` message — chest
contents per world

**Context.** The RL Valheim Link mod can read every player-built container on
the dedicated server from the world's ZDOs (`docs/design-chests.md` in
`Raid-Ledger-Plugins/rl-valheim-link`) and will send them every 10 minutes.
This story adds the message so the mod may emit it; the mod changes nothing
until this ships and its pin is refreshed. Lineage: ROK-1829 (contract),
ROK-1833 (ingest), ROK-1835 (importer), ROK-1834 (mod). Operator rulings:
everything is visible to hub viewers except personal chests (owner and admins
only); the importer and read story carries that rule and its tests.

**Endpoint.** `POST /api/plugins/valheim/link/messages/containers`, same auth,
gzip, error codes and rate buckets as the other kinds. Snapshot key
(`serverId`, `worldUid`), latest `capturedAt` wins (a server that switches
worlds keeps one snapshot per world).

**Schema** (`packages/contract/src/valheim-link-containers.schema.ts`, all
objects strict, header = `valheimLinkHeaderShape`):

| Field | Type | Rule |
|---|---|---|
| `kind` | `"containers"` | |
| `worldUid` | int64 string | the world scanned |
| `containers[]` | ≤ 4 096 | one per counted container, sorted by `prefab`, then `x`, `z`, `y`, `creatorPlayerId` |
| `containers[].prefab` | prefab id | the container piece (`piece_chest`, `Cart`, …) |
| `containers[].x` / `y` / `z` | number, \|v\| ≤ 32 768 | world metres, rounded by the mod to 0.1 m, same frame as map pins; `y` is height |
| `containers[].creatorPlayerId` | int64 string, not `"0"` | `Piece` creator = the `playerId` of the `character` kind; the mod sends only player-built containers |
| `containers[].privacy` | `"public"` \| `"private"` \| `"group"` | `Container.m_privacy` of the prefab |
| `containers[].vehicle` | boolean | the container belongs to a cart or ship |
| `containers[].slots` | int 1–4 096 | width × height of the container |
| `containers[].usedSlots` | int 0–`slots` | occupied cells before aggregation |
| `containers[].unreadable` | boolean | the mod could not decode it; `items` empty, `usedSlots` 0 |
| `containers[].omittedEntries` | int 0–100 000 000 | (prefab, quality) entries dropped because the container had more than 256 |
| `containers[].items[]` | ≤ 256 | stacks aggregated per (`prefab`, `quality`) |
| `items[].prefab` | prefab id | |
| `items[].quality` | int 1–10 | |
| `items[].stack` | int 1–1 000 000 000 | sum of stack sizes for that pair in this container |
| `omittedShared` / `omittedPrivate` | int 0–1 000 000 each | containers dropped by the count caps, split by privacy so a hidden chest is never counted to members |

Totals are **not** sent; Raid Ledger computes them from `containers[]`.
Not sent: slot positions, durability, variant, crafter, custom data, cheat
flags, ZDO ids, creator names, ward data.

**Refinements (Zod; not expressible in `schema.json`; add to CONTRACT §6):**
`items` unique per (`prefab`, `quality`) within a container; (`prefab`, `x`,
`y`, `z`, `creatorPlayerId`) unique within `containers`; `usedSlots ≤ slots`;
`unreadable` implies empty `items` and `usedSlots` 0; `creatorPlayerId` not
`"0"`; total `items` entries across all containers ≤ 50 000.

**Cap.** `VALHEIM_LINK_CONTAINERS_MAX_BYTES = 10 485 760` (10 MiB). Worst
schema-valid body: 50 000 entries × 142 B (96-char prefab, max numbers) ≈ 7.1
MB + 4 096 container headers × ~346 B ≈ 1.4 MB ≈ 8.5 MB, under the cap,
honouring CONTRACT §7's "a schema-valid message never trips a body cap". The
story adds a spec that builds the largest schema-valid message for each kind
and asserts it parses and fits its cap. Typical: 0.2–0.6 MB JSON for ~500
chests.

**Versioning.** Additive under CONTRACT §9's closed-enum rule: the header
`kind` is a closed enum (CONTRACT §2), so a new value is additive and the
server ships first. The PR body says so.

**Changes:** Zod schema + the message union + `ValheimLinkMessageKindSchema`;
cap constant; `schema.json` regenerated, including the refinement list in
`buildValheimLinkJsonSchema`'s description; fixtures `containers-vanilla.json`
(synthetic ids, three chests, one personal chest, one cart) and invalid
fixtures for duplicate (prefab, quality), duplicate (prefab, position,
creator), `usedSlots > slots`, `unreadable` with items, `creatorPlayerId:
"0"`, coordinate out of range, unknown key; CONTRACT §1 key list, §2 `kind` row, the artifact table's Zod file list,
a new section, §6 refinements, §7 cap row; CHANGELOG entry marked
**additive**. Ingest: cap map, `valheimSnapshotKey` and `valheimSnapshotRow`
branches, the `valheim_link_snapshots_kind_chk` migration. Importer, tables and
read routes are a separate story (§6.1).

**Acceptance:** golden fixture parses; each invalid fixture yields its
`{path, code}`; the largest-message spec passes; a 500-chest synthetic body is
accepted by the ingest integration spec; a second message with an older
`capturedAt` is `stale`.

## 5. Linear story draft — contract: `recipes` kind (filed as ROK-1854, with the planner)

**Title:** Valheim Link contract v1 (additive): `recipes` message — crafting
recipes, build pieces, stations and processing steps

**Context.** Raid Ledger's crafting planner needs the server's real recipes,
including modded ones, without hard-coding any. The mod's admin client exports
them once per (game version, mod set), like the `items` catalogue, together
with build pieces, crafting stations and processing steps (smelting, kiln,
windmill, spinning wheel, refinery, fermenting, cooking, obliterator; operator
ruling). Names and icons of items come from the `items` catalogue; pieces and
stations carry their own icons in a separate atlas. Lineage as in §4.

**Endpoint.** `POST /api/plugins/valheim/link/messages/recipes`. Snapshot key
(`serverId`, `gameVersion`), latest wins, the same as `items`.

**Schema** (`packages/contract/src/valheim-link-recipes.schema.ts`, strict;
every number bounded):

| Field | Type | Rule |
|---|---|---|
| `kind` | `"recipes"` | |
| `locale` | as in `items` | language of piece and station names |
| `atlas` | binary ref \| `null` | piece and station icons; required when any icon is an atlas icon |
| `stations[]` | ≤ 256 | `{ prefab, name 1–96, maxLevel 1–20, stackableExtension: boolean, upgrader: boolean, icon }`, `prefab` unique; `upgrader` = `CraftingStation.m_upgrader` (1.0.20 `UpgradeStation`) |
| `recipes[]` | ≤ 4 096 | see below, `id` unique |
| `recipes[].id` | `^[A-Za-z0-9_.-]{1,96}$` | the Recipe asset name; when it does not match, the mod sends `<result>.<n>` with `<result>` shortened so the id stays ≤ 96 characters |
| `recipes[].result` | prefab id | item crafted; name/icon from the `items` catalogue |
| `recipes[].amount` | int 1–10 000 | items produced per craft |
| `recipes[].enabled` | boolean | |
| `recipes[].station` / `repairStation` | prefab id \| `null` | references `stations[].prefab` |
| `recipes[].minStationLevel` | int 0–20 | |
| `recipes[].upgradeOnly` | boolean | `m_noCraftOnlyUpgrade` (1.0.x) |
| `recipes[].anyOneOf` | boolean | `m_requireOnlyOneIngredient`: resources are alternatives |
| `recipes[].qualityResultAmountMultiplier` | number 0–100 | only meaningful with `anyOneOf` |
| `recipes[].formula` | `"vanilla"` \| `"custom"` | `custom` when the live per-quality amounts, station levels or stations differ from the documented formula |
| `recipes[].resources[]` | ≤ 16 | `{ prefab, amount 0–10 000, amountPerLevel 0–10 000, extraAmountOnlyOneIngredient 0–10 000, recover: boolean, upgraderResource: boolean }`, `prefab` unique; `upgraderResource` = `Piece.Requirement.m_upgraderResource` (1.0.20 upgrade items such as `Upgrader1Armor`) |
| `pieces[]` | ≤ 4 096 | `{ prefab, name 1–96, category (identifier ≤ 48, open set, `Piece.m_category`), tool (prefab id), station (prefab id \| null), icon, resources[] ≤ 16 of { prefab, amount 0–10 000, recover: boolean } }`, `prefab` unique |
| `processors[]` | ≤ 256 | processing stations, `prefab` unique; name and icon come from `pieces[]` when the station is buildable |
| `processors[].prefab` | prefab id | the station prefab (`smelter`, `fermenter`, …) |
| `processors[].type` | `"smelter"` \| `"fermenter"` \| `"cooking"` \| `"incinerator"` | the game component; new values are additive (CONTRACT §9) |
| `processors[].fuel` | prefab id \| `null` | `m_fuelItem` when the station uses fuel |
| `processors[].fuelPerProduct` | int 0–1 000 \| `null` | smelter only: fuel consumed per output |
| `processors[].fuelSecondsPerUnit` | number 0.001–86 400 \| `null` | cooking with fuel only: one fuel per this many seconds lit |
| `processors[].windPowered` | boolean | smelter with a windmill: time varies with wind |
| `processors[].requiresRoof` | boolean | |
| `processors[].maxInput` | int 1–10 000 \| `null` | queue size (smelter `m_maxOre`), slot count (cooking), 1 (fermenter) |
| `processors[].burntResult` | prefab id \| `null` | cooking `m_overCookedItem` |
| `processors[].defaultResult` | `{ prefab, perInputs 1–10 000 }` \| `null` | obliterator catch-all: one result per `perInputs` leftover items |
| `processes[]` | ≤ 2 048 | one per conversion, sorted by `processor`, then `priority` descending |
| `processes[].processor` | prefab id | references `processors[].prefab` |
| `processes[].inputs[]` | 1–8 | `{ prefab, amount 1–10 000 }`, `prefab` unique; one entry for smelter, fermenter and cooking |
| `processes[].anyOneOf` | boolean | obliterator `m_requireOnlyOneIngredient` |
| `processes[].output` | `{ prefab, amount 1–10 000 }` | fermenter amount = `m_producedItems`, obliterator = `m_resultAmount`, else 1 |
| `processes[].seconds` | number 0–604 800 \| `null` | smelter `m_secPerProduct`, cooking `m_cookTime`, fermenter `m_fermentationDuration`; `null` for the obliterator |
| `processes[].priority` | int −10 000–10 000 | obliterator order (highest first); 0 otherwise |

Icons of stations and pieces are `{ kind: "atlas", x, y, w, h }` or `null`
only (no `file` icons), which keeps the worst case below the cap.

Per-quality cost is derived on Raid Ledger with the formula, copied into
CONTRACT from §2.3 here. Fuel for a smelter is `fuelPerProduct × outputs`; for
an oven it depends on run time, so the planner shows it as an estimate.

**Refinements (Zod; add to CONTRACT §6):** `prefab` unique in `stations`,
`pieces` and `processors`; `recipes[].id` unique; `prefab` unique within each
`resources` and `inputs`; `station`, `repairStation` and `processor`
references resolve; an atlas icon needs a non-null `atlas` and must lie inside
it; total resources across recipes and pieces plus process inputs ≤ 12 000.

**Cap.** `VALHEIM_LINK_RECIPES_MAX_BYTES = 10 485 760`. Without a total cap the
worst case is far over (≈ 22 MB of resources alone: a maximal recipe resource
is ~201 B, a piece resource ~141 B). With atlas-only icons the headers come to
≈ 7.0 MB (4 096 recipes × ~610 B, 4 096 pieces × ~1 050 B, 256 stations ×
~800 B), processes and processors ≈ 0.8 MB, and 12 000 resources × ≤ 201 B ≈
2.4 MB, ≈ 10.25 MB in total against 10.49 MB. Vanilla needs 3 000–5 000
resources, so 12 000 leaves about 2.5× headroom. The largest-message spec from
§4 confirms the exact figure; if it lands over, the alternative is a 16 MiB
cap with gzip mandatory for this kind (nginx's 10 MiB limit applies to the
compressed body). Ruled 2026-10-20 (§8 item 16): per-array caps plus the
largest-message CI test; mandatory gzip only if the measured worst case still
exceeds the cap.

**Changes:** as §4 (Zod schema, union, kind enum, cap constant, `schema.json`
and its refinement description, fixtures, CONTRACT §1/§2/artifact table/new
section/§6/§7, CHANGELOG **additive**), plus on the ingest side the cap map,
the `valheimSnapshotKey` (`gameVersion`) and `valheimSnapshotRow` (no steam,
player or world, like `items`) branches, the kind CHECK migration, and
the **`valheimBlobRefs` branch for the recipes `atlas`**, without which ingest
never reports it in `missingBlobs` and blob upload never links it.

**Acceptance:** a golden fixture covering one of each processor type (smelter
with fuel, kiln without, fermenter with 6 outputs, oven with fuel seconds,
obliterator with two prioritised conversions and a default result) parses;
invalid fixtures for an `atlas` icon outside the atlas, a dangling `station`
or `processor` reference, a duplicate `id`, a duplicate input prefab and an
unbounded number each yield their `{path, code}`; the largest-message spec
passes; the recipes atlas is listed in `missingBlobs` until uploaded.

## 6. Stories and effort

### 6.1 Other stories (outlines for Linear)

- **Mod — spike (first slice, test server only, no upload).** PR 16, on by
  default, logs the checks in §7. Done = the operator has run it and the UNVERIFIED items
  in §1 are answered in this document.
- **Raid Ledger — importer, tables and read routes.** `valheim_containers`,
  `valheim_recipe_catalogs`, `valheim_base_names` (§3.1); importer branches;
  `keyMatches` branches for both kinds; `/catalog`, `/materials`, `/recipes`,
  base-name admin routes; the hub check and the personal-chest rule with every
  test in §3.2. Done = all §3.2 tests green.
- **Raid Ledger — Base materials tab.** §3.3 first bullet list, MSW mocks,
  Vitest, one smoke spec. Done = the operator sees the base's totals and chests
  on the map, and a member does not see another member's personal chests.
- **Raid Ledger — Crafting planner.** §3.3 planner list. Done = searching
  "Bronze" shows Copper, Tin, ores and coal with have/need against the chests,
  and a shopping list of two targets sums correctly.
- **Mod — `containers`.** §1.4–§1.9: scanner, `ContainersBuilder`, config
  (`Chests.Enabled`, `IntervalMinutes`, `StacksPerTick`, `IncludeVehicles`,
  `ExcludePrefabs`, `ExtraPrefabs`), `PayloadKind.Command` + `rlvl export
  chests`, log lines (§7), fixtures, version bump. Done = operator checklist
  passes and the dashboard shows moved items within 11 minutes (one interval,
  upload, importer cron).
- **Mod — `recipes`.** §2: capture, piece atlas, processors, `features` in
  `hello`, `needRecipes`, `RecipesExports` marker and its reset. Done = the
  planner shows modded recipes from the operator's pack.

### 6.2 Effort (rough, one engineer, including tests; rows match §4, §5 and §6.1)

| Story | Repo | Days |
|---|---|---|
| Spike: §7 checks on the test server (dump, no upload) | mod | 0.5–1 |
| `containers`: scanner, Core builder, model, validator, fixtures, config, forced-scan request, operator checklist lines | mod | 2.5–3 |
| `recipes`: capture coroutine, piece atlas, processing-station discovery, model, validator, `needRecipes` + marker + `features` | mod | 2.5–3 |
| Contract `containers` + ingest (incl. largest-message spec) | RL | 1.5 |
| Contract `recipes` (with processing) + ingest | RL | 1.5–2 |
| Importer, tables, read routes, hub check, personal-chest rule, base names, tests | RL | 5–6 |
| Base materials tab (web): generalised icon hook, toggles, clusters view, tests | RL | 2–2.5 |
| Crafting planner (web): search, tree with processing steps and fuel, have/need, shopping list, tests | RL | 4.5–5 |
| **Total** | | mod ≈ 5.5–7, RL ≈ 14.5–17 |

Order: spike → contract stories (Raid Ledger ships first; the mod pins) → mod
`containers` (it alone unblocks the dashboard) → importer and read routes →
dashboard → mod `recipes` → planner. A shared shopping list and history
follow if wanted (§8).

## 7. Verification plan

- **Spike (test server only, no upload, on by default in the spike build):**
  1. **Storage:** list every string and byte-array key on one known chest ZDO
     (hashes compared with `"items".GetStableHashCode()` and `"itemData"`),
     §1.2.
  2. **Decode:** which data shape and which `Load` overload decode a 1.0.20
     chest on a dedicated server; whether `new Inventory(true)` with
     `Load(pkg, true)` avoids GameObject instantiation; milliseconds per
     stack; whether a chest's row count varies at runtime; whether the package
     still starts with (version, count).
  3. **Counts:** per prefab, chests counted, skipped (creator 0), duplicate
     ZDOIDs removed, stacks decoded and dropped; totals of three named
     materials, which the operator compares with an in-game look into the same
     chests, including one chest in an area no player has loaded since the
     restart (residency) and one personal chest.
  4. **Creator:** on a personal chest, `creator` and `creatorIndex`, compared
     with the placing character's `playerId` from its `character` message, and
     whether `creatorIndex` resolves to the placer's platform (Steam) id.
  5. **Prefabs:** every prefab in `ZNetScene.m_prefabs` carrying `Container`,
     `Smelter`, `Fermenter`, `CookingStation` or `Incinerator`, with its
     component values (confirms the BELIEVED names in §1.4 and §2.2).
  6. **Freshness:** move items in a chest, wait for the next scan, see the
     change.
  7. **1.0.20-only fields:** values of `m_upgraderResource`,
     `m_noSourceConversion`, the cooking toggles and `CraftingStation`'s
     `m_upgrader`, `m_canRepair`, `m_hasCraftTab` on vanilla prefabs.
  ZDO id stability is not tested: ids are assigned in load order, so a quiet
  restart can reproduce the same ids without proving anything.
- **CI:** contract drift, ajv and .NET validator tests over new fixtures in
  `fixtures/`; Core unit tests for aggregation, collision merge, rounding,
  sort, over-cap policy and the scheduler; recipes normalisation tests (id
  fallback, formula check); the visibility check over the new members
  (`Inventory..ctor`, `Inventory.Load`, `ZDOMan.GetAllZDOsWithPrefabIterative`,
  `ZDOMan.GetZDO`, `ZNetScene.GetPrefab`, `ZDO.GetByteArray`).
- **Mock:** `tools/mock-ingest` gains the two kinds after the contract pin
  refresh.
- **Operator checklist,** new numbered checks with exact log lines:
  - at start: `RLVL: chest scan on, every 10 min, N prefabs (M vehicles), excluded: incinerator` or `RLVL: chest scan off (Chests.Enabled = false)`;
  - per scan: `RLVL: chest scan: C containers (V vehicles), S stacks, X dropped, D duplicates removed, Z gone mid-scan, F unreadable, O omitted, in T ms over K ticks`;
  - WARN once per scan when a cap or clamp applied (§1.8); after three consecutive scans with unreadable chests, one WARN per hour;
  - `RLVL: recipes export waiting: no connected admin client advertises recipes`, once per hour while true;
  - recipes: `RLVL: recipes export: R recipes, P pieces, K stations, X processors, Y processes, custom formula: C`.

## 8. Rulings and open questions

**Operator rulings, 2026-10-19 and 2026-10-20:**

1. **Visibility: everything is visible.** Base totals, per-chest contents and
   chest positions on the map are shown to everyone who can see the server's
   hub.
2. **Personal chests stay with their owner and admins.** `privacy: private`
   chests are left out of the shared totals, chest list, clusters and map; the
   owner sees their own, admins see all (§3.2).
3. **No opt-out handling.** Privacy is not a concern for this data;
   `Client.OptOut` keeps covering only what a client sends, and the chest scan
   does not consult it. (A player who never runs the client mod has no Raid
   Ledger profile, so only admins see that player's personal chests.)
4. **Cadence: every 10 minutes** (§1.7).
5. **Recipes are included, with processing steps** (smelting, kiln, windmill,
   spinning wheel, refinery, fermenting, cooking, obliterator) in the first
   `recipes` story (§2.2, §5).
6. **No flags: everything is on by default** (2026-10-20, "I don't want to ever
   have to set a flag, that should be default"). `Chests.Enabled` defaults to
   `true` in the published mod; settings exist only to switch something off or
   tune it (§0 decision 10).

Still open for the operator (proposed default in bold; the work proceeds on
the default unless overruled):

7. **Vehicles.** **Count carts and ships, flagged, with a dashboard toggle.**
8. **Bases.** **Auto-cluster shared chests within 64 m; admins name them by
   placing an anchor.** Or use wards as base boundaries (would need ward
   positions and radii in the message)?
9. **Cheated items** (1.0.20 flags items spawned with dev commands). **Count
   them like any other.** Exclude instead?
10. **Planner scope.** **Show every recipe; per-character "not yet discovered"
    is a filter, not a hide.**
11. **Which claims own a personal chest.** **Steam-matched or admin-assigned
    claims, and manual claims by the profile's own verified Steam account; a
    playerId seen under two Steam ids goes to admins only.** A manual "claim
    this character" needs no Steam check, so counting every manual claim would
    let any member read the personal chests of an unclaimed character. Allow
    all manual claims anyway?
12. **Operators.** Raid Ledger has an `operator` role between member and admin.
    **Personal chests are admin-only, like every other Valheim admin view**
    (and impersonated sessions see none). Include operators instead?
13. **Obliterator.** **Excluded from totals** (`Chests.ExcludePrefabs =
    incinerator`): what sits in it is about to be destroyed, though
    uncollected output (coal) is real stock. Include it, flagged, instead?
14. **Shopping list.** **Per browser for now.** The request says "plan out our
    next material spend", which may mean one shared list per server; that is a
    small extra story (a table and two routes).

Raid Ledger lead items, **ruled by the operator 2026-10-20** (relayed by the
Raid Ledger Lead; stories filed in Backlog, related to ROK-1829/1833/1835):

15. **A new message `kind` is additive** and stays v1 under CONTRACT §9. The
    mod sends `containers` / `recipes` only after Raid Ledger ships the
    contract and ingest for that kind and the mod re-pins.
16. **Per-array caps** (e.g. `containers` ≤ 50 000 entries) **plus a contract
    CI test per kind** that builds the largest schema-valid message and
    asserts it fits its byte cap. Mandatory gzip for `recipes` only if the
    measured worst case still exceeds its cap.
17. **Real bug, ROK-1855 (High):** raise `VALHEIM_LINK_ITEMS_MAX_BYTES` to
    12 MiB (an additive relaxation) after reproducing the 11.05 MB figure with
    the new largest-message test; CONTRACT §7 updated in the same PR. The mod
    re-pins after it ships. **Shipped** (Raid Ledger `c6e45bf`, 2026-10-20;
    pinned in mod 0.2.2).
18. **`recipes` carries raw requirement fields** plus a documented
    per-quality formula; Raid Ledger computes per-quality amounts.
19. **No history in the first dashboard**: latest snapshot only.
20. **Separate story ROK-1856**: the existing armory read routes get the
    hidden-hub check.

Stories: ROK-1853 `containers` (with visibility rulings 1–6, to be confirmed
in its spec), ROK-1854 `recipes` + planner, ROK-1855 `items` cap, ROK-1856
hidden-hub check on armory routes.
