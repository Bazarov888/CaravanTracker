using System;
using System.Collections.Generic;
using ProjectM;
using Unity.Entities;

namespace CaravanTracker;

// Auto-start. Every check the mod looks at the acts of online players (all of them or only the most
// advanced one, see Scope). In each such act a caravan starts on a free route if at least IntervalMinutes
// passed since the previous auto-start in that act and the MaxPerAct and MaxTotal limits allow it.
// If an act has no routes, the nearest lower act with routes is used. Nobody online - nothing starts.
// Routes from ExcludedRoutes are never used. Routes standing at the start of their path go first.
static class AutoStart
{
    static readonly Dictionary<int, DateTime> LastStart = new();
    static readonly Dictionary<string, DateTime> LastRouteStart = new();
    static readonly Random Rng = new();
    static DateTime _ready = DateTime.MinValue;
    public static readonly List<string> LastResults = new();
    static string _lastBlocked = "";

    // Time of the previous auto-start in the act. Before the first one: when the server became ready,
    // so the first caravan of every act sets out one interval after the server start.
    static DateTime LastOf(int act) => LastStart.TryGetValue(act, out var t) ? t : _ready;

    public static double MinutesLeft(int act)
    {
        if (_ready == DateTime.MinValue) return Settings.AutoInterval.Value;
        return Math.Max(0, (LastOf(act).AddMinutes(Settings.AutoInterval.Value) - DateTime.UtcNow).TotalMinutes);
    }

    // Called by .cv auto <minutes>: the new interval counts from now.
    public static void Restart()
    {
        LastStart.Clear();
        _ready = DateTime.UtcNow;
    }

