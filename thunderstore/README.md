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

1. Install the mod on the **dedicated server** too (`BepInEx/plugins/RlValheimLink/`, three DLLs).
   Server and clients need the same RPC protocol, not the same version: a server update never forces a
   modpack update, and the server log says which client version joined.
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
5. **Test world on the same server:** if you sometimes boot this server into another world (a test
   world, an event world), set `[Server] WorldAllowlist` to your production world's name, e.g.
   `WorldAllowlist = Midgard` (several: comma-separated; case does not matter). On any other world the
   link stays idle — the log says `RLVL: world 'x' not in Server.WorldAllowlist; link idle`, joining
   clients are told the world is not linked and stop sending (clients older than 0.2.1 keep sending and
   the server drops it), and nothing reaches Raid Ledger. Without
   it, a character played on the test world becomes that character's latest snapshot on the site.

## Operations (operator)

- **Token rotated or rejected:** uploads halt with `RLVL: uploads HALTED: 401 …` and retry every 10
  minutes. Paste the new token into the config; it is picked up on the next retry, no restart needed.
- **Raid Ledger instance rebuilt, moved, or server id changed:** nothing to do. The server notices that
  its upload target changed and re-sends the base map and the item catalogue by itself; players'
  character and map data re-send on their next save.
- **Switching worlds:** the base map is rendered once per world and game version (about 20 s of server
  ticks on first load) and cached under `BepInEx/RLValheimLink/spool/basemap/`.
- **Chest diagnostic (on by default):** two minutes after every start the server logs
  `RLVL: chestdump` lines about every chest (counts, positions, item names, creator ids). It uploads
  nothing, and its first run can cause one short hitch. `[Chests] Dump = false` skips it.
- `rlvl status` in a client's F5 console (launch option `-console`) prints what the mod is doing.

## Privacy

The server receives: your SteamID64 (from the connection, not from the client), character id and
name, equipment, inventory, skills, trophies, known items and recipes, explored-map bitmaps, map
pins (type, position, label), and sends them to the operator's Raid Ledger. Pin labels are never
logged. Source and the exact wire format: https://github.com/Raid-Ledger-Plugins/rl-valheim-link
