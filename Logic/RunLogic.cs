using System;
using System.Collections.Generic;

namespace CarriageTracker;

// Waking: the route stood at its last stop; it is awake but may not spawn yet (see RunLogic.ReadyToArm).
// Armed: the route may spawn its squad.
public enum RunPhase { Armed, Waking }

// State of one forced start, without any game objects: units are (Index, Version) keys.
public sealed class RunState
{
    public DateTime Started;
    public RunPhase Phase = RunPhase.Armed;
    public bool Spawned;
    public DateTime SpawnedAt;
    public DateTime LastGrow;
    public bool Restored;           // the route's own spawner settings are back: set by the executor once they are written
    public bool LostAny;            // a member of the squad is gone (killed)
    public bool HadCart;
    public bool CartGone;
    public DateTime ReadySince;     // waking: when the route was first seen back at its first stop, idle
    public DateTime AtEndSince;     // when the squad was first seen standing at the last stop
    public (int, int)? Animal;      // the animal whose health is watched (AnimalHealth)
    public float AnimalLowest = -1; // its lowest health seen, -1 before the first look
    public readonly HashSet<(int, int)> Known = new();   // members of the squad
    public readonly Dictionary<(int, int), DateTime> SeenAt = new();   // when each member first appeared
}

// LoopCartLost: the cart is destroyed on a route without an end; the run is over, the rest of the squad circles on.
public enum RunEnd { None, NoSpawn, Done, StuckAtEnd, Timeout, LoopCartLost }

// What to do with a woken route this second (ReadyToArm).
public enum WakeStep { Wait, Arm, GiveUp }

public sealed class SquadStep
{
    public bool JustSpawned;
    public readonly List<(int, int)> Replacements = new();  // sent by the game after a loss: remove
    public readonly List<(int, int)> OverLimit = new();     // above MaxSquadSize: remove
    public readonly List<(int, int)> Remaining = new();     // the squad as it is now
    public bool Assembled;                                  // give the route its own settings back now (until Restored)
}

// One second of a forced start. The game spawns the squad in parts (the animal with the cart first,
// guards up to a minute later), and with the start's zero respawn timers it would replace every killed
// unit at once. So: every unit that appears while nothing of the squad is lost is a member; once a member
// is gone, a new unit is a replacement, unless the squad hasn't reached its size yet (expected: units in the
// route's group, the largest group of its composition): then it is a guard that was still on its way.
// The start settings stay until the squad hasn't grown for AssemblyQuiet (at most AssemblyMax after the first
// unit), then the route gets its own settings back.
public static class RunLogic
{
    public static readonly TimeSpan AssemblyQuiet = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan AssemblyMax = TimeSpan.FromMinutes(4);
    public static readonly TimeSpan WakeMax = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan WakeSettle = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan EndGrace = TimeSpan.FromMinutes(2);
    // The game's spawn buff (Buff_General_Spawn_Unit) lasts a few seconds. On 01.11 and 02.11 it stayed on the
    // animal of squads spawned right after a wake, also after the cart was lost, and that animal took no damage.
    // A member older than this still carrying it has it removed.
    public static readonly TimeSpan SpawnBuffMax = TimeSpan.FromSeconds(10);
    public const float WakeMoved = 5f;
    // A squad's first unit within this distance of where the route stood appeared "in place".
    public const float SamePlace = 20f;

    // A route at its last stop (WaitForDespawn) is woken first with spawning still off. Seen on the server:
    // spawning there at once puts the squad at the end of the route (31.10, MilitiaCotton), while a route the
    // game finishes itself with the patrol awake goes back to its first stop, position included (31.10,
    // BanditCopper; 01.11 three times within a second).
    // Spawning is allowed once the route has left the last stop, stands at its first stop, the patrol has moved
    // and the spawner is idle again (WaitingForSpawn: the game has finished removing the old squad), and all of
    // that has held for WakeSettle. Squads spawned one second after the reset, before that (01.11, 1.0.8), stayed
    // at the last stop instead of being removed there. Their animal took no damage; squads of woken routes still
    // had that with 1.0.10 (02.11), the cause is the spawn buff left on it (SpawnBuffMax).
    // If the route isn't ready within WakeMax the start is given up; until 1.0.10 the squad was let spawn where
    // the route stood.
    public static WakeStep ReadyToArm(RunState s, DateTime now, bool atEndState, bool atStartIndex, float movedMeters,
                                      bool spawnerIdle)
    {
        if (!atEndState && atStartIndex && movedMeters >= WakeMoved && spawnerIdle)
        {
            if (s.ReadySince == default) s.ReadySince = now;
            if (now - s.ReadySince >= WakeSettle) return WakeStep.Arm;
        }
        else s.ReadySince = default;
        return now - s.Started < WakeMax ? WakeStep.Wait : WakeStep.GiveUp;
    }

