# RL Valheim Link

Links this Valheim server to the group's **Raid Ledger** site: on every profile save (the server's
save broadcast, logout, quit) your client sends a summary of your character (equipment, inventory
including Equipment & Quick Slots rows, skills, trophies, known items and recipes) and your explored
map with pins to the **game server**, which uploads it to Raid Ledger with the server's own token.
Once per game version one admin's client also exports the item catalogue (names + icons from the
installed game, so modded items work).

Nothing on your PC talks to the internet: only the dedicated server holds a Raid Ledger token.

## Players

Part of the **GamerSaloon** modpack; nothing to set up. Not join-enforced: without it you simply send
nothing. To **opt out**, set `[Client] OptOut = true` in `BepInEx/config/RaidLedger.ValheimLink.cfg`
(`SendCharacter` / `SendMap` turn off one kind). Nothing is sent until you have spawned, and nothing
leaves your PC except through the game server you are playing on.

## Server setup (operator)

1. Install the same version on the **dedicated server** (`BepInEx/plugins/RlValheimLink/`, three DLLs).
   The server rejects clients on a different mod version, so keep the server and the modpack in step.
2. Start it once so `BepInEx/config/RaidLedger.ValheimLink.cfg` exists, then set under `[RaidLedger]`:
   - `BaseUrl` — your Raid Ledger instance (scheme + host, no trailing slash);
   - `ServerId` — the id Raid Ledger shows next to the token (the Pelican server id, as digits);
   - `ServerToken` — the per-server token from Raid Ledger's **Pelican panel** integration page
     (press **Generate** on this server's row; it is shown once). Paste it into the file; it is never
     written to any log.
3. Restart the server. The log (`BepInEx/LogOutput.log`) shows
   `RLVL: server half active: … token=set` and, a few seconds later, `RLVL: PUT blob … → 201 stored`
   for the world's base map.
4. **Item catalogue:** the export is produced by an **admin's client** (a SteamID64 listed in the
   server's `adminlist.txt`, `Steam_7656…` format), once per game version and again when the server's
   mod set changes. The server asks the first admin who joins; nothing else is needed. To let any player
   do it, set `[Server] ItemsExportBy = anyone`.

## Operations (operator)

- **Token rotated or rejected:** uploads halt with `RLVL: uploads HALTED: 401 …` and retry every 10
  minutes. Paste the new token into the config; it is picked up on the next retry, no restart needed.
- **Raid Ledger instance rebuilt, moved, or server id changed:** nothing to do. The server notices that
  its upload target changed and re-sends the base map and the item catalogue by itself; players'
  character and map data re-send on their next save.
- **Switching worlds:** the base map is rendered once per world and game version (about 20 s of server
  ticks on first load) and cached under `BepInEx/RLValheimLink/spool/basemap/`.
- `rlvl status` in a client's F5 console (launch option `-console`) prints what the mod is doing.

## Privacy

The server receives: your SteamID64 (from the connection, not from the client), character id and
name, equipment, inventory, skills, trophies, known items and recipes, explored-map bitmaps, map
pins (type, position, label), and sends them to the operator's Raid Ledger. Pin labels are never
logged. Source and the exact wire format: https://github.com/Raid-Ledger-Plugins/rl-valheim-link
