using System;
using System.Collections.Generic;
using ProjectM;
using Unity.Entities;

namespace CaravanTracker;

// Auto-start. Every check the mod looks at the acts of online players (all of them or only the most
// advanced one, see Scope). In each such act a caravan starts on a free route if at least IntervalMinutes
// passed since the previous auto-start in that act and the MaxPerAct and MaxTotal limits allow it.
// If an act has no routes, the nearest lower act with routes is used. Nobody online - nothing starts.
// Routes from ExcludedRoutes are never used. Which routes are free and which one goes first is decided
// by RouteLogic (IsFree, Choose).
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
        if (Settings.AutoInterval.Value <= 0 || Settings.GameOnly || Starter.Paused) return;
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
            if (r.Started) Plugin.Info($"CaravanTracker auto: {r.Text}");
            else Plugin.Trace($"CaravanTracker auto: {r.Text}");
        }
    }

    // force = ignore the interval (the .cv autonow command). Returns what happened in each act;
    // acts that simply wait for their interval are not reported.
    public static List<(bool Started, string Text)> Run(EntityManager em, PrefabCollectionSystem prefabs, bool force)
    {
        var results = new List<(bool Started, string Text)>();
        if (Starter.Paused)
        {
            results.Add((false, L.T("cleanup.paused")));
            return results;
        }
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
        var actsWithRoutes = new HashSet<int>(routeActs.Values);

        int interval = Math.Max(1, Settings.AutoInterval.Value);
        int maxPerAct = Math.Max(1, Settings.AutoMaxPerAct.Value);
        int maxTotal = Math.Max(1, Settings.AutoMaxTotal.Value);
        var served = new HashSet<int>();
        var now = DateTime.UtcNow;

        foreach (int playerAct in playerActs)
        {
            int act = ActLogic.Served(playerAct, actsWithRoutes);
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
            var candidates = routes.FindAll(r => ActOf(r) == act);
            var free = new List<Routes.Route>();
            var snaps = new List<RouteSnap>();
            foreach (var r in candidates)
            {
                var snap = Routes.Snap(em, r);
                if (!RouteLogic.IsFree(RouteLogic.Classify(snap))) continue;
                free.Add(r);
                snaps.Add(snap);
            }
            if (free.Count == 0)
            {
                results.Add((false, L.T("auto.busy", L.Act(act))));
                continue;
            }
            int chosen = RouteLogic.Choose(snaps, Settings.ResetToStart.Value, Settings.RotatePick,
                                           key => LastRouteStart.TryGetValue(key, out var t) ? t : DateTime.MinValue, Rng);
            var pick = free[chosen];
            Starter.Start(em, pick, Entity.Null, true, act,
                          $", picked from {free.Count} free: {RouteLogic.Groups(snaps)}, this one {RouteLogic.Group(snaps[chosen])}");
            var stops = Routes.StopsOf(em, pick.Patrol);
            LastStart[act] = now;
            LastRouteStart[pick.Key] = now;
            results.Add((true, L.T("auto.started", L.Route(pick.Key), L.Act(act), free.Count, Routes.StopText(stops))));
        }
        return results;
    }
}
