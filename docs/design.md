# RL Valheim Link — design (ROK-1834)

Status: **draft for operator review**. The client half, the contract library,
the mock receiver and CI are built against this document. **The server half
(receive → dedupe → upload) is not built until the open questions in §9 are
answered**, per the working method.

Reader's contract: `contract/v1/CONTRACT.md` is the wire format and wins over
anything here. This document says how the mod reads the game and moves bytes
to the server; it never restates or reinterprets a format rule.

## 1. Shape

One assembly, `RlValheimLink.dll` (BepInEx 5, `net472`), loaded by the player
client **and** the dedicated server. Plus `RlValheimLink.Contract.dll`
(`netstandard2.0`, no Unity or game references) holding the message model,
the JSON writer, the pre-send validator, the bitset/gzip/base64 encoder and a
PNG encoder, so the parts that must agree with the contract are unit-tested
on plain .NET in CI without any game assembly.

```
player client                               dedicated server
──────────────                              ────────────────
Harmony postfix Game.SavePlayerProfile  ──►  ZRoutedRpc "RLVL_Chunk" handler
  CharacterCapture → message JSON              reassemble transfer
  MapCapture       → message JSON              inflate, parse, ENRICH (§4)
  ItemsCapture     → message JSON + atlas      validate (same validator)
  (opt-out config short-circuits all)           dedupe by key + capturedAt (§5)
  gzip → chunk → send (§3)                      spool to disk
                                               Uploader state machine (§6)
                                                 PUT blob, POST message
                                                 → Raid Ledger (token from config)
```

Roles are decided once `ZNet` exists: `ZNet.instance.IsServer()` turns the
server half on; a non-dedicated host (`!IsDedicated()`) runs both halves and
its own captures loop back through `ZRoutedRpc` to itself.

## 2. What is captured, when, by whom

| kind | Produced by | Trigger | Dedupe key (server) | Skipped when |
|---|---|---|---|---|
| `character` | client | every `Game.SavePlayerProfile` (periodic save broadcast, logout, quit) | (`steamId`, `playerId`) | opt-out; no local player; sent < 30 s ago |
| `map` | client | same trigger, **only if** the explored bitsets or pin list changed since the last send (SHA-256 of `m_explored`, `m_exploredOthers`, pins) | (`steamId`, `playerId`, `worldUid`) | opt-out; unchanged; sent < 5 min ago |
| `items` | **client**, on server request | server asks on `hello` when it has no export for (`gameVersion`, mod-set hash) | (`gameVersion`) | client is not allowed to export (§9 Q3); another export in flight |
| base map PNG (binary for `map.baseMap`) | **server** | world loaded; once per (`worldUid`, `gameVersion`); cached on disk | — | already rendered |

Why the items export runs on a client: sprite textures are not CPU-readable,
so icons are copied through a `RenderTexture`. A dedicated server runs
`-batchmode -nographics` and has no GPU path, so it cannot render an atlas.
Names alone could be exported server-side, but the contract makes `atlas`
mandatory when any icon is an atlas icon, so one client does the whole export.

Game reads (all on the main thread, inside the postfix, no coroutines):

- Identity: `Player.m_localPlayer.GetPlayerID()`, `.GetPlayerName()`,
  `ZNet.instance.GetWorldUID()`, `.GetWorldName()`, `Version.CurrentVersion`
  (`GameVersion.ToString()` gives `0.221.4`), the Steam id as a **claim**
  from `SteamUser.GetSteamID()` (the server replaces it, §4).
- Equipment: `Humanoid.m_helmetItem/m_chestItem/m_legItem/m_shoulderItem/
  m_utilityItem/m_trinketItem/m_rightItem/m_leftItem/m_ammoItem` →
  `head chest legs shoulder utility trinket rightHand leftHand ammo`.
  Hidden items are not a slot.
- Inventory: `Inventory.GetAllItems()`, `GetWidth()/GetHeight()`,
  `ItemData.m_gridPos/m_stack/m_quality/m_variant/m_durability/m_equipped`,
  `GetMaxDurability()`; `durability` and `maxDurability` are both `null`
  unless `m_shared.m_useDurability`. Prefab id = `m_dropPrefab.name`,
  falling back to `ObjectDB.instance.GetItemPrefab(m_shared).name`.
  `extraRows`: every row `y ≥ 4` gets a purpose from the inventory mod that
  is loaded (`BepInEx.Bootstrap.Chainloader.PluginInfos`): EquipmentAndQuickSlots
  (`randyknapp.mods.equipmentandquickslots`) is asked through reflection
  whether cell `(0, y)` is an equipment or quick slot (UNVERIFIED method
  names; falls back to `other`); AzuExtendedPlayerInventory rows are
  `extended`; anything else is `other`.
