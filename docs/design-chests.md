# Chest inventory, base materials and crafting planner — scouting design

Status: **scouting draft, 2026-10-19. Nothing here is built.** No mod code and no
contract file changes accompany this document. It proposes two new message
kinds, which per `AGENTS.md` ("Raid Ledger owns the wire format") must start as
Raid Ledger contract stories; §4 and §5 are written so they can be pasted into
Linear.

The operator's request, paraphrased: record every item in every chest on the
server; show a Raid Ledger dashboard of the base's materials (totals across all
chests); and add a search on Raid Ledger that works like a crafting table, so
players can look up what things cost and plan the next material spend against
what is in the chests.

Reader's contract: `contract/v1/CONTRACT.md` and `docs/design.md` remain the
rules for everything already shipped. Section numbers below that start with
"design §" refer to `docs/design.md`.

## 0. Recommendation in one page

| Piece | Where it runs | Trigger | New kind | Snapshot key |
|---|---|---|---|---|
| Chest scan | **dedicated server**, no client needed | postfix of `ZNet.SaveWorld` (every world save, vanilla 30 min) with a minimum interval and a change hash; admin console command | `containers` | (`serverId`, `worldUid`) |
| Recipe catalogue | **admin client**, same lease/marker flow as the items export | server sends `needRecipes` after `hello` when it has no recipes export for (`gameVersion`, mod-set hash) | `recipes` | (`serverId`, `gameVersion`) |
| Base materials dashboard | Raid Ledger web, Valheim hub tab | reads the latest `containers` snapshot + the items catalogue | — | — |
| Crafting planner | Raid Ledger web, hub tab or route | reads `recipes` + materials totals + items catalogue | — | — |

Key decisions, each argued below:

1. **Read chests straight from the server's ZDOs** (§1.1). Every placed chest is a
   ZDO in `ZDOMan` whether or not anyone is near it; its contents are the
   base64 `Inventory` package under the ZDO string key `"items"`. No client and
   no loaded zone are required.
2. **Decode with the game's own public `Inventory.Load`, not a hand parser**
   (§1.3). The package format moved from version 106 (0.221.x) to **110** in
   1.0.20 (`Version.Item.NewCheats`), and the 107–110 layouts are not readable
   from the reference assemblies we have. One short verification spike on the
   test server is the first slice (§7).
3. **Only player-built containers count** (§1.4): the prefab must be a buildable
   piece that carries a `Container` (derived at runtime from the tools' piece
   tables, so modded chests are included automatically) **and** the ZDO's
   `creator` long must be non-zero. Loot chests, tombstones, dungeon crates and
   world-generated furniture are excluded by construction.
4. **Key a container by prefab + position, not ZDO id** (§1.5). ZDO ids are
   reassigned on every world load (`ZDO.Load` → `m_uid.SetID(++ZDOID.m_loadID)`),
   so they are useless across restarts.
5. **Send per-container stacks aggregated by (prefab, quality); totals are
   computed by Raid Ledger**, not sent (§4). One source of truth, smaller body.
6. **Recipes come from `ObjectDB.m_recipes` plus the build tools' piece tables**,
   exported by an admin client alongside the items catalogue, so modded and
   ServerSync-overridden recipes appear exactly as players see them (§2).
   Upgrade costs per quality are derived from the raw requirement fields with
   the vanilla formula, and the export flags any recipe whose live
   `GetAmount(q)` disagrees with that formula (§2.3).
7. **Off by default** on the mod side (`Chests.Enabled = false`): scanning every
   chest is new collection of shared and personal data, so the operator turns
   it on per server after the privacy questions in §8 are answered.

Rough effort: mod ≈ 4–5 days after the spike; Raid Ledger ≈ 12–14 days across
contract, ingest, importer, dashboard and planner (§6).

## 1. Game side: enumerating containers on the dedicated server

All member names below were read from the reference assemblies the repo already
uses (`docs/reference-assemblies.md`): the **1.0.20 publicized reference**
(`Digitalroot.Valheim.Common.References` 1.0.20, signatures only, every method
body is `throw null`) and the **unpublicized CoffeeNova copy**
(`CoffeeNova.Valheim.ManagedReferences` 1.2214.6, which reports
`Version.CurrentVersion = 0.221.4` and has real IL). Decompiled with `ilspycmd`
into a scratch directory; nothing decompiled is committed. Where 0.221.4 IL is
the only evidence for 1.0.20 behaviour, the item is marked **UNVERIFIED** and is
confirmed by the spike (§7), not by assumption.

