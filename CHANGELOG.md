# Changelog

## 1.0.4

- Stops are shown the way players count them: "stop 1 of 7" at the start, "stop 7 of 7" at the last one.
- `.cv r` tells apart a route whose cart was destroyed and only the animal walks on.
- `.cv rm <number>`: map marker on the point of a route from `.cv r`.
- A cart at the last stop of its route, or a cart left standing without its animal, is no longer listed or shown on the map.
- A start clears the respawn time the game scheduled after the previous squad was destroyed, so the new squad doesn't wait up to 45 minutes for it.
- The server log shows the `Source` setting at load and every change made with `.cv source`.
- A route left mid-way by an earlier caravan is put back to the first stop of its path before the mod starts it, so the caravan drives the whole way. Setting `ResetToStart`, on by default.
- Auto-start takes routes at their last stop before routes left mid-way: the game returns those to the start by itself.
- A route whose start failed (no squad within `SpawnTimeoutMinutes`) is skipped by auto-start for an hour.
- The server log tells where the squad of a started caravan appeared, and warns if a route put back to the start set out from somewhere else.

## 1.0.2

- New setting `Source` and command `.cv source`: caravans only from the mod (the game no longer spawns them when a player comes near a route), only from the game (auto-start off), or both. With `Mod`, a warning goes to the log if the game still spawns a caravan on a blocked route.
- Auto-start prefers routes standing at the start of their path. A route left mid-way by an earlier caravan is used only when no route stands at the start, and then the one with the most stops left.
- `.cv l`, `.cv r` and `.cv ss` show how far along its path a caravan is. `.cv start` says when a route stands mid-way.
- A started caravan whose cart is destroyed no longer counts in the auto-start limits; the horse still walks to the end of the route, where the game removes it.
- When a start ends by timeout or `.cv stop`, units still alive may freeze without players again, as all units of the game do.
- A robbed caravan's horse frozen mid-route no longer keeps its route busy for auto-start.
- Safety limit `MaxSquadSize`: extra units of a squad started by the mod are removed at once.
- New settings: `IconOnLoot`, `IconOnFrozen`, `AnnounceTo`, `AnnounceRadius`, `AnnounceVanilla`, `RoutePick`, `ExcludedRoutes`, `SpawnTimeoutMinutes`, `MaxRunMinutes`, `MaxSquadSize`, `VerboseLog`.
- The server log records every caravan that disappears from the map, with its last state and what its route is doing, and lists the caravans found at server start. Routine lines moved behind `VerboseLog`.
- The route list is kept between checks instead of being collected from the world every time; `RouteActs` is parsed only when it changes.

## 1.0.1

- Fixed killed guards of a started caravan coming back over and over, which made the server run out of memory: replacements are removed, and the respawn time computed during the start is cleared.
- Routes left with start settings after a crash get their normal settings back on server start.
- A started caravan is checked once a second instead of every frame.
- Starting a route that stands at its last stop no longer ends right away: the mod waits until the game clears the old squad and returns the route to the start.
- A cart standing at the last stop of its route is no longer announced as setting out.
- Removed the horse icon preset: the game shows that icon only to the owner of the mount.

## 1.0.0

First public release.

- Caravans, frozen caravans and loot containers on the map, visible to every player on the whole map.
- Two-line chat announcement with distance, direction and the marker command for each player.
- Auto-start by act: per-act interval, MaxPerAct and MaxTotal limits, AllOnlineActs or Leader scope.
- Player act by V Blood drunk, route act by guard level, manual override with RouteActs.
- English and Russian chat messages.
- Map icon presets: merchant, unknown, vblood, or any icon number.
- Developer commands moved behind EnableDiagnostics.