- Skills: `Skills.m_skillData` → `id = SkillType.ToString()`, `m_level`,
  `m_accumulator`.
- Trophies / known: `Player.m_trophies`, `m_knownMaterial`, `m_knownRecipes`
  (HashSets, sorted ordinal before emit so output is stable).
- Map: `Minimap.instance.m_textureSize`, `m_pixelSize`, `m_explored`,
  `m_exploredOthers` (bool[], index `py * size + px`, `py = 0` south — the
  same layout the contract's bitset uses, so packing is a straight loop),
  `m_pins` where `m_save` is true (transient Player/Ping/Shout/event pins are
  not saved and not sent); `shared = m_ownerID != 0` (UNVERIFIED: vanilla sets
  `m_ownerID` on pins that arrived from a cartography table).
- Items: `ObjectDB.instance.m_items` → `ItemDrop.m_itemData.m_shared`:
  `Localization.instance.Localize(m_name)` for `name`, `m_itemType.ToString()`
  for `category`, `m_maxStackSize`, `m_maxQuality`, `m_variants`,
  `m_icons[0]` for the icon. Locale = `Localization.instance.GetSelectedLanguage()`.

Over-cap policy (the contract's array caps bind and cannot be changed here):
lists are sorted, truncated to the cap and a warning is logged with the count
dropped. Pins beyond 2000 keep the 2000 nearest the player's current position.

## 3. RPC transport

A hand-rolled chunked transfer over the vanilla routed RPC, no Jotunn
dependency (§9 Q1):

```
ZRoutedRpc "RLVL_Chunk"(long sender, ZPackage pkg)
  pkg: int   protocol      = 1
       int   transferId    (client-local counter)
       byte  kind          0 hello, 1 character, 2 map, 3 items, 4 blob
       int   totalChunks
       int   chunkIndex
       int   totalBytes    (of the whole payload, for pre-allocation and caps)
       byte[] bytes        (≤ 32 KiB)
ZRoutedRpc "RLVL_Control"(long sender, ZPackage pkg)   server → client
  pkg: int protocol = 1; string op ("needItems" | "ack" | "reject"); string arg
```

- Payload of kinds 1–3 is the **gzip of the UTF-8 message JSON**. The server
  inflates with an output cap equal to the kind's §7 body cap. Kind 4 is a
  raw PNG (≤ 16 MiB) preceded by nothing; its sha256 is the transfer's
  identity.
- 32 KiB chunks stay far under the 512 KiB Steam reliable-message limit and
  the client sends at most 8 chunks per frame while
  `socket.GetSendQueueSize() < 256 KiB`, so a map never stalls gameplay
  traffic.
- Server reassembly: one open transfer per (sender, transferId), dropped
  after 60 s without a chunk, at most 4 open transfers and 8 transfers per
  minute per sender, `totalBytes` must be ≤ the kind's cap (+ gzip never
  expands past the cap on inflate). Anything over is dropped with one log
  line; nothing is ever sent back to the client except `RLVL_Control`.
- `hello` (kind 0) is sent once per session after the local player spawns:
  `{ modVersion, gameVersion, modSetHash }`. The server answers `needItems`
  when it has no items export for (`gameVersion`, `modSetHash`) and the
  client is allowed to export (§9 Q3).

## 4. Server enrichment and trust

The client's JSON is parsed on the server into a `JObject` and these fields
are set by the server before validation, so the client never has to know the
Raid Ledger server id and can never spoof identity:

| Field | Server rule |
|---|---|
| `serverId` | from config `ServerId` (the client sends none) |
| `character.steamId` | **replaced** by the sender peer's socket identity: `ZNetPeer.m_socket.GetHostName()` (`ZSteamSocket` gives the SteamID64 digits). A non-Steam identity (PlayFab/crossplay) is **rejected** unless `TrustClientSteamId=true` (§9 Q2). For a non-dedicated host's own captures the local `SteamUser.GetSteamID()` is used. |
| `character.playerId` | must equal `ZDOMan.instance.GetZDO(peer.m_characterID).GetLong(ZDOVars.s_playerID)`; mismatch → rejected and logged |
| `capturedAt` | client clock; **replaced by the server's clock when it differs by more than 60 s** (logged once per peer), so a skewed client never trips the contract's +5 min rule or pins "latest" wrongly |
| `modVersion` | must equal the server's own mod version (same pack); mismatch → rejected with `RLVL_Control reject modVersion` |

Then the whole message runs through the **same validator** the client used,
and only a clean message is serialised (`Formatting.None`, UTF-8, no BOM) —
those bytes are what gets POSTed, and what the CI fixtures were produced by.

## 5. Dedupe

Per snapshot key (§2) the server keeps `lastAcceptedCapturedAt` and
`lastUploadedCapturedAt` in `spool/index.json`. A received message with
`capturedAt ≤ lastAcceptedCapturedAt` is dropped (debug log). A newer one
replaces any still-pending message for the key in the spool (one file per
key, atomic rename), so a burst of saves uploads only the newest. Blobs are
stored by sha256 under `spool/blobs/` and deleted once Raid Ledger has
accepted both the blob and every message that referenced it.

The spool lives under `<game dir>/BepInEx/RLValheimLink/spool/` (configurable),
survives a server restart, and holds player data (pin labels) — see §9 Q6.

## 6. Upload state machine (server)

```
            ┌─────────────────────────────────────────────────┐
            ▼                                                  │
  IDLE ──tick──► pick oldest pending (blob before its message) │
            │                                                  │
            ▼                                                  │
  SENDING: PUT /blobs/<sha256>  or  POST message (gzip body)   │
            │ 2xx ──────────────► mark uploaded, delete file ──┘
            │ 400/422 (schema)  ► drop + ERROR log (our bug or contract drift)
            │ 401/403          ► HALT: no uploads, ERROR every 10 min until config changes
            │ 404 (blob route) ► drop blob, keep message (RL may accept it blind), WARN once
            │ 409 blob exists  ► treat as 2xx
            │ 413              ► drop + ERROR (should be impossible: caps checked before)
            │ 429              ► BACKOFF: Retry-After header, else 60 s
            │ 5xx / timeout /  ► BACKOFF: 30 s · 2^n, capped at 10 min, n reset on success
            │   network error
            ▼
  BACKOFF ──timer──► IDLE
```

Config: `UploadIntervalSeconds` (min gap between requests, default 5),
`MaxUploadsPerMinute` (default 20, counts blobs and messages), request
timeout 30 s for JSON and 120 s for blobs, `Content-Encoding: gzip` on by
default. HTTP goes through `System.Net.Http.HttpClient` on a thread-pool
task; results are marshalled back on `Update`. The token is read from config
once per request and **never** appears in any log line, exception message,
spool file or `RLVL_Control` payload (a dedicated test fails the build if the
logger ever sees a string containing it).

Routes are config values because ROK-1833 has not landed: `MessagesPath`
(default `/api/valheim-link/messages`) and `BlobsPath` (default
`/api/valheim-link/blobs/{sha256}`); see §9 Q8.

## 7. Size budget per kind vs CONTRACT.md §7

| kind | Typical | Worst case this mod can emit | §7 cap | Headroom |
|---|---|---|---|---|
| `character` | 15–40 KB (8×4 grid, ~20 skills, ~600 known items) | ~1.2 MB (512 stacks, 128 skills, 8192+8192+1024 ids at 96 chars) | 2 MiB | the array caps bind first (contract guarantee) |
| `map` | 2048² bitset = 512 KiB raw → 10–120 KB gzip → ×4/3 base64; two layers + pins ≈ 50–400 KB JSON (UNVERIFIED until a real capture) | 2 × 3 000 000 chars + 2000 pins ≈ 7 MB | 8 MiB | contract guarantee |
| `items` | ~2000 vanilla items × ~150 B ≈ 300 KB; the GamerSaloon mod set adds few items | 10 000 items ≈ 9.4 MB | 10 MiB | contract guarantee |
| base map PNG | 2048² RGBA, biome fills compress well: 1–4 MB | 16 MiB cap check before send; else WARN + `baseMap: null` | 16 MiB | — |
| icon atlas PNG | 64 px icons, 32 per row: 2000 icons → 2048×4032 ≈ 3–6 MB | 16 MiB check; if over, re-render at 48 px, then 32 px, then `icon: null` for the rest | 16 MiB | — |

Over the RPC the same bytes travel gzip-compressed, so a map costs the
client a few hundred KB of upload once per changed save.

## 8. Config (`BepInEx/config/RaidLedger.ValheimLink.cfg`)

| Section.Key | Default | Side | Meaning |
|---|---|---|---|
| `RaidLedger.BaseUrl` | `` | server | `https://raid.gamernight.net` (no trailing slash) |
| `RaidLedger.ServerToken` | `` | server | per-server bearer token from RL admin; never logged |
| `RaidLedger.ServerId` | `` | server | RL's id for this game server; goes into every message |
| `RaidLedger.MessagesPath` / `BlobsPath` | see §6 | server | routes, until ROK-1833 fixes them |
| `Upload.IntervalSeconds` | 5 | server | min gap between HTTP requests |
| `Upload.MaxPerMinute` | 20 | server | per-server request budget |
| `Upload.Gzip` | true | server | `Content-Encoding: gzip` on message bodies |
| `Upload.SpoolDirectory` | `BepInEx/RLValheimLink/spool` | server | pending uploads + blob cache |
| `Server.TrustClientSteamId` | false | server | accept the client's `steamId` claim for non-Steam peers (§9 Q2) |
| `Server.ItemsExportBy` | `admins` | server | who may answer `needItems`: `admins` (adminlist.txt) / `anyone` / `nobody` |
| `BaseMap.Enabled` | true | server | render the world PNG once per (world, version) |
| `BaseMap.RowsPerTick` | 8 | server | time-slicing of the render (2048 rows ≈ 5 s of ticks at 50 Hz) |
| `Client.OptOut` | false | client | **per-player opt-out**: the mod sends nothing from this client |
| `Client.SendCharacter` / `SendMap` | true | client | finer opt-outs |
| `Client.MinSecondsBetweenCharacter` / `…Map` | 30 / 300 | client | throttles |
| `Items.IconSize` | 64 | client | atlas icon edge in px |

## 9. Open questions for the operator (defaults in bold)

1. **Transport:** hand-rolled chunked routed RPC (no Jotunn dependency, §3) or
   Jotunn's `CustomRPC` (already in the pack and on the server; does slicing
   and compression for us but couples us to its API)? **Default: hand-rolled.**
