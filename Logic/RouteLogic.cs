using System;
using System.Collections.Generic;

namespace CaravanTracker;

// What the game tells about one route at one moment. Built by Routes.Snap from the patrol entity;
// every decision about a route is made from this alone.
public sealed record RouteSnap(
    string Key,
    string Move,          // MovePatrolState.Status: Moving, Stopped, WaitForDespawn, WaitingForPath...; "?" if unknown
    string Spawner,       // UnitCompositionSpawner.SpawnerState: WaitingForSpawn, Spawned...
    Progress Stops,       // null if the path is unknown
    int Units,            // living units of the squad (horse and guards)
    int UnitsOnMap,       // of them not frozen off the map at (10000, 10000)
    int Carts,            // carts pulled by the squad's horses
    bool Running,         // the mod is starting this route now
    bool FailedRecently)  // a start of this route failed within the last hour
{
    public bool AtEndState => Move == "WaitForDespawn";
    public bool WaitingForSpawn => Spawner == "WaitingForSpawn";
}

public enum RouteState
{
    Running,         // the mod is starting it
    Failed,          // its start failed within the last hour
    OnRoad,          // a squad with its cart
    CartLost,        // the cart is destroyed, the rest of the squad walks to the end of the route
    FrozenLeftover,  // what is left of a robbed caravan froze off the map, it would stand there until a player comes
    AtEnd,           // at the last stop (WaitForDespawn): the game removes the squad and returns the route to the start
    Empty,           // no squad: waits for a new caravan at the stop it stands at
}

// Which group a free route is picked from first.
public enum PickGroup { AtStart, AtEnd, Midway }

public static class RouteLogic
{
    public static RouteState Classify(RouteSnap s)
    {
        if (s.Running) return RouteState.Running;
        if (s.FailedRecently) return RouteState.Failed;
        if (s.AtEndState) return RouteState.AtEnd;
        if (s.Units == 0) return RouteState.Empty;
        if (s.Carts > 0) return RouteState.OnRoad;
        return s.UnitsOnMap == 0 ? RouteState.FrozenLeftover : RouteState.CartLost;
    }

    // A route the mod may start: nothing of the mod's runs there, and no caravan on the road.
    // A start removes leftovers (a squad at the end, a frozen horse without its cart) like any stuck squad.
    public static bool IsFree(RouteState state) =>
        state == RouteState.Empty || state == RouteState.AtEnd || state == RouteState.FrozenLeftover;

    // Routes standing at the start of their path go first. Then routes at their last stop: the game returns
    // them to the start when they are started. Then routes left mid-way by an earlier caravan: the mod puts
    // them back to the start (ResetToStart), or with that off the caravan sets out from where the route stands.
    public static PickGroup Group(RouteSnap s)
    {
        if (s.Stops == null || s.Stops.AtStart) return PickGroup.AtStart;
        return s.AtEndState ? PickGroup.AtEnd : PickGroup.Midway;
    }

    // Index of the route to start among free ones. rotate: within the group the route the mod started longest
    // ago (lastStart gives DateTime.MinValue for never), otherwise a random one. Mid-way routes without
    // ResetToStart: the one with the most stops left, so the caravan drives the longest way.
    public static int Choose(IReadOnlyList<RouteSnap> free, bool resetToStart, bool rotate,
                             Func<string, DateTime> lastStart, Random rng)
    {
        if (free.Count == 0) return -1;
        foreach (var group in new[] { PickGroup.AtStart, PickGroup.AtEnd, PickGroup.Midway })
        {
            var idx = new List<int>();
            for (int i = 0; i < free.Count; i++) if (Group(free[i]) == group) idx.Add(i);
            if (idx.Count == 0) continue;
            if (group == PickGroup.Midway && !resetToStart)
            {
                int best = idx[0];
                foreach (int i in idx) if (Left(free[i]) > Left(free[best])) best = i;
                return best;
            }
            if (!rotate) return idx[rng.Next(idx.Count)];
            int oldest = idx[0];
            foreach (int i in idx) if (lastStart(free[i].Key) < lastStart(free[oldest].Key)) oldest = i;
            return oldest;
        }
        return -1;
    }

    static int Left(RouteSnap s) => s.Stops?.Left ?? 0;

    // "1 at start, 0 at end, 4 midway" for the log line of an auto-start.
    public static string Groups(IReadOnlyList<RouteSnap> free)
    {
        int a = 0, e = 0, m = 0;
        foreach (var s in free)
            switch (Group(s)) { case PickGroup.AtStart: a++; break; case PickGroup.AtEnd: e++; break; default: m++; break; }
        return $"{a} at start, {e} at end, {m} midway";
    }

    // A route to put back to the first stop before a start: it stands mid-way. A route at its last stop
    // isn't touched: the game returns it to the start by itself.
    public static bool NeedsReset(Progress stops, string move) =>
        stops != null && !stops.AtStart && move != "WaitForDespawn";
}
