# CaravanTracker

Server-side mod for V Rising. Caravans become visible on the map for everyone, players get a chat announcement when a caravan sets out, and the mod can start caravans on its own in the acts where players are online.

Players don't need to install anything.

## What it does

- **Caravans on the map.** Every caravan on the road, every caravan frozen mid-route and every loot container left after a robbed caravan gets a map icon. The icon is visible to all players on the whole map, without discovering the area first.
- **Announcements.** When a caravan sets out, every online player gets two chat lines. The first one names the caravan and its act, the second one gives the distance and direction from that player and the `.cv m <number>` command that puts a map marker on it.
- **Auto-start by act.** In vanilla a caravan only spawns when a player happens to be near its route. The mod starts caravans itself: in every act where players are online, at most one new caravan per interval, within the per-act and server limits. Routes standing at the start of their path go first, so a caravan gets the whole way. Nobody online means nothing starts.
- **Commands** to list caravans, mark the nearest one, see all routes with their act, and start a caravan on a chosen route.

## How acts work

Acts are the same as in the game's V Blood menu (K).

- **A player's act** is the highest act among the V Blood they have drunk.
- **A route's act** comes from the level of its guards, with the same bounds as the bosses in the menu: up to 39 is Act I, 40-59 Act II, 60-65 Act III, 66 and above Act IV. On a typical server the bandit routes land in Act I, militia in Act II, church and Blackfang in Acts III-IV. Check with `.cv routes` and override any route with `RouteActs` in the config.
- If an act has no routes, the nearest lower act with routes is used.

## Commands

For everyone:

| Command | What it does |
|---|---|
| `.cv l` | Caravans on the map, nearest first |
| `.cv m [number]` | Map marker on a caravan, the nearest by default |
| `.cv r` | All routes with level, act and state |
| `.cv a` | Your act and the routes of that act |
| `.cv rm <number>` | Map marker on the point of a route from `.cv r` |

`.cv r` and `.cv rm` show where every route is. With `RouteCommandsForAll = false` they are for admins only.

For admins:

| Command | What it does |
|---|---|
| `.cv start <number>` | Start a caravan on a route from `.cv r` right now |
| `.cv ss` | How the started caravans are doing |
| `.cv stop` | Stop tracking started caravans and restore normal route settings |
| `.cv auto [minutes]` | Auto-start status; with a number sets the interval, `0` turns it off |
| `.cv an` | Run auto-start right now, ignoring the interval |
| `.cv source [mod, game or both]` | Who starts caravans: only the mod, only the game, or both |
| `.cv icon <name or number>` | Map icon: `merchant`, `unknown`, `vblood` or a number from `.cv icons` |
| `.cv icons [filter] [page]` | Map icons of the game with their numbers |
| `.cv mode <direct or proxy>` | Where the icon hangs: on the caravan or on a separate marker |
| `.cv lang <en or ru>` | Language of chat messages |
| `.cv stats`, `.cv refresh` | Results of the last check, or a full check right now |
| `.cv cleanup` | Before removing the mod: give routes the game's own settings back and remove the mod's icons (see Uninstalling) |

`.cv` is short for `.caravan`.

## Configuration

`BepInEx/config/Bazarov.CaravanTracker.cfg` is created on the first start.