2. **Identity on a crossplay server:** if the server runs with `-crossplay`,
   peers connect over PlayFab sockets and `GetHostName()` is a PlayFab id, not
   a SteamID64, so the server cannot vouch for `steamId`. Is the GamerSaloon
   server Steam-only? If not, is `TrustClientSteamId=true` acceptable (a
   modified client could then claim another Steam id)? **Default: Steam-only;
   non-Steam peers are skipped with one log line.**
3. **Who produces the items export** (needs a client with a GPU): only players
   in `adminlist.txt`, anyone with the mod, or nobody (operator triggers it
   from their own client with a console command `rlvl export items`)?
   **Default: admins, plus the console command.**
4. **`capturedAt` clock rule:** replace the client timestamp with the server
   clock when they differ by more than 60 s (§4)? **Default: yes.**
5. **Map send policy:** only when the explored bitsets or pins changed, at
   most every 5 min, on the save/logout/quit trigger (§2). **Default: yes.**
6. **Spool on disk** under `BepInEx/RLValheimLink/spool/` on the Pelican
   volume (holds pin labels and inventories until uploaded, deleted after)?
   **Default: yes; alternative is memory-only and lose pending uploads on
   restart.**
7. **Base map colours:** an approximation of the in-game minimap (biome fill
   + height shading + water), rendered by the server half at world load.
   Good enough for V7 (ROK-1837), or does V7 want a specific palette?
   **Default: in-game approximation.**
