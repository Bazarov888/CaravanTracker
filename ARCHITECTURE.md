# How CaravanTracker works

A server-side BepInEx (IL2CPP) plugin for V Rising. Nothing is installed on clients: everything players see
(map icons, chat lines) is made of the game's own entities and messages.

This page is for reviewers. It describes the game mechanics the mod relies on, which of them were seen on a
live server and which are assumptions, and where in the code each part lives.

## Layers

The mod is split so that every decision can be tested without the game:

1. **Reading the game** (`Routes.Snap`, `Caravans.FastScan` and friends): turns entities into plain records
   (`RouteSnap`, `Progress`). No decisions here.
2. **Decisions** (`Logic/`): pure functions over those records. No game types, no Unity, no BepInEx.
   `RouteLogic` (is a route free, which one to start), `RunLogic` (a forced start second by second),
   `GoneLogic` (why a caravan disappeared), `SpawnRange` (what range and timers a route gets),
   `CaravanRules` (what is a caravan, what is announced), `ActLogic` (acts).
3. **Acting on the game** (`Starter`, `StarterSource`, `StarterRepair`, `Icons`, `Tracker`): carry the decisions out.

`tests/` is a console program that runs the decision layer against cases taken from the server logs
(a caravan started mid-way, a caravan without guards, a cart left without its animal, a caravan that reached
the end between two checks, and so on). CI runs it before the build; a failing case fails the build.
A change of behaviour starts with a test case.

## Files

| File | What it does |
|---|---|
| `Logic/*.cs` | The decision layer (above). |
| `tests/` | Tests of the decision layer: `dotnet run --project tests`. |
| `Plugin.cs` | Entry point. `Ticker` runs every server frame and schedules all work (see Timing). |
| `Settings.cs` | BepInEx config entries. |
| `Caravans.cs` | Finding carts, horses and loot containers in the world; reading game components by name. |
| `Routes.cs` | Caravan routes (game patrols): list, squad, carts, progress along the path, `Snap`, state text. |
| `Tracker.cs` | Every check: search, announcements in chat, map icons, log of caravans that disappeared. |
| `Icons.cs` | Map icons on caravans (`Icons`) and on separate markers (`Proxies`). |
| `AutoStart.cs` | Which route to start in which act, and when. |
| `Starter.cs` | A forced start of one route and looking after it until it ends. |
| `StarterSource.cs` | `Source = Mod`: keeping the game from spawning caravans by itself. |
| `StarterRepair.cs` | Giving routes the game's settings back after a crash, and `.cv cleanup`. |
| `Acts.cs` | Act of a player (by V Blood drunk) and of a route (by guard level). |
| `Commands.cs` | Chat commands (VampireCommandFramework). |
| `Loc.cs` | Chat texts in English and Russian. |
| `Diag.cs` | Developer commands `.cvdiag`, registered only with `EnableDiagnostics = true`. |

## Game model

A caravan route is a patrol entity with `GlobalPatrolState`, `MovePatrolState` and `UnitCompositionSpawner`.
Its squad is a horse `CHAR_CarriageHorse_<Route>`, guards, and a cart `CHAR_Carriage_<Route>` the horse pulls.
When the cart is destroyed its loot stays in a container `TM_CarriageContainer_<Route>`.

Observed on a live server (game build with BepInEx 6.0.0-be.733):

- The path is the `PatrolBusStopNode` buffer of the patrol. `CurrentBusStopIndex` is 0-based; `Count - 1` is the last stop.
- The game spawns a squad at the stop the route stands at, and only when a player is within `PlayerRange` of the patrol.
- At the last stop the route goes to `WaitForDespawn`, the squad is removed and the route goes back to index 0.
- A squad comes out in parts: the horse with the cart first, the guards up to about a minute later.
- Units without players near get `DisabledDueToNoPlayersInRange` and are moved to about (10000, 10000).
  `DisableWhenNoPlayersInRange` is the tag that allows this.
- After a squad is destroyed the game schedules `RespawnTime` up to 45 minutes ahead.
- Killing a guard of a squad whose spawner timers are 0 makes the game send a replacement at once.

Not verified, treated as assumptions:

- `PlayerRange = (0, 0.001)` keeps the game from spawning (`Source = Mod`). The range is kept above 0 on purpose,
  in case 0 means "no limit". If a squad appears on a blocked route anyway, a warning goes to the log once per route.
- Writing `CurrentBusStopIndex` / `TargetBusStopIndex` and `MovePatrolState.Status = WaitingForPath` puts a route
  back to its first stop (`ResetToStart`). The log tells where the squad then appeared and warns if it was not the first stop.
