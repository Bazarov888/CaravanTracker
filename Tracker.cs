using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CaravanTracker;

// Announcement to online players, two lines. The first one: which caravan and of which act.
// The second one is personal: distance, direction and the mark command with this caravan's number in their .cv l.
// Who gets it is set by AnnounceTo: everyone, players of the caravan's act, or players within AnnounceRadius.
static class Announcer
{
    public static void CaravanStarted(EntityManager em, PrefabCollectionSystem prefabs, string route, Entity cart, float3 pos)
    {
        int act = 0;
        try { act = Acts.OfRouteKey(em, prefabs, route); }
        catch (Exception e) { Plugin.Logger.LogWarning($"CaravanTracker: act of route {route} unknown: {e.Message}"); }
        bool sameAct = Settings.AnnounceSameAct && act > 0;
        bool nearby = Settings.AnnounceNearby;
        float radius = Math.Max(100, Settings.AnnounceRadius.Value);
        var withRoutes = sameAct ? Acts.WithRoutes(em, prefabs) : null;
        int sent = 0;
        string title = Settings.Color(Settings.TitleColor, "#FFC83C");
        string text = Settings.Color(Settings.TextColor, "#FFFFFF");
        string command = Settings.Color(Settings.CommandColor, "#7CFC7C");
        string name = L.Route(route) + (act > 0 ? " (" + L.Act(act) + ")" : "");
        string head = $"<color={title}><b>{L.T("announce.title", name)}</b></color>";

        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<User>()));
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var users = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var ue in users)
            {
                var user = em.GetComponentData<User>(ue);
                if (!user.IsConnected) continue;
                if (sameAct && Acts.Served(Acts.OfUser(em, prefabs, ue).Act, withRoutes) != act) continue;
                string where = "";
                string mark = ".cv m";
                var character = user.LocalCharacter.GetEntityOnServer();
                bool hasCharacter = em.Exists(character);
                float3 me = hasCharacter ? Caravans.PositionOf(em, character) : float3.zero;
                float dist = hasCharacter ? Caravans.Dist2(pos, me) : float.MaxValue;
                if (nearby && dist > radius) continue;
                if (hasCharacter)
                {
                    where = L.T("announce.where", Math.Round(dist), Caravans.Direction(pos.x - me.x, pos.z - me.z)) + " ";
                    int n = NumberFor(me, cart);
                    mark = n == 1 ? ".cv m" : n > 1 ? $".cv m {n}" : ".cv l";
                }
                Chat.Send(em, user, head);
                Chat.Send(em, user, $"<color={text}>{where}{L.T("announce.mark")} </color><color={command}><b>{mark}</b></color>");
                sent++;
            }
        }
        finally
        {
            users.Dispose();
            query.Dispose();
        }
        Plugin.Trace($"CaravanTracker: announcement of {route} sent to {sent} players");
    }

    // Number of the caravan in the player's list (.cv l sorts by distance from them). 0 if not found.
    static int NumberFor(float3 me, Entity cart)
    {
        var list = new List<(float Dist, Entity Target)>();
        foreach (var c in Tracker.Current) list.Add((Caravans.Dist2(c.Pos, me), c.Target));
        list.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        for (int i = 0; i < list.Count; i++)
            if (list[i].Target == cart) return i + 1;
        return 0;
    }
}

static class Chat
{
    public static void Send(EntityManager em, User user, string text)
    {
        FixedString512Bytes msg = new(text);
        ServerChatUtils.SendSystemMessageToClient(em, user, ref msg);
    }

    public static void Send(EntityManager em, Entity userEntity, string text)
    {
        if (userEntity == Entity.Null || !em.Exists(userEntity) || !Caravans.Has<User>(em, userEntity)) return;
        var user = em.GetComponentData<User>(userEntity);
        if (user.IsConnected) Send(em, user, text);
    }
}

