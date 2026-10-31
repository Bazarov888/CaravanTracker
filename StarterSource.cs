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
    // Such routes (waiting for spawn, not started by the mod) get a player range of (0, 0.001): the game
    // would spawn only for a player within 1 mm of the patrol point, which doesn't happen. The range is kept
    // above zero on purpose, so that the game can't take it for "no limit". Every other route, and every
    // route with Game or Both, gets the game's range (0, 90) back. A start by the mod sets its own range.
    // If a squad still appears on a blocked route, the block doesn't work on this game version: a warning
    // goes to the log once per route.
    static readonly float2 BlockedRange = new float2(0f, 0.001f);
    static readonly float2 NormalRange = new float2(0f, 90f);
    static readonly HashSet<(int, int)> BlockedRoutes = new();
    static readonly HashSet<(int, int)> WarnedRoutes = new();

    static bool IsBlockedRange(float2 r) => r.y > 0f && r.y < 0.01f;

    // Source = Mod: right after a start ends, a route that waits for a new caravan is blocked at once,
    // not at the next check, so that the game can't spawn its own caravan there in between.
    static void BlockIfWaiting(EntityManager em, Entity patrol)
    {
        if (Paused || !Settings.ModOnly || !em.Exists(patrol)) return;
        var sp = em.GetComponentData<UnitCompositionSpawner>(patrol);
        if (sp.SpawnerState.ToString() != "WaitingForSpawn") return;
        object boxed = sp;
        SetField(boxed, "PlayerRange", BlockedRange);
        em.SetComponentData(patrol, (UnitCompositionSpawner)boxed);
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
                Plugin.Logger.LogWarning($"CaravanTracker: route {r.Key}: Source = Mod, but the game spawned a caravan on it by itself " +
                                         $"(range {range.x}-{range.y}). The block doesn't work here; switch back with .cv source both and report it.");
            bool block = modOnly && waiting;
            if (block) BlockedRoutes.Add(key); else BlockedRoutes.Remove(key);
            bool isBlocked = IsBlockedRange(range) || (range.x == 0f && range.y == 0f);
            if (block == isBlocked && !(block && range.y == 0f)) continue;
            object boxed = sp;
            SetField(boxed, "PlayerRange", block ? BlockedRange : NormalRange);
            em.SetComponentData(r.Patrol, (UnitCompositionSpawner)boxed);
            if (block) blocked++; else opened++;
        }
        if (blocked > 0 || opened > 0)
            Plugin.Trace($"CaravanTracker: source {Settings.Source.Value}: game spawning turned off on {blocked} routes, back on for {opened}");
        return blocked;
    }
}
