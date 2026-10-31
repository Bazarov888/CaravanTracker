# Changelog

## 1.0.8

- A route standing at its last stop is now woken before its squad may spawn, so the game first returns it to its first stop and the caravan sets out from there. Before, the squad appeared at the end of the route. If the route doesn't go back within 20 seconds, the squad spawns where it stands, as before, and the log says so.
- Auto-start takes routes at their first stop and routes at their last stop as one group, and doesn't pick the route it started last while another one is free. Before, a route that had just finished stood at its first stop again and was picked over and over, while routes at their last stop waited.
- A caravan in a fight keeps its map icon even when its animal is more than 30 m from the cart. Before, the icon vanished during the attack and the log said "cart left without its animal".
- With `Source = Mod`, routes standing at their last stop are blocked too, not only routes waiting for a new squad. A player walking near such a route wakes it, the game resets it and could spawn its own caravan before the next check.
- The log tells how far from where the route stood the squad appeared.
- Each player picks their own language with `.cv lang en` or `.cv lang ru` (no longer an admin command that switched it for everyone). Announcements, replies and start messages come in each player's language; `Language` in the config is the default for players who haven't picked one.
- A player who has just come into the world gets two lines, in English and Russian (or in their own language if they picked one): what the mod does and how to pick the language. Setting `Chat.Welcome`, on by default.
- Long replies (`.cv ss` with several started caravans, `.cv auto`, `.cv a`) are split into several lines. In Russian they could exceed the 512-byte chat limit and get cut or not arrive at all. For a route put back to the start (`ResetToStart`) it warns when the squad appeared where the route stood, i.e. the caravan first walks back to the first stop.

## 1.0.7

- Every decision of the mod (is a route free, which route to start, what to do with a started squad, why a caravan disappeared, what spawn range a route gets, what is announced) now lives in one place without game calls, and is covered by tests built from cases seen on the server. CI runs them before every build.
- The log no longer says "cart destroyed" about a caravan that reached the end of its route between two checks.
- Every log line of the mod starts with the server time. Start lines tell how many seconds the squad took to appear and to assemble, and how many minutes the run lasted.
- An auto-start log line tells which free routes there were (at the start, at the end, mid-way) and from which group the route was picked.
- `.cv ss` no longer says "cart destroyed" twice.

## 1.0.6

- `.cv cleanup` (admin): run it before removing the mod. It stops started caravans, gives every route the game's own spawn range, timers and freezing back, and removes the mod's icons and markers. Until the next restart the mod then changes nothing in the world.
- With `Source = Mod`, routes are checked for blocking every 5 seconds instead of every `CheckSeconds`, so the game has less time to spawn its own caravan next to a player.
- With `Source = Mod` and `IntervalMinutes = 0` no caravans appear at all; the server log at load and `.cv auto` now say so.
- New setting `RouteCommandsForAll` (section Chat, on by default): `false` makes `.cv r` and `.cv rm` admin-only, since they show where every route is.
- Map icons another mod puts on a caravan are no longer removed; the mod removes only icons of the kinds it uses.
- The version is written only in `CaravanTracker.csproj`; `MyPluginInfo` and the Thunderstore `manifest.json` are generated from it.
- `tools/deploy.sh` can't break when `git pull` updates the script itself, and runs a pinned `alpine/git` image.
- CI uses current versions of the GitHub actions.
- ARCHITECTURE.md describes how the mod works inside, for reviewers.

## 1.0.5

- Fixed a started caravan setting out without guards: the start settings now stay until the whole squad has appeared, not only the animal with the cart. With `Source = Mod` the squad on the road also gets the game's normal spawn range back instead of the blocked one.
- The server log tells how many units the squad of a started caravan has once it is assembled.
- With `Source = Mod`, a route whose start has ended is blocked at once, so the game can't spawn its own caravan there before the next check.
- A caravan is announced once: a cart that froze and woke up again, or was hidden for a moment, is no longer announced again.
- After a crash in the middle of a start, units and carts of the route get their freezing without players back on the next server start.
- An error in one started caravan no longer stops the others; a start that fails 10 times in a row is given up.

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