- Spawner settings changed by the mod are kept in the server save. That is why `RepairLeftovers` runs on the first
  check after the server starts and why `.cv cleanup` exists.

## Timing

`Ticker.Update` runs every frame:

- `Starter.Update` while a start is running; the work itself once a second.
- `Starter.ApplySource` every 5 seconds.
- `Tracker.Tick` and `AutoStart.Tick` every `CheckSeconds` (30 by default).

All world access is on the main thread. Each step has its own try/catch, so an error in one doesn't stop the others.

## A forced start (`Starter.Start`, `Starter.UpdateRun`)

1. A squad still on the route is destroyed together with its carts.
2. With `ResetToStart`, a route standing mid-way is put back to its first stop.
3. The spawner gets `PlayerRange = (0, 100000)`, zero respawn timers and no `RespawnTime`; the old values are saved in the run.
4. The patrol loses `DisableWhenNoPlayersInRange` and is enabled.

Then once a second `RunLogic.Squad` and `RunLogic.Outcome` decide, and `UpdateRun` carries it out:

- The first unit seen (not at `WaitForDespawn`) marks the squad as spawned.
- Respawn guard: every unit that appears while the squad assembles is added to `Known`. Once a known unit is gone,
  any new unit is a replacement and is destroyed. Units above `MaxSquadSize` are destroyed too.
- The start settings stay until the squad hasn't grown for 90 seconds (at most 4 minutes after the first unit),
  then `RestoreSpawner` puts the saved spawner values back. A blocked or zero range is restored as the game's (0, 90).
- Every unit and cart of the squad loses `DisableWhenNoPlayersInRange` and is woken up if the game froze it.
- The run ends when no unit appeared within `SpawnTimeoutMinutes` (the route is then skipped by auto-start for
  an hour), when the squad is gone, or after `MaxRunMinutes`. `Finish` restores the spawner, gives the freeze tag
  back to units still alive and, with `Source = Mod`, blocks the route again if it waits for a new squad.

Runs live only in memory. After a restart in the middle of a start, `RepairLeftovers` finds routes with start
settings (range above 1000, zero timers, no freeze tag) and gives them the game's values.

## Source

- `Both`: the game and the mod start caravans.
- `Game`: auto-start is off; `.cv start` still works.
- `Mod`: `ApplySource` gives every route that waits for a squad (`SpawnerState = WaitingForSpawn`) and isn't
  being started by the mod the blocked range, and every other route the game's range back. With `Mod` and
  `IntervalMinutes = 0` there are no caravans at all; the load log and `.cv auto` warn about it.

## Tracking and icons (`Tracker.Tick`)

- A fast search every check, a full pass over networked entities every `SafetyScanMinutes` as a safety net.
- A caravan is a cart on the map pulled by its horse (horse exists, on the map, within 30 m), or a container with loot.
  Carts at `WaitForDespawn` and carts no horse pulls are skipped.
- A cart is announced once, the first time it is seen on the map after the first check. Frozen carts are not announced.
- When a caravan disappears, `GoneLogic` names the reason from what the route does now. Checks are 30 s apart,
  so a caravan that reached the end is usually last seen one stop before it; the route back at its first stop
  with no units tells it apart from a robbed one, whose route stays where it was robbed.
- Icons: in `direct` mode the icon entity targets the cart; frozen carts and `proxy` mode use a marker entity
  that follows the caravan. `Icons.Sync` touches only icons the mod could have made: on a caravan, icons of the
  current kind, `OldIcons` or a named preset; off a caravan, only an icon of the current or an `OldIcons` kind
  whose object is gone. Icons on marker objects (`Attach`), where the game keeps its own icons, are never touched.

## Removing the mod (`.cv cleanup`)

Stops all runs, opens blocked, zero or start ranges, restores zero timers, gives the freeze tag back to patrols,
units and carts, and makes the next `Tracker.Tick` remove every icon and marker of the mod. A `Paused` flag then
keeps `Start`, auto-start and `ApplySource` from changing the world until the server restarts.

## Things worth a careful look

- `ResetToStart` writes patrol state the game normally owns. If the game keeps other state about the path,
  the caravan could set out in a strange way. Logged on every start.
- The respawn guard destroys units the game spawns. If the game ever spawns guards later than the 90-second
  window, they count as squad members, not replacements; if it replaces a unit that was never seen, the guard misses it.
- Fields of `UnitCompositionSpawner` are set by reflection on a boxed copy (`SetField`); a renamed field only logs a warning.
- `Caravans.Field` / `GameTypes.Find` read some game types by name, because their namespace differs between game builds.
