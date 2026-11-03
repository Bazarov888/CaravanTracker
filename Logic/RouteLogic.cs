using System;
using System.Collections.Generic;

namespace CarriageTracker;

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
    bool FailedRecently,  // a start of this route failed within the last hour
    string Id = null)     // this patrol ("index:version"): two patrols share the key MilitiaCotton
{
    public bool AtEndState => Move == "WaitForDespawn";
    public bool WaitingForSpawn => Spawner == "WaitingForSpawn";
    public bool Loop => Stops != null && Stops.Loop;
    // What start history is kept by: the patrol, or the key if the patrol isn't known.
    public string Ident => Id ?? Key;
}

public enum RouteState
{
    Running,         // the mod is starting it
    Failed,          // its start failed within the last hour
    OnRoad,          // a squad with its cart
    CartLost,        // the cart is destroyed, the rest of the squad walks to the end of the route
    FrozenLeftover,  // what is left of a robbed caravan froze off the map, it would stand there until a player comes
    LoopLeftover,    // the cart is destroyed on a route without an end, players are near: the rest circles on
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
        if (s.UnitsOnMap == 0) return RouteState.FrozenLeftover;
        return s.Loop ? RouteState.LoopLeftover : RouteState.CartLost;
    }

    // A route the mod may start: nothing of the mod's runs there, and no caravan on the road.
    // A start removes leftovers (a squad at the end, a frozen horse without its cart) like any stuck squad.
    // On an ordinary route the rest of a robbed caravan walks to the end, where the game removes it. On a loop it
    // never would: it stays busy while players are near (they may still be fighting it) and becomes a
    // FrozenLeftover, free, once nobody is (the run ends with the cart, so it may freeze: RunEnd.LoopCartLost).
    public static bool IsFree(RouteState state) =>
        state == RouteState.Empty || state == RouteState.AtEnd || state == RouteState.FrozenLeftover;

    // Where a free route stands, for the pick order and the log. A route without an end has no "mid-way":
    // it counts as standing at its start.
    public static PickGroup Group(RouteSnap s)
    {
        if (s.Stops == null || s.Stops.AtStart || s.Loop) return PickGroup.AtStart;
        return s.AtEndState ? PickGroup.AtEnd : PickGroup.Midway;
    }

    // Index of the route to start among free ones, -1 if none.
    // Routes at their first stop and routes at their last stop go first, as one group: both set out from the
    // first stop (a route at its last stop is woken first, see RunLogic.ReadyToArm). Routes left mid-way by an
    // earlier caravan come after them.
    // Within a group: rotate takes the route the mod started longest ago (lastStart gives DateTime.MinValue for
    // never; it is asked by RouteSnap.Ident, the patrol, so the two MilitiaCotton patrols keep their own history);
    // otherwise a random one, but not the route started last while another one is there. Without that a
    // route that just finished stood at its first stop again and was picked over and over (31.10: MilitiaCotton
    // five times in a row while routes at their last stop waited). Mid-way routes without ResetToStart: the one
    // with the most stops left, so the caravan drives the longest way.
    public static int Choose(IReadOnlyList<RouteSnap> free, bool resetToStart, bool rotate,
                             Func<string, DateTime> lastStart, Random rng)
    {
        var ready = new List<int>();
        var midway = new List<int>();
        for (int i = 0; i < free.Count; i++) (Group(free[i]) == PickGroup.Midway ? midway : ready).Add(i);
        if (ready.Count > 0) return OneOf(free, ready, rotate, lastStart, rng);
        if (midway.Count == 0) return -1;
        if (resetToStart) return OneOf(free, midway, rotate, lastStart, rng);
        int best = midway[0];
        foreach (int i in midway) if (Left(free[i]) > Left(free[best])) best = i;
        return best;
    }

    static int OneOf(IReadOnlyList<RouteSnap> free, List<int> idx, bool rotate, Func<string, DateTime> lastStart, Random rng)
    {
        int oldest = idx[0], newest = idx[0];
        foreach (int i in idx)
        {
            if (lastStart(free[i].Ident) < lastStart(free[oldest].Ident)) oldest = i;
            if (lastStart(free[i].Ident) > lastStart(free[newest].Ident)) newest = i;
        }
        if (rotate) return oldest;
        var pool = new List<int>(idx);
        if (pool.Count > 1 && lastStart(free[newest].Ident) != DateTime.MinValue) pool.Remove(newest);
        return pool[rng.Next(pool.Count)];
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
    // isn't touched: the game returns it to the start by itself. A route without an end isn't either:
    // a caravan there may set out from any stop.
    public static bool NeedsReset(Progress stops, string move) =>
        stops != null && !stops.Loop && !stops.AtStart && move != "WaitForDespawn";
}
