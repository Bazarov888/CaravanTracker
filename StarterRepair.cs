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

// Giving routes the game's normal settings back: after a crash, and before the mod is removed (.cv cleanup).
static partial class Starter
{
    // A numeric spawner field as double; NaN if it isn't a number.
    static double Num(object o)
    {
        try { return o is IConvertible c ? Convert.ToDouble(c) : double.NaN; }
        catch { return double.NaN; }
    }

    static bool TimersZero(object boxed) =>
        SpawnRange.TimersZero(Num(Caravans.Field(boxed, "MinRespawnTime")), Num(Caravans.Field(boxed, "MaxRespawnTime")));

    static void NormalTimers(object boxed)
    {
        SetField(boxed, "RespawnTimeModifier", 1);
        SetField(boxed, "MinRespawnTime", SpawnRange.NormalMinRespawn);
        SetField(boxed, "MaxRespawnTime", SpawnRange.NormalMaxRespawn);
        ResetField(boxed, "RespawnTime");
    }

    // After a crash or restart in the middle of a start, a route may keep the start settings: no freeze
    // without players, zero timers, endless player range. Its units and carts may stay without the freeze
    // too. Such routes and units get the game's normal values back.
    public static int RepairLeftovers(EntityManager em, PrefabCollectionSystem prefabs)
    {
        int repaired = 0;
        var tag = Tag("DisableWhenNoPlayersInRange");
        foreach (var r in Routes.All(em, prefabs))
        {
            if (IsRunning(r.Patrol) || !em.Exists(r.Patrol)) continue;
            if (tag.HasValue)
            {
                var units = Routes.ActiveUnits(em, r.Patrol);
                var all = new List<Entity>(units);
                if (units.Count > 0) all.AddRange(Routes.CartsOf(em, units));
                int fixedUnits = 0;
                foreach (var e in all)
                    if (!em.HasComponent(e, tag.Value)) { AddTag(em, e, "DisableWhenNoPlayersInRange"); fixedUnits++; }
                if (fixedUnits > 0)
                    Plugin.Info($"CaravanTracker: route {r.Key}: {fixedUnits} units and carts left without freezing, restored");
            }
            var sp = em.GetComponentData<UnitCompositionSpawner>(r.Patrol);
            object boxed = sp;
            bool farRange = Caravans.Field(boxed, "PlayerRange") is float2 range && SpawnRange.IsStart(range.x, range.y);
            bool zeroTimers = TimersZero(boxed);
            bool noTag = tag.HasValue && !em.HasComponent(r.Patrol, tag.Value);
            if (!farRange && !zeroTimers && !noTag) continue;
            if (farRange) SetField(boxed, "PlayerRange", new float2(0f, SpawnRange.NormalY));
            if (zeroTimers) NormalTimers(boxed);
            else ResetField(boxed, "RespawnTime");
            em.SetComponentData(r.Patrol, (UnitCompositionSpawner)boxed);
            AddTag(em, r.Patrol, "DisableWhenNoPlayersInRange");
            repaired++;
            Plugin.Info($"CaravanTracker: route {r.Key} patrol {r.Patrol.Index}:{r.Patrol.Version} had start settings left (range {farRange}, timers {zeroTimers}, no freeze {noTag}), restored");
        }
        return repaired;
    }

    // After .cv cleanup the mod changes nothing more in the world until the server restarts.
    public static bool Paused { get; private set; }

    // .cv cleanup: everything the mod changed in the world goes back to the game's own state, so the mod
    // can be removed: starts are stopped, blocked or start ranges opened, zero timers restored, freezing
    // without players given back to routes, units and carts. The tracker then removes the mod's icons.
    public static (int Runs, int Routes, int Units) Cleanup(EntityManager em, PrefabCollectionSystem prefabs)
    {
        Paused = true;
        int runs = StopAll(em);
        int routes = 0, units = 0;
        var tag = Tag("DisableWhenNoPlayersInRange");
        foreach (var r in Routes.All(em, prefabs))
        {
            if (!em.Exists(r.Patrol)) continue;
            var sp = em.GetComponentData<UnitCompositionSpawner>(r.Patrol);
            object boxed = sp;
            bool changed = false;
            if (Caravans.Field(boxed, "PlayerRange") is float2 range && SpawnRange.CleanupOpens(range.x, range.y))
            {
                SetField(boxed, "PlayerRange", new float2(0f, SpawnRange.NormalY));
                changed = true;
            }
            if (TimersZero(boxed))
            {
                NormalTimers(boxed);
                changed = true;
            }
            if (changed)
            {
                em.SetComponentData(r.Patrol, (UnitCompositionSpawner)boxed);
                routes++;
            }
            if (!tag.HasValue) continue;
            AddTag(em, r.Patrol, "DisableWhenNoPlayersInRange");
            var squad = Routes.ActiveUnits(em, r.Patrol);
            var all = new List<Entity>(squad);
            if (squad.Count > 0) all.AddRange(Routes.CartsOf(em, squad));
            foreach (var e in all)
                if (!em.HasComponent(e, tag.Value)) { AddTag(em, e, "DisableWhenNoPlayersInRange"); units++; }
        }
        BlockedRoutes.Clear();
        return (runs, routes, units);
    }
}