8. **Routes:** ROK-1833 has not fixed them. Assumed `POST
   <BaseUrl>/api/valheim-link/messages` and `PUT
   <BaseUrl>/api/valheim-link/blobs/<sha256>` (`Content-Type: image/png`,
   same bearer), configurable until confirmed. Can the Raid Ledger Lead
   confirm or correct these before the server half is built?
9. **Thunderstore:** the package namespace/team (`RaidLedger`?) and the mod as
   a hard dependency of the GamerSaloon pack (the server runs it too, so every
   client must have it; XPortal-style version enforcement is **not** planned —
   a client without the mod simply sends nothing). **Default: namespace
   `RaidLedger`, listed in the pack as "Recommended".**
10. **Items export cadence on mod-set change:** the server asks for a new
    export when the hash of its own BepInEx plugin GUID+version list changes.
    Client-only mods that add items (none in the pack today) would be missed
    — acceptable? **Default: yes.**

## 10. Contract change requests

None. Everything the mod reads fits v1. Things noted for the Raid Ledger side
(not changes): the pin type enum now also contains `Player`, `RandomEvent`,
`Ping`, `EventArea`, `Hildir1-3` and `None`; only saved pins are sent.
`ItemType` now has `Trinket`, `TwoHandedWeaponLeft`, `AmmoNonEquipable`,
`Attach_Atgeir`, `Fish` — all open-set in the contract, so fine.

## 11. Verification plan

- **CI, always:** contract drift check; ajv validation of `contract/v1/fixtures/*.json`
  and `fixtures/*.json` against `schema.json`; .NET tests: the C# validator
  accepts every golden fixture and rejects every `fixtures/invalid/*` with
  the `{ path, code }` from `EXPECTED.json`; bitset encoder round-trips and
  inflates to exactly `⌈w·h/8⌉` bytes; PNG encoder writes a valid IHDR; the
  mod's fixture generator reproduces `fixtures/` byte-for-byte.
- **CI, when game assemblies resolve:** `dotnet build` of the plugin against
  the `ValheimGameLibs` NuGet package. The job records in the step summary
  whether it compiled or was skipped.
- **Local:** `tools/mock-ingest` is the acceptance target until a Raid Ledger
  env with ROK-1833 exists.
- **In game:** `docs/operator-checklist.md` (numbered, with exact log lines).
