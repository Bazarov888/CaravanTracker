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

namespace CarriageTracker;

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

    // Routes whose settings couldn't be given back at the end of a start: repaired again every 5 seconds
    // (from the Source pass of the Ticker) until it works.
    static readonly Dictionary<(int, int), string> RepairQueue = new();

    static void QueueRepair(Entity patrol, string route) => RepairQueue[Key(patrol)] = route;

    public static void RetryRepairs(EntityManager em, PrefabCollectionSystem prefabs)
    {
        if (RepairQueue.Count == 0) return;
        foreach (var r in Routes.All(em, prefabs))
        {
            if (!RepairQueue.ContainsKey(Key(r.Patrol))) continue;
            if (IsRunning(r.Patrol)) { RepairQueue.Remove(Key(r.Patrol)); continue; }
            try
            {
                RepairRoute(em, r);
                RepairQueue.Remove(Key(r.Patrol));
                Plugin.Info($"CarriageTracker: route {r.Key} patrol {r.Patrol.Index}:{r.Patrol.Version} got its settings back on a retry");
            }
            catch (Exception e) { Plugin.Trace($"CarriageTracker: route {r.Key} still can't be repaired: {e.Message}"); }
        }
        // A patrol that no longer exists has nothing to repair.
        foreach (var key in new List<(int, int)>(RepairQueue.Keys))
            if (!Routes.All(em, prefabs).Exists(r => Key(r.Patrol) == key)) RepairQueue.Remove(key);
    }

    // After a crash or restart in the middle of a start, a route may keep the start settings: no freeze
    // without players, zero timers, endless player range. Its units and carts may stay without the freeze
    // too. Such routes and units get the game's normal values back. One route that fails doesn't stop the
    // others; it is queued and repaired again every 5 seconds. Returns the number of routes repaired.
    public static int RepairLeftovers(EntityManager em, PrefabCollectionSystem prefabs)
    {
        int repaired = 0;
        foreach (var r in Routes.All(em, prefabs))
        {
            if (IsRunning(r.Patrol) || !em.Exists(r.Patrol)) continue;
            try { if (RepairRoute(em, r)) repaired++; }
            catch (Exception e)
            {
                QueueRepair(r.Patrol, r.Key);
                Plugin.Error($"CarriageTracker: repair of route {r.Key} failed, will try again: {e.Message}");
            }
        }
        return repaired;
    }

    // One route: true if something had to be changed.
    static bool RepairRoute(EntityManager em, Routes.Route r)
    {
        var tag = Tag("DisableWhenNoPlayersInRange");
        if (tag.HasValue)
        {
            var units = Routes.ActiveUnits(em, r.Patrol);
            var all = new List<Entity>(units);
            if (units.Count > 0) all.AddRange(Routes.CartsOf(em, units));
            int fixedUnits = 0;
            foreach (var e in all)
                if (!em.HasComponent(e, tag.Value)) { AddTag(em, e, "DisableWhenNoPlayersInRange"); fixedUnits++; }
            if (fixedUnits > 0)
                Plugin.Info($"CarriageTracker: route {r.Key}: {fixedUnits} units and carts left without freezing, restored");
        }
        var sp = em.GetComponentData<UnitCompositionSpawner>(r.Patrol);
        object boxed = sp;
        bool farRange = Caravans.Field(boxed, "PlayerRange") is float2 range && SpawnRange.IsStart(range.x, range.y);
        bool zeroTimers = TimersZero(boxed);
        bool noTag = tag.HasValue && !em.HasComponent(r.Patrol, tag.Value);
        if (!farRange && !zeroTimers && !noTag) return false;
        if (farRange) SetField(boxed, "PlayerRange", new float2(0f, SpawnRange.NormalY));
        if (zeroTimers) NormalTimers(boxed);
        else ResetField(boxed, "RespawnTime");
        em.SetComponentData(r.Patrol, (UnitCompositionSpawner)boxed);
        AddTag(em, r.Patrol, "DisableWhenNoPlayersInRange");
        Plugin.Info($"CarriageTracker: route {r.Key} patrol {r.Patrol.Index}:{r.Patrol.Version} had start settings left (range {farRange}, timers {zeroTimers}, no freeze {noTag}), restored");
        return true;
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
        RepairQueue.Clear();
        return (runs, routes, units);
    }
}