static class Tracker
{
    static List<Caravans.Caravan> _caravans = new();
    static DateTime _nextSafety = DateTime.MinValue;
    static DateTime _lastSafetyAt = DateTime.MinValue;
    static long _lastSafetyMs = -1;
    static int _lastMissed;
    static string _lastStats;
    public static string LastStats => _lastStats ?? L.T("stats.none");
    public static List<Caravans.Caravan> Current => _caravans;
    static HashSet<(int, int)> _announced = new();
    static bool _primed;

    // Every time a fast search. A full pass runs on schedule or on command;
    // it also checks whether the fast search missed a caravan.
    public static bool Tick(bool forceFull)
    {
        var world = Caravans.ServerWorld();
        if (world == null) return false;
        var prefabs = Caravans.Prefabs(world);
        if (prefabs == null) return false;
        var em = world.EntityManager;
        var now = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var found = Caravans.FastScan(em, prefabs, out int scanned);
        long fastMs = sw.ElapsedMilliseconds;

        int safetyMin = Settings.SafetyScanMinutes.Value;
        bool full = forceFull || (safetyMin > 0 && now >= _nextSafety);
        if (full)
        {
            var fsw = System.Diagnostics.Stopwatch.StartNew();
            var all = Caravans.FullScan(em, prefabs, out _);
            _lastSafetyMs = fsw.ElapsedMilliseconds;
            _lastSafetyAt = now;
            _nextSafety = now.AddMinutes(Math.Max(1, safetyMin));
            var fast = new HashSet<(int, int)>();
            foreach (var f in found) fast.Add((f.Entity.Index, f.Entity.Version));
            _lastMissed = 0;
            foreach (var f in all)
            {
                if (f.Kind == Kind.Horse || fast.Contains((f.Entity.Index, f.Entity.Version))) continue;
                _lastMissed++;
                found.Add(f);
                Plugin.Logger.LogWarning($"CaravanTracker: fast search missed {f.Kind} on route {f.Route}");
            }
        }

        var built = Caravans.Build(found);
        if (_primed) LogGone(em, _caravans, built);
        else if (built.Count > 0)
            Plugin.Logger.LogInfo($"CaravanTracker: on the map at start: " +
                                  string.Join("; ", built.ConvertAll(c => $"{c.Route} {c.State} at ({Math.Round(c.Pos.x)}, {Math.Round(c.Pos.z)})")));
        _caravans = built;

        // First check after the server start: routes left with start settings by a crash get normal values back.
        if (!_primed)
        {
            try { Starter.RepairLeftovers(em, prefabs); }
            catch (Exception e) { Plugin.Logger.LogError($"CaravanTracker: repair of routes failed: {e}"); }
        }

        // Announcement: once per cart, when it is first seen moving on the map (the game spawned it next to
        // a player, or the mod started it). A cart that froze and woke up again, or was hidden for a moment
        // as left without its animal, isn't announced again. A cart parked off the map is forgotten, so if
        // the game uses it for a new caravan, that one is announced. The first check after server start
        // only remembers the carts already on the map.
        var announced = new HashSet<(int, int)>();
        bool paused = Starter.Paused;
        foreach (var f in found)
        {
            if (paused || f.Kind != Kind.Cart || !Caravans.OnMap(f.Pos)) continue;
            var key = (f.Entity.Index, f.Entity.Version);
            if (!_primed || _announced.Contains(key)) { announced.Add(key); continue; }
            if (f.Frozen || f.Orphan) continue;
            // A cart at the last stop of its route isn't setting out: the game is about to remove it.
            if (f.Status == "WaitForDespawn") continue;
            announced.Add(key);
            bool byMod = f.Patrol != Entity.Null && Starter.IsRunning(f.Patrol);
            string stops = f.Stops != null ? $", stop {f.Stops.Log}" : "";
            Plugin.Logger.LogInfo($"CaravanTracker: caravan {f.Route} set out at ({Math.Round(f.Pos.x)}, {Math.Round(f.Pos.z)}){stops}{(byMod ? ", started by the mod" : "")}");
            if (Settings.Announce.Value && (byMod || Settings.AnnounceVanilla.Value))
                Announcer.CaravanStarted(em, prefabs, f.Route, f.Entity, f.Pos);
        }
        _announced = announced;
        _primed = true;

        bool proxyMode = Settings.ProxyMode;
        var none = new List<Caravans.Caravan>();
        // Frozen carts physically lie at (10000, 10000), so their icon goes on a separate marker
        // where the patrol stands. Moving ones as the mode says. After .cv cleanup no icons are wanted,
        // so all of the mod's icons and markers are removed.
        var withIcon = paused ? none : _caravans.FindAll(c => c.Icon);
        var active = withIcon.FindAll(c => !c.Frozen);
        var frozenWithIcon = withIcon.FindAll(c => c.Frozen);
        var direct = Icons.Sync(em, prefabs, proxyMode ? none : active);
        var viaProxy = Proxies.Sync(em, prefabs, proxyMode ? withIcon : frozenWithIcon);
        int created = direct.Created + viaProxy.Created;
        int removed = direct.Removed + viaProxy.Removed;
        int globalized = direct.Globalized + viaProxy.Globalized;
        long ms = sw.ElapsedMilliseconds;
        int empty = found.FindAll(f => f.Kind == Kind.Container && f.Empty).Count;
        int parked = found.FindAll(f => f.Kind != Kind.Horse && !Caravans.OnMap(f.Pos)).Count;
        int frozenCount = _caravans.FindAll(c => c.Frozen).Count;

        _lastStats = L.T("stats.main", proxyMode ? "proxy" : "direct", ms, fastMs, scanned) +
                     (full ? L.T("stats.full", _lastSafetyMs) : "") + ". " +
                     L.T("stats.counts", _caravans.Count, empty, frozenCount) +
                     (parked > 0 ? L.T("stats.parked", parked) : "") + ". " +
                     L.T("stats.icons", created, removed) +
                     (globalized > 0 ? L.T("stats.globalized", globalized) : "") + "." +
                     (_lastSafetyMs >= 0 ? " " + L.T("stats.safety", Math.Round((now - _lastSafetyAt).TotalMinutes), _lastMissed) : "");
        if (created > 0 || removed > 0 || globalized > 0)
            Plugin.Trace($"CaravanTracker: check {ms} ms (fast {fastMs} ms over {scanned}), caravans {_caravans.Count}, " +
                                  $"empty containers {empty}, icons added {created}, removed {removed}, made global {globalized}");
        return true;
    }