### 1.1 Where chests live

- `ZDOMan.instance` holds every ZDO of the world on the server, loaded or not.
  0.221.4 keeps them in `m_objectsByID` and per-sector lists
  `m_objectsBySector` (both private in real IL); 1.0.20 adds chunked saving
  (`GetSaveClonePerChunk`, `Version.World.ChunkedSave` = 40; the 1.0.20 world version is `CheatCleanSlate` = 42) but still keeps
  `m_objectsByID` / `m_objectsBySector`.
- The public way to find them is
  `ZDOMan.GetAllZDOsWithPrefabIterative(string prefab, List<ZDO> zdos, ref int index)`,
  public in 0.221.4 and present in 1.0.20. It scans 400 sectors per call and
  returns `true` when the whole world has been visited, so it is time-sliced by
  design: call it once per `Update` tick per prefab until it returns `true`.
  Cost is one pass over all ZDOs per container prefab (≈ 10–15 vanilla
  prefabs); at the operator's world size that is tens of milliseconds of CPU in
  total, spread over a few hundred ticks. If profiling says otherwise, a single
  pass over `m_objectsBySector` through a new `GameAccess` accessor is the
  fallback (one accessor, CI's `tools/visibility-check` covers it).
- Per ZDO (all public on `ZDO` in 1.0.20): `GetPrefab()` (stable hash of the
  prefab name), `GetPosition()` (`Vector3`, world metres), `GetString(int)`,
  `GetLong(int)`, `DataRevision` (bumped on every data change; usable for a
  cheap per-chest change check).

### 1.2 ZDO keys (`ZDOVars`, identical names in 0.221.4 and 1.0.20)

| `ZDOVars` field | String hashed | Type | Meaning for us |
|---|---|---|---|
| `s_items` | `"items"` | string | base64 of the `ZPackage` written by `Inventory.Save` (`Container.Save`: `zPackage.GetBase64()` → `GetZDO().Set(ZDOVars.s_items, base64)`); empty or absent = empty chest |
| `s_creator` | `"creator"` | long | `Piece.SetCreator(playerId)` on placement; **0 for anything not placed by a player** (world-gen locations, dungeons) |
| `s_addedDefaultItems` | `"addedDefaultItems"` | bool | set once a loot container has rolled its `m_defaultItems`; a further loot signal, not needed when `creator` is checked |
| `s_inUse` | `"InUse"` | int | 1 while someone has it open (vanilla). Not used: MultiUserChest changes this (§1.6) |

1.0.20 also adds `s_creatorIndex` and `Piece.m_creatorPlatformUserIDIndex`
(a platform user id for crossplay creators). Not needed: `creator` is the
`playerId` that the `character` message already carries, so Raid Ledger can
join a chest to a character without the mod sending any Steam or platform id.

### 1.3 Inventory serialisation

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

1.0.20 (signatures only): `Version.Item` now runs `… 106 Stable, 107
AbandonedDN, 108 Smaller, 109 ChunksNCheats, 110 NewCheats`, with
`Version.c_ItemDataVersion = NewCheats`. `ItemDrop.ItemData` gained
`m_cheated`; `Inventory` gained `AddItem(int prefabHash, …, bool cheated, …)`,
`LoadOld(ZPackage, Version.Item)`, `OldSave` and a `Load(ZPackage, bool)`
overload. So the 1.0.20 package very likely stores a prefab **hash** instead of
the name ("Smaller") and a cheat flag per item. We cannot read those layouts
from a stub assembly.

**Decision:** do not parse the package ourselves. On the server, per chest:

```
var inv = new Inventory("rlvl-scan", null, w, h);   // public ctor; w, h from the prefab's Container
inv.Load(new ZPackage(base64));                     // public; the game's own decoder for every version
foreach (var it in inv.GetAllItems()) …             // public; m_dropPrefab.name, m_stack, m_quality
```

`w`/`h` come from the prefab: `ZNetScene.instance.GetPrefab(hash)`
→ `GetComponentInChildren<Container>(true)` → `m_width`, `m_height` (public).
One scratch `Inventory` is reused for every chest. `Inventory.Load` resolves
items through `ObjectDB.instance.GetItemPrefab`, which is populated on a
dedicated server; an unknown prefab (a removed mod) is skipped by the game,
and the scan logs how many stacks were dropped that way.

**UNVERIFIED for 1.0.20, checked by the spike:** that `Inventory.Load` is
callable on a dedicated server without a local player (1.0.20's cheat
bookkeeping might touch `Player.m_localPlayer`); that out-of-grid positions are
not silently relocated when the scratch inventory has the prefab's size; that
the public signatures above are public in the real assembly (the
`visibility-check` CI job reports members it cannot find in the lagging
CoffeeNova copy rather than failing, so the spike confirms at runtime).

### 1.4 Which containers count (player-built only)

Two filters, both cheap:

1. **Prefab is a buildable container piece.** At server start, after
   `ObjectDB` and `ZNetScene` are ready, collect every piece prefab reachable
   from a tool: for each `ObjectDB.instance.m_items` entry whose
   `m_itemData.m_shared.m_buildPieces` (a `PieceTable`) is set, take
   `PieceTable.m_pieces`, keep those with a `Container` in children. In vanilla
   that yields `piece_chest_wood`, `piece_chest` (reinforced),
   `piece_chest_blackmetal`, `piece_chest_private` (personal),
   `piece_chest_barrel`, plus vehicles whose container sits on a child with
   `m_rootObjectOverride`: `Cart`, `Karve`, `VikingShip` and later ship
   prefabs. Modded chests registered as hammer pieces (Jotunn) are picked up
   automatically. All fields read are public in real IL.
   Config overrides: `Chests.ExtraPrefabs`, `Chests.ExcludePrefabs`
   (comma-separated prefab names).
2. **ZDO has a creator:** `zdo.GetLong(ZDOVars.s_creator) != 0`. World-generated
   pieces of a buildable prefab (a wood chest in an abandoned house) have
   creator 0 and are skipped.

Excluded by construction: `TreasureChest_*` and other loot chests, dungeon
crates, `Player_tombstone` (a `Container`, but not a buildable piece),
`CargoCrate` and loot bags. Vehicles are included but flagged, and
`Chests.IncludeVehicles` (default `true`) turns them off: carts often hold the
day's haul, ships at sea are not "the base".

Privacy per container is read from the prefab, not the ZDO: `Container.m_privacy`
(`Public`, `Private`, `Group`). Vanilla `piece_chest_private` is `Private`
(only its creator may open it, `CheckAccess` compares `Piece.GetCreator()` to
the opener's player id).

### 1.5 Identity and grouping

- **ZDO ids are not stable.** `ZDO.Load` assigns `m_uid.SetID(++ZDOID.m_loadID)`
  on every world load (0.221.4 IL), so a chest's `ZDOID` changes on each
  restart. The message therefore does not carry it.
- Raid Ledger derives a stable container key from `prefab` + position rounded
  to 0.25 m. Chests do not move; vehicles do, so a vehicle's key is only valid
  for one snapshot, which is fine for latest-wins.
- Positions use the same frame as map pins (`x` east-west, `z` north-south,
  world metres, |v| ≤ 32 768) plus `y` (height), so the dashboard can place
  chests on the existing base map and group them into bases. Grouping is a
  Raid Ledger concern (§6.3): single-link clustering with a radius such as
  64 m, named by an admin. Wards (`guard_stone`) are deliberately not exported
  in this proposal (open question §8).

### 1.6 MultiUserChest 0.6.2 (on the operator's server)

Read from its source (`github.com/MSchmoecker/No-Chest-Block`, master as of
2026-09-12, the repository the Thunderstore page links). It patches
`Container.Awake`, `RPC_RequestOpen` and `RPC_RequestStack` to hand **ZDO
ownership** to the player who opens the chest (`ZDOMan.ForceSendZDO`,
`ZDO.SetOwner`), and routes item moves through its own request/response RPCs.
It does not patch `Container.Save`/`Load`, does not write any ZDO key, and
does not change the `Inventory` format. Consequence for us: the authoritative
`items` string is written by whichever client owns the chest and reaches the
server through normal ZDO sync, so the server's copy can lag by a second or so
while a chest is in use. Irrelevant at a 30-minute cadence. `s_inUse` is not a
reliable signal under this mod, which is why we never read it.

### 1.7 When to scan

- **Trigger:** Harmony postfix on `ZNet.SaveWorld(bool sync)` (server only;
  private in 0.221.4, which Harmony patches by name like the existing
  `ZNet.Awake` hook). `Game.m_saveInterval` is 1 800 s in vanilla, so this is
  every 30 minutes plus manual `save` commands and shutdown. The postfix only
  starts a time-sliced scan; the scan runs on later `Update` ticks.
- **Throttle:** `Chests.MinMinutesBetween` (default 15). A save during a scan
  is ignored.
- **Change check:** SHA-256 over the sorted (prefab, rounded position,
  `items` string) of every counted chest. Identical to the last spooled
  snapshot → nothing is sent (debug log only). This makes an idle server cost
  one scan and zero uploads per save.
- **Manual:** console command `rlvl export chests` (admins, like
  `rlvl export items`) forces a scan and bypasses the change check.
- **Shutdown:** the final world save still triggers a scan; if the process
  exits before the upload, the spool (design §5) sends it on the next start.
- **Budget:** per tick, at most one `GetAllZDOsWithPrefabIterative` call per
  prefab while discovering, then at most `Chests.ChestsPerTick` (default 50)
  `Inventory.Load` calls. 500 chests decode in about ten ticks.
- **Upload:** the message is built server-side, goes through `Validator`,
  `Spool` (key `containers|{worldUid}`, latest wins) and the existing
  `Uploader`. One message per 15–30 minutes is negligible against the
  self-imposed 30 messages/min (design §6, §8).

### 1.8 Payload estimate (~500 chests)

Per container: a header of about 180 bytes (prefab, `x`/`y`/`z` to 0.1 m,
`creatorPlayerId`, privacy, vehicle flag, slot counts) plus one entry per
distinct (prefab, quality), about 45 bytes each (`{"prefab":"Wood","quality":1,"stack":50}`).

| Scenario | Entries per chest | JSON | gzip on the wire (≈ 8–12×) |
|---|---|---|---|
| Sorted base (one or two materials per chest) | ~4 | ~180 KB | ~20 KB |
| Typical mixed base | ~8–20 | 270–540 KB | 30–60 KB |
| Every chest a full black-metal chest of distinct items | 32 | ~810 KB | ~80 KB |

Well inside the 10 MiB body cap proposed in §4 and the 10 MiB nginx limit
(ROK-1850). The scan holds at most one decoded chest at a time.

## 2. Recipe catalogue (for the crafting planner)

### 2.1 Why an admin client, alongside the items export

The data itself (`ObjectDB.m_recipes`, piece tables, crafting stations) exists
on the dedicated server too. The export still runs on a client because:

- **piece icons** (portals, walls, stations) are sprites and need the same GPU
  path as the item atlas (design §2), and build pieces are not in the items
  catalogue;
- the client sees the crafting table **after** ServerSync-style config mods
  have applied the server's recipe overrides, i.e. exactly what players see;
- it reuses the proven lease, marker and chunked-transfer flow of the items
  export: `RLVL_Control needRecipes`, a `RecipesExports` marker keyed by
  (`gameVersion`, mod-set hash) in `spool/index.json`, `Server.ItemsExportBy`
  governs who may answer, and `rlvl export recipes` forces it.

Separate control op and marker (not folded into `needItems`) so an existing
items export does not block the first recipes export, and an older server
simply never asks. Older servers would drop the new payload kind anyway:
`LinkChannel.MaxPayloadBytes` returns 0 for an unknown kind. **No RPC protocol
bump is needed.**

### 2.2 What is read (all public fields in real 0.221.4 IL; present in 1.0.20)

- `ObjectDB.instance.m_recipes` (`List<Recipe>`): per recipe `name` (the
  ScriptableObject name, e.g. `Recipe_Bronze5`), `m_item` (result `ItemDrop`),
  `m_amount`, `m_enabled`, `m_craftingStation`, `m_repairStation`,
  `m_minStationLevel`, `m_requireOnlyOneIngredient`,
  `m_qualityResultAmountMultiplier`, `m_listSortWeight`, and new in 1.0.20
  `m_noCraftOnlyUpgrade`; `m_resources` (`Piece.Requirement[]`): `m_resItem`,
  `m_amount`, `m_amountPerLevel`, `m_extraAmountOnlyOneIngredient`,
  `m_recover`, and new in 1.0.20 `m_upgraderResource` (meaning not yet known,
  exported only once the spike reads its use).
- Build pieces: for every tool (`m_shared.m_buildPieces`), each piece's prefab,
  `Piece.m_name` (localised), `m_category`, `m_craftingStation`,
  `m_resources` (pieces have no quality), `m_icon`.
- Stations: each distinct `CraftingStation` referenced above: prefab,
  `m_name`, icon, and max level = 1 + the number of `StationExtension` pieces
  whose `m_craftingStation` is that station (`CraftingStation.GetLevel()` is
  `1 + extension count`).
- **Optional, phase 2:** processing conversions, which the planner needs to
  expand Bronze → Copper → Copper ore + coal. `Smelter.m_conversion`
  (`m_from`, `m_to`), `m_fuelItem`, `m_fuelPerProduct`, `m_secPerProduct`
  (smelter, blast furnace, kiln, windmill, spinning wheel, eitr refinery);
  `Fermenter.m_conversion` (`m_from`, `m_to`, `m_producedItems`);
  `CookingStation.m_conversion` (`m_from`, `m_to`, `m_cookTime`). Found by
  scanning `ZNetScene.instance.m_prefabs` for those components.

### 2.3 Upgrade cost per quality

From 0.221.4 IL:

```
Requirement.GetAmount(q)         = q <= 1 ? m_amount : (q - 1) * m_amountPerLevel
Recipe.GetRequiredStationLevel(q) = max(1, m_minStationLevel) + (q - 1)
Recipe.GetRequiredStation(q)      = m_craftingStation ?? (q > 1 ? m_repairStation : null)
```

Proposal: export the raw fields and let Raid Ledger apply this formula for
qualities 1…`maxQuality` (from the items catalogue). That keeps the body
bounded (precomputed per-quality tables multiply the size by up to 10). To stay
honest about mods that patch `GetAmount`, the exporter evaluates the live
`Requirement.GetAmount(q)` and `GetRequiredStationLevel(q)` for every quality
and sets `formula: "custom"` on any recipe where they disagree with the
formula (the web then shows "upgrade cost may differ in game"). Recipes with
`requireOnlyOneIngredient` list alternatives rather than a sum; the planner
treats their resources as "any one of".

### 2.4 Size

Vanilla 1.0.x has roughly 600–700 item recipes and 400–500 pieces, about 3–4
requirements each, ~120 bytes per requirement with keys: about 0.5 MB JSON
before the piece atlas. The piece atlas uses the same `AtlasBuilder`
(64 px, 32 per row): ~500 icons → 2048 × 1024, 1–2 MB PNG.

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
  A CHECK constraint lists the kinds (`'character','map','items'`), so a new kind
  needs a migration.
- A 30-second importer cron (`armory/valheim-armory-import.service.ts`,
  `apply()` switch per kind) projects snapshots into normalised tables
  (`api/src/drizzle/schema/valheim-armory.ts`: `valheim_profiles`,
  `valheim_maps`, `valheim_item_catalogs` keyed by server + game version).
- Proposed new tables (one migration for both, next number 0231):
  - `valheim_containers`: one row per (server, world, container key) with
    prefab, x/y/z, creator player id, privacy, vehicle flag, slots, used
    slots, `items` jsonb and `captured_at`; replaced wholesale per import in
    one transaction. Totals are a `GROUP BY prefab, quality` over
    `jsonb_array_elements(items)` (500 chests × 20 entries is trivial), joined
    to `valheim_item_catalogs` for name, category and icon. A materialised
    totals table is unnecessary at this size.
  - `valheim_recipe_catalogs`: modelled on `valheim_item_catalogs`: unique
    (server, game version), jsonb for recipes, pieces, stations and (later)
    conversions, plus the piece atlas sha.
- Quotas: one `containers` snapshot per world and one `recipes` snapshot per
  game version, far below the 2 000-snapshot per-server quota.

### 3.2 Read API

Reads live in `api/src/plugins/valheim/armory/read/` behind
`AuthGuard('jwt')` + `PluginActiveGuard`. Today the catalogue is only projected
for the prefabs one character references; there is **no full-catalogue
route**. New routes:

- `GET plugins/valheim/armory/servers/:serverId/catalog` (full items catalogue
  + atlas sha; needed by both pages),
- `GET …/servers/:serverId/materials` (totals; per-container breakdown and
  positions only for viewers allowed by §8),
- `GET …/servers/:serverId/recipes` (recipes, pieces, stations).

Blobs (item and piece atlases) already stream from
`…/servers/:serverId/blobs/:sha256`.

### 3.3 Web

- Valheim registers hub tabs through `registerServerHubGameTab('valheim', …)`
  (`web/src/plugins/valheim/register.ts`; seam in
  `web/src/plugins/pelican/hub/server-hub-game-tabs.ts`). Characters is order
  15, Map 20. A hub is one Pelican server, so no server picker is needed.
- **Base materials** = a new hub tab (`id: 'materials'`, order 25): search box
  and category filter (`components/ui/search-input.tsx`, `filter-entry.tsx`,
  `filter-panel.tsx`), a table of item icon, name, category, total (by
  quality), number of chests, sortable; a "where is it" drawer listing chests
  by base cluster and a toggle that drops chest markers on the existing map
  component. Item icons reuse `web/src/plugins/valheim/character/item-icon.tsx` and
  `item-tile.tsx`, but `character/use-item-icons.ts` is tied to a character and needs a
  `(serverId, items, atlas)` variant.
- **Crafting planner** = a second hub tab or a route
  (`/valheim/servers/:serverId/planner`, via `web/src/lazy-routes.ts` and
  `web/src/app-routes.tsx`):
  1. search any craftable item or piece (`combobox.tsx` over recipes ∪ pieces,
     with the items catalogue for names and icons);
  2. pick target quality (craft, or upgrade from q to q′) and quantity;
  3. the recipe tree: each requirement shows **have** (materials total) /
     **need** / **short**, with station and station level, and an expand
     control on intermediates that have their own recipe (Bronze → Copper +
     Tin) or, once conversions exist, a processing step (Copper → Copper ore
     + Coal at a smelter);
  4. a multi-item shopping list summing needs across targets, minus stock,
     kept in local storage per server (server-side lists are a later story);
  5. optional per-character view: grey out recipes the character has not
     discovered, using `knownRecipes` from the `character` snapshot (already
     stored, not yet returned by the API).
- No virtualisation library exists in the web app. ~1 000 catalogue rows render
  fine without one; adding one would be a "New pattern" line in the PR.

## 4. Linear story draft — contract: `containers` kind

**Title:** Valheim Link contract v1 (additive): `containers` message — chest
contents per world

**Context.** The RL Valheim Link mod can read every player-built container on
the dedicated server from the world's ZDOs (`docs/design-chests.md` in
`Raid-Ledger-Plugins/rl-valheim-link`). This story adds the message so the mod
may emit it; the mod changes nothing until this ships and its pin is
refreshed. Lineage: ROK-1829 (contract), ROK-1833 (ingest), ROK-1835
(importer), ROK-1834 (mod).

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
| `containers[]` | ≤ 4 096 | one per counted container, sorted by `prefab`, then `x`, `z` |
| `containers[].prefab` | prefab id | the container piece (`piece_chest`, `Cart`, …) |
| `containers[].x` / `y` / `z` | number, \|v\| ≤ 32 768 | world metres, same frame as map pins; `y` is height |
| `containers[].creatorPlayerId` | int64 string \| `null` | `Piece` creator = the `playerId` of the `character` kind |
| `containers[].privacy` | `"public"` \| `"private"` \| `"group"` | `Container.m_privacy` of the prefab |
| `containers[].vehicle` | boolean | the container belongs to a cart or ship |
| `containers[].slots` | int 1–1 024 | width × height of the container |
| `containers[].usedSlots` | int 0–`slots` | occupied cells before aggregation |
| `containers[].items[]` | ≤ 256 | stacks aggregated per (`prefab`, `quality`), unique on that pair |
| `items[].prefab` | prefab id | |
| `items[].quality` | int 1–10 | |
| `items[].stack` | int 1–1 000 000 000 | sum of stack sizes for that pair in this container (slots × max stack can exceed 10⁸) |
| `omitted` | int ≥ 0 | containers dropped by the mod's caps (0 normally); the web warns when > 0 |

Totals are **not** sent; Raid Ledger computes them from `containers[]`.
Not sent: slot positions, durability, variant, crafter, custom data, ZDO ids
(reassigned on every world load), creator names, ward data.

**Refinements (Zod, not expressible in `schema.json`; add to CONTRACT §6):**
`items` unique per (`prefab`, `quality`) within a container; `usedSlots ≤
slots`; total `items` entries across all containers ≤ 50 000.

**Cap.** `VALHEIM_LINK_CONTAINERS_MAX_BYTES = 10 485 760` (10 MiB). Worst
schema-valid body: 50 000 entries × 142 B (96-char prefab, max numbers) ≈
7.1 MB + 4 096 container headers × 300 B ≈ 1.2 MB ≈ 8.3 MB, under the cap,
honouring CONTRACT §7's "a schema-valid message never trips a body cap".
Typical: 0.2–0.6 MB JSON for ~500 chests.

**Changes:** Zod schema + union + `ValheimLinkMessageKindSchema`; cap constant;
`schema.json` regenerated; fixtures `containers-vanilla.json` (synthetic ids,
three chests, one cart) and invalid fixtures for duplicate (prefab, quality),
`usedSlots > slots`, coordinate out of range, unknown key; CONTRACT §1 key
list, new section, §6 refinements, §7 cap row; CHANGELOG entry marked
**additive** (a new kind is not listed in §9; say so in the PR body). Ingest:
cap map, snapshot key helper, kind CHECK migration. Importer and tables are a
separate story (§3.1).

**Acceptance:** golden fixture parses; each invalid fixture yields its
`{path, code}`; a 500-chest synthetic body is accepted by the ingest
integration spec; a second message with an older `capturedAt` is `stale`.

## 5. Linear story draft — contract: `recipes` kind

**Title:** Valheim Link contract v1 (additive): `recipes` message — crafting
recipes, build pieces and stations

**Endpoint.** `POST /api/plugins/valheim/link/messages/recipes`. Snapshot key
(`serverId`, `gameVersion`), latest wins, the same as `items`. Produced by an
admin client when the server asks (`needRecipes`), once per (game version, mod
set).

**Schema** (`valheim-link-recipes.schema.ts`, strict):

| Field | Type | Rule |
|---|---|---|
| `kind` | `"recipes"` | |
| `locale` | as in `items` | language of piece and station names |
| `atlas` | binary ref \| `null` | piece and station icons; required when any icon is an atlas icon (same rule as `items`) |
| `stations[]` | ≤ 256 | `{ prefab, name 1–96, maxLevel 1–20, icon }`, `prefab` unique |
| `recipes[]` | ≤ 4 096 | see below, `id` unique |
| `recipes[].id` | `^[A-Za-z0-9_.-]{1,96}$` | the Recipe asset name; the mod substitutes `<result>#<n>` when the name does not match |
| `recipes[].result` | prefab id | item crafted; name/icon from the `items` catalogue |
| `recipes[].amount` | int 1–10 000 | items produced per craft |
| `recipes[].enabled` | boolean | |
| `recipes[].station` / `repairStation` | prefab id \| `null` | references `stations[].prefab` |
| `recipes[].minStationLevel` | int 0–20 | |
| `recipes[].upgradeOnly` | boolean | `m_noCraftOnlyUpgrade` (1.0.x) |
| `recipes[].anyOneOf` | boolean | `m_requireOnlyOneIngredient`: resources are alternatives |
| `recipes[].qualityResultAmountMultiplier` | number ≥ 0 | only meaningful with `anyOneOf` |
| `recipes[].formula` | `"vanilla"` \| `"custom"` | `custom` when the live per-quality amounts differ from the documented formula |
| `recipes[].resources[]` | ≤ 16 | `{ prefab, amount 0–10 000, amountPerLevel 0–10 000, extraAmountOnlyOneIngredient 0–10 000, recover }`, `prefab` unique |
| `pieces[]` | ≤ 4 096 | `{ prefab, name 1–96, category (open set, `Piece.m_category`), tool (prefab id), station (prefab id \| null), icon, resources[] ≤ 16 of { prefab, amount, recover } }`, `prefab` unique |

Per-quality cost is derived on Raid Ledger with the formula in CONTRACT
(copied from §2.3 here). Optional later field (additive):
`conversions[] { station, from, to, fuel?, fuelPerProduct?, secondsPerProduct?, produced? }`.

**Cap.** `VALHEIM_LINK_RECIPES_MAX_BYTES = 10 485 760`. Worst case with the
array caps above: (4 096 recipes + 4 096 pieces) × 16 resources × ~150 B ≈ 19.7
MB, plus up to ~5 MB of recipe, piece and station headers (free-text names
counted at 6 bytes per escaped character, as CONTRACT §7 does). That breaks the
§7 rule. Proposal: a refinement "total resources across recipes and pieces
≤ 30 000" (≈ 4.5 MB) like `containers`, which keeps the worst case near 10 MB;
the contract author recomputes the exact figure with the fixture generator and
trims the arrays or raises the cap so §7 holds. Vanilla tops out at 5
requirements per recipe.

**Acceptance:** as for `containers`, plus: an `atlas` icon outside the atlas,
a dangling `station` reference and a duplicate `id` are rejected.

## 6. Effort (rough, one engineer, including tests)

| Work | Repo | Days |
|---|---|---|
| Spike: verify §1.3/§1.4 on the test server (console dump of counts, no upload) | mod | 0.5 |
| `containers`: scanner, model, validator, fixtures, config, console command, operator checklist lines | mod | 2–2.5 |
| `recipes`: capture coroutine, piece atlas, model, validator, `needRecipes` + marker | mod | 2 |
| Contract `containers` + ingest | RL | 1.5 |
| Contract `recipes` + ingest | RL | 1–1.5 |
| Migration, tables, importer branches, specs | RL | 2.5 |
| Base materials: full-catalogue + materials routes, hub tab, generalised icon hook, tests | RL | 3 |
| Crafting planner: recipes route, search, tree, have/need, shopping list, tests | RL | 4–5 |
| **Total** | | mod ≈ 4.5–5, RL ≈ 12–14 |

Order: spike → contract stories (RL ships first; mod pins) → mod slices
(`containers` first, it alone unblocks the dashboard) → importer → dashboard →
`recipes` → planner. Conversions and server-side shopping lists follow.

## 7. Verification plan

- **Spike (first slice, test server only, no upload):** a temporary
  `rlvl chests dump` console command that runs §1.4 + §1.3 and logs, per
  prefab, the number of chests counted, skipped (creator 0), and decoded
  stacks; plus the totals of three named materials, which the operator
  compares with an in-game look into the same chests. Confirms: `Inventory.Load`
  on a dedicated server under 1.0.20; prefab discovery; that ZDO ids change
  across a restart (log one chest's id before and after). Also reads the
  1.0.20 use of `m_upgraderResource`.
- **CI:** the usual contract-drift, ajv and .NET validator tests over new
  fixtures in `fixtures/`; a unit test for aggregation (two stacks of the same
  prefab and quality become one entry) and for the change hash; the visibility
  check over the new members (`Inventory..ctor`, `Inventory.Load`,
  `ZDOMan.GetAllZDOsWithPrefabIterative`, `ZNetScene.GetPrefab`).
- **Mock:** `tools/mock-ingest` gains the two kinds after the contract pin
  refresh.
- **Operator checklist:** new numbered checks with exact `RLVL:` log lines for
  "chest scan: N containers (M vehicles) from P prefabs, S stacks, Q dropped"
  and "recipes export: R recipes, P pieces, K stations, custom formula: C".

## 8. Open questions

For the operator (defaults in bold):

1. **Who sees what?** The ROK-1799 spike defaulted pins to owner + admins
   because they reveal bases; chest positions and contents do too. Proposal:
   **totals visible to every member of the server hub; per-chest breakdown and
   map positions to admins only.**
2. **Personal chests** (`piece_chest_private`, `privacy: private`): their
   contents are one player's, not the base's. **Default: the mod sends them
   flagged; Raid Ledger leaves them out of base totals and shows them only to
   the owner (creator's character → Steam id) and admins.** Alternative: the
   mod never reads them.
3. **Consent.** `Client.OptOut` is per player and only covers what a client
   sends; the chest scan reads the world, so an opted-out player's chests
   would still be counted. **Default: count shared chests regardless (they
   are base data), and honour opt-out by excluding a creator's personal
   chests if Raid Ledger knows the player opted out.** Needs a ruling.
4. **Which servers.** **`Chests.Enabled = false` by default; the operator
   turns it on per server.**
5. **Cadence.** **Every world save (30 min), at most every 15 min, only when
   something changed; plus `rlvl export chests`.** Is "up to 30 minutes old"
   acceptable for planning?
6. **Vehicles.** **Count carts and ships, flagged, with a dashboard toggle.**
7. **Bases.** **Auto-cluster within 64 m, admin can rename clusters.** Or use
   wards as base boundaries (would need ward positions and radii in the
   message)?
8. **Cheated items** (1.0.20 flags items spawned with dev commands). **Count
   them like any other.** Exclude instead?
9. **Planner scope.** **Show every recipe; per-character "not yet discovered"
   is a filter, not a hide.**

For the Raid Ledger lead:

10. Accept the two kinds as **additive v1** (CONTRACT §9 does not list a new
    kind explicitly)?
11. Caps: total-entry refinements (`containers` ≤ 50 000 entries, `recipes` ≤
    30 000 resources) versus tighter per-array caps.
12. Recipes: raw requirement fields + documented formula (proposed) versus
    precomputed per-quality tables.
13. History: v1 keeps only the latest snapshot. A "wood over time" chart needs a
    history table on the importer side; in or out of the first dashboard?
14. Conversions (smelting, fermenting, cooking) in the first `recipes` story or
    a follow-up?
