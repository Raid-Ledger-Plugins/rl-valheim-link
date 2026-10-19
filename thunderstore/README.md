# RL Valheim Link

Links this Valheim server to the group's **Raid Ledger** site: on every profile save (the server's
save broadcast, logout, quit) your client sends a summary of your character (equipment, inventory
including Equipment & Quick Slots rows, skills, trophies, known items and recipes) and your explored
map with pins to the **game server**, which uploads it to Raid Ledger with the server's own token.
Once per game version one client also exports the item catalogue (names + icons from the installed
game, so modded items work).

Nothing on your PC talks to the internet: only the dedicated server holds a Raid Ledger token.

## Install

Part of the **GamerSaloon** modpack. Standalone: install `RaidLedger-ValheimLink` with r2modman /
Thunderstore Mod Manager; needs BepInExPack and JsonDotNET (pulled in automatically). The **server**
must run it too.

## Opt out

`BepInEx/config/RaidLedger.ValheimLink.cfg` → `[Client] OptOut = true`. Then this game sends
nothing at all. `SendCharacter` / `SendMap` turn off one kind.

## Console (F5)

```
rlvl status          what the mod is doing
rlvl export now      capture character + map now
rlvl export items    export the item catalogue (admins; takes a few seconds)
```

## Server setup (operator)

In the server's `BepInEx/config/RaidLedger.ValheimLink.cfg` set `[RaidLedger] BaseUrl`,
`ServerToken` (from Raid Ledger admin, for this server only) and `ServerId`. The token is never
written to any log.

## Privacy

The server receives: your SteamID64 (from the connection, not from the client), character id and
name, equipment, inventory, skills, trophies, known items and recipes, explored-map bitmaps, map
pins (type, position, label), and sends them to the operator's Raid Ledger. Pin labels are never
logged. Source and the exact wire format: https://github.com/Raid-Ledger-Plugins/rl-valheim-link