    // A caravan that was in the list at the previous check and isn't now: written to the log with what
    // it was and what its route is doing, so it is clear later where it went.
    static void LogGone(EntityManager em, List<Caravans.Caravan> before, List<Caravans.Caravan> now)
    {
        var still = new HashSet<(int, int)>();
        foreach (var c in now) still.Add((c.Target.Index, c.Target.Version));
        foreach (var c in before)
        {
            if (still.Contains((c.Target.Index, c.Target.Version))) continue;
            bool exists = em.Exists(c.Target);
            bool patrolLeft = c.Patrol != Entity.Null && em.Exists(c.Patrol);
            bool atEnd = patrolLeft && Routes.MoveStatus(em, c.Patrol) == "WaitForDespawn";
            string why = c.State == "loot"
                ? (exists ? "looted" : "container removed by the game")
                : !exists ? "cart destroyed or removed by the game"
                : atEnd ? "reached the last stop, the game removes it there"
                : Caravans.OnMap(Caravans.PositionOf(em, c.Target)) ? "cart left without its animal"
                : "cart parked off the map";
            string route = "";
            if (patrolLeft)
            {
                var p = Routes.StopsOf(em, c.Patrol);
                route = $"; route now {Routes.MoveStatus(em, c.Patrol)}, units {Routes.ActiveUnits(em, c.Patrol).Count}, stop {p?.Log}";
            }
            Plugin.Logger.LogInfo($"CaravanTracker: caravan {c.Route} gone at ({Math.Round(c.Pos.x)}, {Math.Round(c.Pos.z)}): was {c.State}, {why}{route}");
        }
    }
}
