using System;
using System.Collections.Generic;

namespace CaravanTracker;

// State of one forced start, without any game objects: units are (Index, Version) keys.
public sealed class RunState
{
    public DateTime Started;
    public bool Spawned;
    public DateTime SpawnedAt;
    public DateTime LastGrow;
    public bool Restored;           // the route's own spawner settings are back
    public bool LostAny;            // a member of the squad is gone (killed)
    public bool HadCart;
    public bool CartGone;
    public readonly HashSet<(int, int)> Known = new();   // members of the squad
}

public enum RunEnd { None, NoSpawn, Done, Timeout }

public sealed class SquadStep
{
    public bool JustSpawned;
    public readonly List<(int, int)> Replacements = new();  // sent by the game after a loss: remove
    public readonly List<(int, int)> OverLimit = new();     // above MaxSquadSize: remove
    public readonly List<(int, int)> Remaining = new();     // the squad as it is now
    public bool Assembled;                                  // give the route its own settings back now
}

// One second of a forced start. The game spawns the squad in parts (the animal with the cart first,
// guards up to a minute later), and with the start's zero respawn timers it would replace every killed
// unit at once. So: every unit that appears while nothing of the squad is lost is a member; once a member
// is gone, any new unit is a replacement. The start settings stay until the squad hasn't grown for
// AssemblyQuiet (at most AssemblyMax after the first unit), then the route gets its own settings back.
public static class RunLogic
{
    public static readonly TimeSpan AssemblyQuiet = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan AssemblyMax = TimeSpan.FromMinutes(4);

    // units: living units of the squad now. atEnd: the route is at its last stop, where the game first
    // removes an old squad; units seen there are not the new caravan.
    public static SquadStep Squad(RunState s, DateTime now, bool atEnd, IReadOnlyList<(int, int)> units, int maxSquad)
    {
        var step = new SquadStep();
        if (!s.Spawned && units.Count > 0 && !atEnd)
        {
            s.Spawned = true;
            s.SpawnedAt = now;
            s.LastGrow = now;
            step.JustSpawned = true;
        }
        if (!s.Spawned)
        {
            step.Remaining.AddRange(units);
            return step;
        }
        var present = new HashSet<(int, int)>(units);
        if (!s.LostAny)
            foreach (var k in s.Known)
                if (!present.Contains(k)) { s.LostAny = true; break; }
        foreach (var u in units)
        {
            if (s.Known.Contains(u)) { step.Remaining.Add(u); continue; }
            if (s.LostAny) { step.Replacements.Add(u); continue; }
            if (s.Known.Count >= maxSquad) { step.OverLimit.Add(u); continue; }
            s.Known.Add(u);
            s.LastGrow = now;
            step.Remaining.Add(u);
        }
        if (!s.Restored && (now - s.LastGrow >= AssemblyQuiet || now - s.SpawnedAt >= AssemblyMax))
        {
            s.Restored = true;
            step.Assembled = true;
        }
        return step;
    }

    // After Squad: carts pulled by the remaining units. cartJustLost: the cart was destroyed just now, the rest
    // of the squad walks on to the end of the route (the start no longer counts in auto-start limits).
    public static RunEnd Outcome(RunState s, DateTime now, int remaining, int carts, TimeSpan spawnTimeout,
                                 TimeSpan maxRun, out bool cartJustLost)
    {
        cartJustLost = false;
        if (carts > 0) s.HadCart = true;
        else if (s.HadCart && !s.CartGone && remaining > 0)
        {
            s.CartGone = true;
            cartJustLost = true;
        }
        if (!s.Spawned && now - s.Started > spawnTimeout) return RunEnd.NoSpawn;
        if (s.Spawned && remaining == 0) return RunEnd.Done;
        if (now - s.Started > maxRun) return RunEnd.Timeout;
        return RunEnd.None;
    }
}
