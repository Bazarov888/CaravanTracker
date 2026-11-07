# CarriageTracker

[![build](https://github.com/Bazarov888/CarriageTracker/actions/workflows/build.yml/badge.svg)](https://github.com/Bazarov888/CarriageTracker/actions/workflows/build.yml)

Server-side mod for V Rising. Carriages (the game's caravans) become visible on the map for everyone, players get a chat announcement when a carriage sets out, and the mod can start carriages on its own in the acts where players are online.

Players don't need to install anything. Until 1.0.11 the mod was called CaravanTracker.

## What it does

- **Carriages on the map.** Every carriage on the road, every carriage frozen mid-route and every loot container left after a robbed carriage gets a map icon. The icon is visible to all players on the whole map, without discovering the area first.
- **Announcements.** When a carriage sets out, online players get two chat lines (all of them, those of the carriage's act or those nearby, see `AnnounceTo`), each in their own language. The first one names the carriage and its act, the second one gives the distance and direction from that player and the `.cv m <number>` command that puts a map marker on it.
- **Auto-start by act.** In vanilla a carriage only spawns when a player happens to be near its route. The mod starts carriages itself: in every act where players are online, at most one new carriage per interval, within the per-act and server limits. Routes standing at the start or at the end of their path go first, both set out from the first stop, so a carriage gets the whole way; the route started last is skipped while another one is free. Nobody online means nothing starts.
- **Commands** to list carriages, mark the nearest one, see all routes with their region and act, and start a carriage on a chosen route.

## How acts work

Acts are the same as in the game's V Blood menu (K).

- **A player's act** (`PlayerAct`): by default the highest act among the V Blood they have drunk, and the next act as soon as the strongest V Blood of an act is drunk (Quincey opens Act II, Octavian Act III, Cyril Act IV). `PlayerAct = Level` counts by the character's level instead, with the bounds below: with [Bloodcraft](https://thunderstore.io/c/v-rising/p/zfolmt/Bloodcraft/) leveling that is the Bloodcraft level, without it the gear level. `Highest` takes the higher of the two.
- **A route's act** is the act of its region: Farbane Act I, Dunley Act II, Silverlight and Oakveil Act IV. So the Draculin prison carriage (vampires guarded by the Church of Luminance) is an Act II carriage, the hard one of Dunley, although its guards are the same units as in Silverlight. A route the wiki doesn't know (one a game update adds) gets its act from its guards' level, with the bounds of the V Blood menu: up to 39 is Act I, 40-59 Act II, 60-65 Act III, 66 and above Act IV. Check with `.cv routes`.
- **Act III has no routes in the game**: there are no carriages in Gloomrot and the Cursed Forest. A player in an act without routes gets the next act up with routes (`EmptyAct = Higher`, the default: Act III players get the Silverlight and Oakveil carriages), the next one down (`Lower`) or one of each (`Both`).
- **`RouteActs`** sets acts by hand, for a route or a whole region, one act or several joined by `+`. For example `Silverlight=3+4` gives the Silverlight carriages to Act III and Act IV players, and `Silverlight=3+4,Oakveil=4` keeps Oakveil for Act IV only.

Regions and routes, after the [wiki](https://wiki.v-ris.ing/w/Carriage) (route keys are the game's own spelling, `Chuch` included):

| Region | Routes |
|---|---|
| `Farbane` (Farbane Woods) | `BanditAnimal` (creature prison), `BanditCopper`, `BanditPrisoners` (villager prison), `BanditPlank`, `BanditSulfur` |
| `Dunley` (Dunley Farmlands) | `MilitiaCotton` (two routes, east and west), `MilitiaIron`, `MilitiaQuartz`, `ChurchLegion` (Draculin prison, church guards) |
| `Silverlight` (Silverlight Hills) | `ChuchSilver`, `ChurchMutants` (mutant prison), `ChuchGrape` |
| `Oakveil` (Oakveil Woodlands) | `BlackfangBastion` (emery), `BlackfangSouth` (venom sap and oak) |

The two Oakveil routes have no end: their carriage circles until it is destroyed. When its cart is destroyed, the rest of the squad circles on; once no player is near it freezes, and the next start on that route removes it.

## Commands

For everyone:

| Command | What it does |
|---|---|
| `.cv l` | Carriages on the map, nearest first |
| `.cv m [number]` | Map marker on a carriage, the nearest by default |
| `.cv r` | All routes with region, level, act and state |
| `.cv a` | Your act, how it was found, and the routes you get |
| `.cv rm <number>` | Map marker on the point of a route from `.cv r` |
| `.cv lang <en or ru>` | Your language of the mod's messages; other players keep theirs |

`.cv r` and `.cv rm` show where every route is. With `RouteCommandsForAll = false` they are for admins only.

For admins:

| Command | What it does |
|---|---|
| `.cv start <number>` | Start a carriage on a route from `.cv r` right now |
| `.cv ss` | How the started carriages are doing |
| `.cv stop` | Stop tracking started carriages and restore normal route settings |
| `.cv auto [minutes]` | Auto-start status; with a number sets the interval, `0` turns it off |
| `.cv an` | Run auto-start right now, ignoring the interval |
| `.cv source [mod, game or both]` | Who starts carriages: only the mod, only the game, or both |
| `.cv icon <name or number>` | Map icon: `merchant`, `unknown`, `vblood` or a number from `.cv icons` |
| `.cv icons [filter] [page]` | Map icons of the game with their numbers |
| `.cv mode <direct or proxy>` | Where the icon hangs: on the carriage or on a separate marker |
| `.cv stats`, `.cv refresh` | Results of the last check, or a full check right now |
| `.cv cleanup` | Before removing the mod: give routes the game's own settings back and remove the mod's icons (see Uninstalling) |

`.cv` is short for `.carriage`.

## Configuration

`BepInEx/config/CarriageTracker.cfg` is created on the first start. A `Bazarov.CaravanTracker.cfg` from the old name is moved to it.

| Section | Setting | Default | Meaning |
|---|---|---|---|
| General | Language | `en` | `en` or `ru`: default for players who haven't picked their own with `.cv lang` |
| Map | Icon | `merchant` | `merchant`, `unknown`, `vblood` or a PrefabGUID number |
| Map | OldIcons | empty | Filled by `.cv icon`: icons used before, whose leftovers are cleaned up |
| Map | GlobalIcons | `true` | Icons visible to everyone on the whole map |
| Map | IconMode | `direct` | `direct` or `proxy` |
| Map | IconOnLoot | `true` | Icon on the loot left after a robbed carriage |
| Map | LootKeepMinutes | `10` | How long the loot of a robbed carriage stays, while it has loot; `0` = the game's timers (60 s with nobody within 20 m) |
| Map | IconOnFrozen | `true` | Icon on a carriage frozen mid-route because no players are near |
| Chat | Announce | `true` | Chat announcement when a carriage sets out |
| Chat | AnnounceTo | `All` | `All`, `SameAct` (players who get carriages of that act) or `Nearby` |
| Chat | AnnounceRadius | `1500` | Meters for `Nearby` |
| Chat | AnnounceVanilla | `true` | Also announce carriages the game spawned by itself; `false` = only the mod's |
| Chat | RouteCommandsForAll | `true` | `.cv r` and `.cv rm` for every player; `false` = admins only |
| Chat | ShowOthersActs | `true` | `.cv a` also lists the act of every player online; `false` = only admins see that list (on a PvP server it tells who is how far) |
| Chat | Welcome | `true` | Two lines to a player who has just come into the world: what the mod does and how to pick the language |
| Chat | WelcomeEn, WelcomeRu | empty | Your own welcome lines instead of the mod's; color tags work, about 450 bytes at most (a Russian letter is 2 bytes) |
| Chat | TitleColor, TextColor, CommandColor | gold, white, green | Announcement colors |
| AutoStart | Source | `Both` | `Mod` = only the mod starts carriages, the game no longer spawns them near players; `Game` = only the game, auto-start off; `Both` |
| AutoStart | IntervalMinutes | `20` | Minimum time between two auto-starts in the same act, `0` = off. With `Source = Mod` and `0` there are no carriages at all; the log warns about it |
| AutoStart | Scope | `AllOnlineActs` | `AllOnlineActs` or `Leader` (only the most advanced player's act) |
| AutoStart | MaxPerAct | `2` | Carriages started by the mod on the road at once in one act |
| AutoStart | MaxTotal | `6` | The same for the whole server |
| AutoStart | RoutePick | `Random` | `Random` (any free route but the one started last) or `Rotate` (the route started longest ago) |
| AutoStart | ExcludedRoutes | empty | Routes auto-start never uses, e.g. `BanditPrisoners,ChurchLegion` |
| AutoStart | RouteActs | empty | Acts by hand for routes or regions, e.g. `Silverlight=3+4,MilitiaIron=2`; a route's own entry wins over its region's |
| AutoStart | PlayerAct | `VBlood` | `VBlood`, `Level` (character level; Bloodcraft's with its leveling) or `Highest` |
| AutoStart | EmptyAct | `Higher` | What a player in an act without routes gets: `Higher`, `Lower` or `Both` |
| Start | SpawnTimeoutMinutes | `5` | How long to wait for a started squad to appear |
| Start | ResetToStart | `true` | A route left mid-way is put back to its first stop before a start |
| Start | MaxRunMinutes | `90` | How long a started carriage is looked after |
| Start | MaxSquadSize | `20` | Safety limit: extra units of a started squad are removed |
| Timing | CheckSeconds | `30` | How often carriages are searched |
| Timing | SafetyScanMinutes | `30` | How often the whole world is searched as a safety net |
| Debug | VerboseLog | `false` | Every check and every step of started carriages in the server log |
| Debug | EnableDiagnostics | `false` | Developer commands `.cvdiag` |

Limits count only carriages started by the mod that still have their cart. Carriages that spawn the vanilla way next to players are not counted.

Each player's language picked with `.cv lang` is kept in `BepInEx/config/CarriageTracker.languages.txt`.

## Limitations

- The game has no wagon map icon, and a server mod can't send new pictures to players, so the carriage icon is one of the game's own icons. Pick the one you like with `.cv icon`.
- A started carriage keeps moving even if everybody logs off, and disappears at the end of its route as usual.
- A route carries one carriage at a time. When the cart is destroyed, the animal walks on to the end of the route, where the game removes it and returns the route to the start. Until then that route is busy; other routes are not affected.
- A carriage whose whole squad was killed leaves its route mid-way. While a player is near, the empty route drives on to its end and the game returns it to the start. If it froze mid-way, the mod puts it back to its first stop before starting it (`ResetToStart`): the carriage walks to the first stop and then drives the whole way. Auto-start takes such routes last.
- A carriage on an Oakveil route that nobody robs keeps circling after the mod stops looking after it (`MaxRunMinutes`), and that route stays busy until it is destroyed, as in vanilla.

## Installation

Download `CarriageTracker.dll` from the latest [release](https://github.com/Bazarov888/CarriageTracker/releases/latest) and put it into `BepInEx/plugins/CarriageTracker/` on the server. It needs [BepInExPack V Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/) and [VampireCommandFramework](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/). The zip next to it is the Thunderstore package. The mod isn't on Thunderstore yet; once it is, r2modman or Thunderstore Mod Manager will install it with its dependencies.

Coming from CaravanTracker: delete `BepInEx/plugins/CaravanTracker` before starting the server. While the old DLL is there, CarriageTracker writes an error to the log and does nothing, so the two never run at once. The settings and the players' languages move to the new file names by themselves.

## Uninstalling

The mod changes some route settings in the world while it runs: with `Source = Mod` it blocks the game's own spawning, a started carriage gets start settings for a while, and the icons it creates live in the world. The server save can keep these. Before removing the mod:

1. Run `.cv cleanup` in chat as admin. It stops started carriages, gives every route the game's own spawn range, timers and freezing back, and removes the mod's icons and markers. Until the next server restart the mod then changes nothing.
2. Stop the server, delete `BepInEx/plugins/CarriageTracker`, start the server.

Without the cleanup, routes blocked by `Source = Mod` would stay without carriages from the game.

## Building

```
dotnet build -c Release
```

Reference assemblies come from NuGet (nuget.org and nuget.bepinex.dev). To build against an installed server instead: `dotnet build -c Release -p:BepDir=/path/to/server/BepInEx`.

Tests of the decision logic need only the .NET 8 SDK, no game files: `dotnet run --project tests`.

The mod targets `net6.0` because BepInEx for V Rising (IL2CPP) runs on .NET 6; a newer target would not load. .NET 6 itself is out of support and gets no more security updates. That matters only for the build tools, not for the server: the mod runs inside the game's own BepInEx.

A Release build also produces the Thunderstore package `dist/CarriageTracker-<version>.zip`. The version is written only in `CarriageTracker.csproj` (`<Version>`); `MyPluginInfo` for the code and the package's `manifest.json` are generated from it at build time.

A new version in `CarriageTracker.csproj` merged to `main` gets its GitHub Release by itself (`release.yml`) and is then published to Thunderstore (`thunderstore.yml`), once the repository has the secret `THUNDERSTORE_KEY` (a Thunderstore team's service account token) and the variable `THUNDERSTORE_TEAM` (the team's name). An existing release can be published by hand: Actions → thunderstore → Run workflow, with its version.

How the mod works inside (patrols, starts, the spawn block, repair) is described in [ARCHITECTURE.md](https://github.com/Bazarov888/CarriageTracker/blob/main/ARCHITECTURE.md).

Every push and pull request is built and tested by GitHub Actions. A new version merged into `main` gets its GitHub Release with the DLL and the package by itself (`release.yml`).

On a server with Docker, `tools/deploy.sh /path/to/server` pulls the repository, builds the mod against the server's BepInEx and installs it.

## Credits

- The way to make map icons global is taken from [KindredPortals](https://thunderstore.io/c/v-rising/p/odjit/KindredPortals/) by odjit.
- How to remove a buff follows [KindredCommands](https://thunderstore.io/c/v-rising/p/odjit/KindredCommands/) and Bloodcraft.
- Act bounds follow the V Blood list on the [V Rising wiki](https://vrising.fandom.com/wiki/V_Blood_Carriers); routes and regions follow the [Carriage](https://wiki.v-ris.ing/w/Carriage) page.

License: MIT.

---

## По-русски

Серверный мод для V Rising. Караваны видны на карте всем игрокам, при выходе каравана в чат приходит анонс с расстоянием и направлением, а мод сам запускает караваны в актах, где есть игроки онлайн. Игрокам ничего ставить не нужно. До 1.0.11 мод назывался CaravanTracker.

Установка: скачай `CarriageTracker.dll` из последнего [релиза](https://github.com/Bazarov888/CarriageTracker/releases/latest) и положи в `BepInEx/plugins/CarriageTracker/` на сервере. Если стоял CaravanTracker, удали папку `BepInEx/plugins/CaravanTracker`: пока старая DLL на месте, новый мод пишет ошибку в лог и ничего не делает. Настройки и языки игроков переедут в новые файлы сами.

Язык у каждого игрока свой: `.cv lang ru` или `.cv lang en`. Кто не выбрал, получает язык из конфига (`Language`). Новому игроку при входе приходят две строки, на английском и русском: что делает мод и как выбрать язык.

Автозапуск: в каждом акте, где есть игрок онлайн, новый караван выходит не чаще раза в `IntervalMinutes` минут, пока в акте едет меньше `MaxPerAct`, а на сервере меньше `MaxTotal` караванов мода. Сначала берутся маршруты, которые стоят в начале или в конце пути, причём не тот, что мод запускал последним.

Акт игрока (`PlayerAct`) по умолчанию считается по выпитым V Blood, и следующий акт открывается сразу, как только выпит самый сильный V Blood предыдущего (Quincey, Octavian, Cyril). `Level` считает по уровню персонажа: с Bloodcraft это его уровень, без него уровень снаряжения. `Highest` берёт больший из двух. Акт маршрута считается по его региону: Farbane I, Dunley II, Silverlight и Oakveil IV. Поэтому повозка с пленными дракулинами (вампиры под охраной церкви) идёт во II акте вместе с ополчением, хотя охрана у неё та же, что в Silverlight. В III акте маршрутов в игре нет (в Gloomrot и Cursed Forest повозок нет), поэтому игрок III акта по умолчанию получает караваны IV акта, Silverlight и Oakveil (`EmptyAct = Higher`; `Lower` даст II акт, `Both` оба). Привязку можно задать руками через `RouteActs`, для маршрута или целого региона, например `Silverlight=3+4`. Проверить, что получилось: `.cv r` и `.cv a`. Убрать маршрут из автозапуска: `ExcludedRoutes`.

Кто запускает караваны, задаёт `Source` (или команда `.cv source`): `Mod` - только мод, сама игра больше не выпускает караван, когда игрок подходит к маршруту; `Game` - только игра, автозапуск выключен; `Both` - и мод, и игра.

Кому приходит анонс, задаёт `AnnounceTo`: всем (`All`), игрокам того же акта (`SameAct`) или тем, кто ближе `AnnounceRadius` метров (`Nearby`).

Команды и настройки описаны выше, в английской части.

Перед удалением мода выполни в чате `.cv cleanup` (админ): мод вернёт маршрутам настройки игры и уберёт свои иконки. Потом останови сервер, удали папку `BepInEx/plugins/CarriageTracker` и запусти сервер. Без этого маршруты, заблокированные при `Source = Mod`, останутся без караванов игры.