| Section | Setting | Default | Meaning |
|---|---|---|---|
| General | Language | `en` | `en` or `ru` |
| Map | Icon | `merchant` | `merchant`, `unknown`, `vblood` or a PrefabGUID number |
| Map | GlobalIcons | `true` | Icons visible to everyone on the whole map |
| Map | IconMode | `direct` | `direct` or `proxy` |
| Map | IconOnLoot | `true` | Icon on the loot left after a robbed caravan |
| Map | IconOnFrozen | `true` | Icon on a caravan frozen mid-route because no players are near |
| Chat | Announce | `true` | Chat announcement when a caravan sets out |
| Chat | AnnounceTo | `All` | `All`, `SameAct` (players of the caravan's act) or `Nearby` |
| Chat | AnnounceRadius | `1500` | Meters for `Nearby` |
| Chat | AnnounceVanilla | `true` | Also announce caravans the game spawned by itself; `false` = only the mod's |
| Chat | RouteCommandsForAll | `true` | `.cv r` and `.cv rm` for every player; `false` = admins only |
| Chat | TitleColor, TextColor, CommandColor | gold, white, green | Announcement colors |
| AutoStart | Source | `Both` | `Mod` = only the mod starts caravans, the game no longer spawns them near players; `Game` = only the game, auto-start off; `Both` |
| AutoStart | IntervalMinutes | `20` | Minimum time between two auto-starts in the same act, `0` = off. With `Source = Mod` and `0` there are no caravans at all; the log warns about it |
| AutoStart | Scope | `AllOnlineActs` | `AllOnlineActs` or `Leader` (only the most advanced player's act) |
| AutoStart | MaxPerAct | `2` | Caravans started by the mod on the road at once in one act |
| AutoStart | MaxTotal | `6` | The same for the whole server |
| AutoStart | RoutePick | `Random` | `Random` or `Rotate` (the route started longest ago) |
| AutoStart | ExcludedRoutes | empty | Routes auto-start never uses, e.g. `BanditPrisoners,ChurchLegion` |
| AutoStart | RouteActs | empty | Manual acts, e.g. `MilitiaIron=2,ChuchSilver=4` |
| Start | SpawnTimeoutMinutes | `5` | How long to wait for a started squad to appear |
| Start | ResetToStart | `true` | A route left mid-way is put back to its first stop before a start |
| Start | MaxRunMinutes | `90` | How long a started caravan is looked after |
| Start | MaxSquadSize | `20` | Safety limit: extra units of a started squad are removed |
| Timing | CheckSeconds | `30` | How often caravans are searched |
| Timing | SafetyScanMinutes | `30` | How often the whole world is searched as a safety net |
| Debug | VerboseLog | `false` | Every check and every step of started caravans in the server log |
| Debug | EnableDiagnostics | `false` | Developer commands `.cvdiag` |

Limits count only caravans started by the mod that still have their cart. Caravans that spawn the vanilla way next to players are not counted.

## Limitations

- The game has no wagon map icon, and a server mod can't send new pictures to players, so the caravan icon is one of the game's own icons. Pick the one you like with `.cv icon`.
- A started caravan keeps moving even if everybody logs off, and disappears at the end of its route as usual.
- A route carries one caravan at a time. When the cart is destroyed, the horse walks on to the end of the route, where the game removes it and returns the route to the start. Until then that route is busy; other routes are not affected.
- If an earlier caravan was destroyed mid-way, its route stays there. Before starting such a route the mod puts it back to its first stop (`ResetToStart`), and auto-start takes such routes last.

## Installation

With r2modman or Thunderstore Mod Manager: install CaravanTracker, the dependencies come along.

Manually: put `CaravanTracker.dll` into `BepInEx/plugins/` on the server. Needs [BepInExPack V Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/) and [VampireCommandFramework](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/).

## Uninstalling

The mod changes some route settings in the world while it runs: with `Source = Mod` it blocks the game's own spawning, a started caravan gets start settings for a while, and the icons it creates live in the world. The server save can keep these. Before removing the mod:

1. Run `.cv cleanup` in chat as admin. It stops started caravans, gives every route the game's own spawn range, timers and freezing back, and removes the mod's icons and markers. Until the next server restart the mod then changes nothing.
2. Stop the server, delete `BepInEx/plugins/CaravanTracker`, start the server.

Without the cleanup, routes blocked by `Source = Mod` would stay without caravans from the game.

## Building

```
dotnet build -c Release
```

Reference assemblies come from NuGet (nuget.org and nuget.bepinex.dev). To build against an installed server instead: `dotnet build -c Release -p:BepDir=/path/to/server/BepInEx`.

Tests of the decision logic need only the .NET 8 SDK, no game files: `dotnet run --project tests`.

A Release build also produces the Thunderstore package `dist/CaravanTracker-<version>.zip`. The version is written only in `CaravanTracker.csproj` (`<Version>`); `MyPluginInfo` for the code and the package's `manifest.json` are generated from it at build time.

How the mod works inside (patrols, starts, the spawn block, repair) is described in [ARCHITECTURE.md](ARCHITECTURE.md).

Every push is built by GitHub Actions; the package is attached to the run as an artifact.

On a server with Docker, `tools/deploy.sh /path/to/server` pulls the repository, builds the mod against the server's BepInEx and installs it.

## Credits

- The way to make map icons global is taken from [KindredPortals](https://thunderstore.io/c/v-rising/p/odjit/KindredPortals/) by odjit.
- Act bounds follow the V Blood list on the [V Rising wiki](https://vrising.fandom.com/wiki/V_Blood_Carriers).

License: MIT.

---

## По-русски

Серверный мод для V Rising. Караваны видны на карте всем игрокам, при выходе каравана в чат приходит анонс с расстоянием и направлением, а мод сам запускает караваны в актах, где есть игроки онлайн. Игрокам ничего ставить не нужно.

Русский язык сообщений включается в конфиге (`Language = ru`) или командой `.cv lang ru`.

Автозапуск: в каждом акте, где есть игрок онлайн, новый караван выходит не чаще раза в `IntervalMinutes` минут, пока в акте едет меньше `MaxPerAct`, а на сервере меньше `MaxTotal` караванов мода. Сначала берутся маршруты, которые стоят в начале пути. Акт игрока считается по выпитым V Blood, акт маршрута по уровню охраны. Проверить привязку маршрутов можно командой `.cv r`, поправить вручную через `RouteActs`, убрать маршрут из автозапуска через `ExcludedRoutes`.

Кто запускает караваны, задаёт `Source` (или команда `.cv source`): `Mod` - только мод, сама игра больше не выпускает караван, когда игрок подходит к маршруту; `Game` - только игра, автозапуск выключен; `Both` - и мод, и игра.

Кому приходит анонс, задаёт `AnnounceTo`: всем (`All`), игрокам того же акта (`SameAct`) или тем, кто ближе `AnnounceRadius` метров (`Nearby`).

Команды и настройки описаны выше, в английской части.

Перед удалением мода выполни в чате `.cv cleanup` (админ): мод вернёт маршрутам настройки игры и уберёт свои иконки. Потом останови сервер, удали папку `BepInEx/plugins/CaravanTracker` и запусти сервер. Без этого маршруты, заблокированные при `Source = Mod`, останутся без караванов игры.
