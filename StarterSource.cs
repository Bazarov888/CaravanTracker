using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace CaravanTracker;

// Source = Mod: keeping the game from spawning caravans by itself.
static partial class Starter
{
    // Source = Mod: the game must not spawn a caravan by itself when a player comes near an empty route.
    // What range each route gets is decided by SpawnRange.SourceTarget; routes the mod is starting are left
    // alone (a start sets its own range). If a squad still appears on a blocked route, the block doesn't work
    // on this game version: a warning goes to the log once per route.
    static readonly HashSet<(int, int)> BlockedRoutes = new();
    static readonly HashSet<(int, int)> WarnedRoutes = new();

    static void SetRangeY(EntityManager em, Entity patrol, UnitCompositionSpawner sp, float x, float y)
    {
        object boxed = sp;
        SetField(boxed, "PlayerRange", new float2(x, y));
        em.SetComponentData(patrol, (UnitCompositionSpawner)boxed);
    }

    // Source = Mod: right after a start ends, a route that waits for a new caravan is blocked at once,
    // not at the next check, so that the game can't spawn its own caravan there in between.
    static void BlockIfWaiting(EntityManager em, Entity patrol)
    {
        if (Paused || !Settings.ModOnly || !em.Exists(patrol)) return;
        var sp = em.GetComponentData<UnitCompositionSpawner>(patrol);
        if (sp.SpawnerState.ToString() != "WaitingForSpawn") return;
        if (Caravans.Field(sp, "PlayerRange") is not float2 range) return;
        if (SpawnRange.SourceTarget(true, true, range.x, range.y) is float y) SetRangeY(em, patrol, sp, range.x, y);
        BlockedRoutes.Add(Key(patrol));
    }

    public static int ApplySource(EntityManager em, PrefabCollectionSystem prefabs)
    {
        if (Paused) return 0;
        bool modOnly = Settings.ModOnly;
        int blocked = 0, opened = 0;
        foreach (var r in Routes.All(em, prefabs))
        {
            var key = Key(r.Patrol);
            if (IsRunning(r.Patrol) || !em.Exists(r.Patrol)) { BlockedRoutes.Remove(key); continue; }
            var sp = em.GetComponentData<UnitCompositionSpawner>(r.Patrol);
            if (Caravans.Field(sp, "PlayerRange") is not float2 range) continue;
            bool waiting = sp.SpawnerState.ToString() == "WaitingForSpawn";
            if (modOnly && !waiting && BlockedRoutes.Contains(key) && Routes.ActiveUnits(em, r.Patrol).Count > 0 && WarnedRoutes.Add(key))
                Plugin.Warn($"CaravanTracker: route {r.Key}: Source = Mod, but the game spawned a caravan on it by itself " +
                                         $"(range {range.x}-{range.y}). The block doesn't work here; switch back with .cv source both and report it.");
            if (modOnly && waiting) BlockedRoutes.Add(key); else BlockedRoutes.Remove(key);
            if (SpawnRange.SourceTarget(modOnly, waiting, range.x, range.y) is not float y) continue;
            SetRangeY(em, r.Patrol, sp, range.x, y);
            if (SpawnRange.IsBlocked(range.x, y)) blocked++; else opened++;
        }
        if (blocked > 0 || opened > 0)
            Plugin.Trace($"CaravanTracker: source {Settings.Source.Value}: game spawning turned off on {blocked} routes, back on for {opened}");
        return blocked;
    }
}