    // A route standing at its last stop is woken before its squad may spawn.
    public static RunPhase FirstPhase(string move) => move == "WaitForDespawn" ? RunPhase.Waking : RunPhase.Armed;

    // units: living units of the squad now. atEnd: the route is at its last stop, where the game first
    // removes an old squad; units seen there are not the new caravan. expected: size of the route's squad,
    // 0 if unknown.
    public static SquadStep Squad(RunState s, DateTime now, bool atEnd, IReadOnlyList<(int, int)> units, int maxSquad,
                                  int expected = 0)
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
            if (s.LostAny && (expected <= 0 || s.Known.Count >= expected)) { step.Replacements.Add(u); continue; }
            if (s.Known.Count >= maxSquad) { step.OverLimit.Add(u); continue; }
            s.Known.Add(u);
            s.SeenAt[u] = now;
            s.LastGrow = now;
            step.Remaining.Add(u);
        }
        // Asked again every second until the executor has written the settings and set Restored: a write that
        // failed is tried again (Finish does it too).
        if (!s.Restored && (now - s.LastGrow >= AssemblyQuiet || now - s.SpawnedAt >= AssemblyMax))
            step.Assembled = true;
        return step;
    }

    // The caravan's animal must not heal (setting AnimalKeepsDamage). 04.11: an animal that broke off from its cart
    // in a fight came back, hitched up again and had full health. Its lowest health seen is kept; health above it is
    // a heal, and the returned value is what to set it back to (null: leave it). Small rises (up to 1 point) are
    // rounding. A dead animal (0) or another animal (the squad changed) starts anew.
    public static float? AnimalHealth(RunState s, (int, int) animal, float health)
    {
        if (s.Animal != animal || health <= 0)
        {
            s.Animal = animal;
            s.AnimalLowest = health > 0 ? health : -1;
            return null;
        }
        if (s.AnimalLowest < 0 || health < s.AnimalLowest)
        {
            s.AnimalLowest = health;
            return null;
        }
        return health > s.AnimalLowest + 1f ? s.AnimalLowest : null;
    }

    // A member that appeared at least SpawnBuffMax ago: a spawn buff on it is a leftover. Carts aren't members;
    // for them the time of the first unit counts (unit = null).
    public static bool PastSpawn(RunState s, (int, int)? unit, DateTime now)
    {
        if (!s.Spawned) return false;
        DateTime since = s.SpawnedAt;
        if (unit.HasValue && !s.SeenAt.TryGetValue(unit.Value, out since)) return false;
        return now - since >= SpawnBuffMax;
    }

    // After Squad: carts pulled by the remaining units. cartJustLost: the cart was destroyed just now, the rest
    // of the squad walks on to the end of the route (the start no longer counts in auto-start limits).
    // atEnd: the route stands at its last stop. The game removes a squad there within seconds; one still
    // standing there after EndGrace is StuckAtEnd and is removed by the mod (01.11: a squad stood there 77 minutes).
    // loop: the route has no end. Its robbed caravan would never reach one, so the run ends when the cart is lost:
    // the rest circles on and may freeze again once no player is near, which frees the route.
    public static RunEnd Outcome(RunState s, DateTime now, int remaining, int carts, bool atEnd, TimeSpan spawnTimeout,
                                 TimeSpan maxRun, out bool cartJustLost, bool loop = false)
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
        if (loop && s.CartGone) return RunEnd.LoopCartLost;
        if (s.Spawned && atEnd)
        {
            if (s.AtEndSince == default) s.AtEndSince = now;
            else if (now - s.AtEndSince >= EndGrace) return RunEnd.StuckAtEnd;
        }
        else s.AtEndSince = default;
        if (now - s.Started > maxRun) return RunEnd.Timeout;
        return RunEnd.None;
    }
}