    public static void Tick()
    {
        if (Settings.AutoInterval.Value <= 0 || Settings.GameOnly) return;
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) return;
        if (_ready == DateTime.MinValue) { _ready = DateTime.UtcNow; return; }
        var results = Run(world.EntityManager, prefabs, false);
        if (results.Count == 0) return;
        // A start is always logged. A reason why nothing started (limit, busy routes) is logged
        // only when it changes, otherwise it would repeat every check.
        string blocked = string.Join(" | ", results.FindAll(r => !r.Started).ConvertAll(r => r.Text));
        bool started = results.Exists(r => r.Started);
        if (!started && blocked == _lastBlocked) return;
        _lastBlocked = blocked;
        LastResults.Clear();
        foreach (var r in results)
        {
            LastResults.Add(r.Text);
            if (r.Started) Plugin.Logger.LogInfo($"CaravanTracker auto: {r.Text}");
            else Plugin.Trace($"CaravanTracker auto: {r.Text}");
        }
    }

    // Free route: the mod isn't starting it now and there is no caravan on it: no squad at all, or the squad
    // froze at the end of the route (it vanishes when approached), or what is left of a robbed caravan
    // (the horse without its cart) froze mid-route with no players near and would stand there until someone
    // comes. Starting the route removes such leftovers like any stuck squad.
    static bool IsFree(EntityManager em, Routes.Route r)
    {
        if (Starter.IsRunning(r.Patrol) || Starter.RecentlyFailed(r.Key)) return false;
        var units = Routes.ActiveUnits(em, r.Patrol);
        if (units.Count == 0) return true;
        if (Routes.MoveStatus(em, r.Patrol) == "WaitForDespawn") return true;
        if (Routes.CartsOf(em, units).Count > 0) return false;
        // Frozen units are moved by the game off the map, to (10000, 10000).
        return units.TrueForAll(u => !Caravans.OnMap(Caravans.PositionOf(em, u)));
    }

    // Which free routes go first: those standing at the start of their path, then those at their last stop
    // (the game returns them to the start when they are started), then those left mid-way by an earlier
    // caravan (the mod puts them back to the start, see ResetToStart; with it off, the one with the most
    // stops left). Within a group Random takes any route, Rotate the one the mod started longest ago.
    static Routes.Route Pick(EntityManager em, List<Routes.Route> free)
    {
        var atStart = new List<Routes.Route>();
        var atEnd = new List<Routes.Route>();
        var midway = new List<Routes.Route>();
        foreach (var r in free)
        {
            var p = Routes.StopsOf(em, r.Patrol);
            if (p == null || p.AtStart) atStart.Add(r);
            else if (Routes.MoveStatus(em, r.Patrol) == "WaitForDespawn") atEnd.Add(r);
            else midway.Add(r);
        }
        if (atStart.Count > 0) return OneOf(atStart);
        if (atEnd.Count > 0) return OneOf(atEnd);
        if (Settings.ResetToStart.Value) return OneOf(midway);
        Routes.Route best = null;
        int bestLeft = -1;
        foreach (var r in midway)
        {
            int left = Routes.StopsOf(em, r.Patrol)?.Left ?? 0;
            if (left > bestLeft) { best = r; bestLeft = left; }
        }
        return best ?? free[Rng.Next(free.Count)];
    }

    static Routes.Route OneOf(List<Routes.Route> routes)
    {
        if (!Settings.RotatePick) return routes[Rng.Next(routes.Count)];
        Routes.Route oldest = null;
        var oldestAt = DateTime.MaxValue;
        foreach (var r in routes)
        {
            var at = LastRouteStart.TryGetValue(r.Key, out var t) ? t : DateTime.MinValue;
            if (oldest == null || at < oldestAt) { oldest = r; oldestAt = at; }
        }
        return oldest;
    }

    // force = ignore the interval (the .cv autonow command). Returns what happened in each act;
    // acts that simply wait for their interval are not reported.
    public static List<(bool Started, string Text)> Run(EntityManager em, PrefabCollectionSystem prefabs, bool force)
    {
        var results = new List<(bool Started, string Text)>();
        if (Settings.GameOnly)
        {
            results.Add((false, L.T("auto.gameonly")));
            return results;
        }
        var online = Acts.Online(em, prefabs);
        if (online.Count == 0)
        {
            if (force) results.Add((false, L.T("auto.nobody")));
            return results;
        }

        // Player acts to serve, the most advanced first.
        var playerActs = new List<int>();
        if (Settings.LeaderScope) playerActs.Add(Acts.Leader(online).Act.Act);
        else
            foreach (var p in online)
                if (!playerActs.Contains(p.Act.Act)) playerActs.Add(p.Act.Act);
        playerActs.Sort((a, b) => b.CompareTo(a));

        var routes = Routes.All(em, prefabs).FindAll(r => !Settings.Excluded(r.Key));
        var routeActs = new Dictionary<(int, int), int>();
        foreach (var r in routes) routeActs[(r.Patrol.Index, r.Patrol.Version)] = Acts.OfRoute(em, prefabs, r).Act;
        int ActOf(Routes.Route r) => routeActs[(r.Patrol.Index, r.Patrol.Version)];

        int interval = Math.Max(1, Settings.AutoInterval.Value);
        int maxPerAct = Math.Max(1, Settings.AutoMaxPerAct.Value);
        int maxTotal = Math.Max(1, Settings.AutoMaxTotal.Value);
        var served = new HashSet<int>();
        var now = DateTime.UtcNow;

        foreach (int playerAct in playerActs)
        {
            // Nearest act at or below the player's act that has routes.
            int act = playerAct;
            while (act >= 1 && !routes.Exists(r => ActOf(r) == act)) act--;
            if (act < 1)
            {
                results.Add((false, L.T("auto.noroutes", L.Act(playerAct))));
                continue;
            }
            if (!served.Add(act)) continue;
            if (!force && now < LastOf(act).AddMinutes(interval)) continue;

            if (Starter.AutoCount() >= maxTotal)
            {
                results.Add((false, L.T("auto.maxtotal", Starter.AutoCount(), maxTotal)));
                break;
            }
            if (Starter.AutoCount(act) >= maxPerAct)
            {
                results.Add((false, L.T("auto.maxact", L.Act(act), Starter.AutoCount(act), maxPerAct)));
                continue;
            }
            var free = routes.FindAll(r => ActOf(r) == act && IsFree(em, r));
            if (free.Count == 0)
            {
                results.Add((false, L.T("auto.busy", L.Act(act))));
                continue;
            }
            var pick = Pick(em, free);
            Starter.Start(em, pick, Entity.Null, true, act);
            var stops = Routes.StopsOf(em, pick.Patrol);
            LastStart[act] = now;
            LastRouteStart[pick.Key] = now;
            results.Add((true, L.T("auto.started", L.Route(pick.Key), L.Act(act), free.Count, Routes.StopText(stops))));
        }
        return results;
    }
}
