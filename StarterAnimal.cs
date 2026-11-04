using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Shared;
using Stunlock.Core;
using Unity.Entities;

namespace CarriageTracker;

// The caravan's animal (horse or ox). On 01.11 and 02.11 the animal of squads spawned right after a wake took no
// damage, even after its cart was destroyed; squads that set out from their first stop could be killed. The log of
// 1.0.10 showed why: Immortal was false, but the game's spawn buff Buff_General_Spawn_Unit, which lasts a few
// seconds, was still on that animal at assembly and after the cart was lost, and only on woken starts. So a spawn
// buff left on any unit or cart of a start SpawnBuffMax after it appeared is removed (RemoveSpawnBuffs). The
// animal's state is still written to the log at assembly and when the cart is lost, and an animal still immortal
// after losing its cart is made mortal: the rest of a robbed caravan must be killable.
static partial class Starter
{
    static Entity AnimalOf(EntityManager em, PrefabCollectionSystem prefabs, List<Entity> units)
    {
        if (prefabs == null) return Entity.Null;
        foreach (var u in units)
            if (Caravans.Has<PrefabGUID>(em, u) && Caravans.Classify(prefabs, em.GetComponentData<PrefabGUID>(u)).Kind == Kind.Horse)
                return u;
        return Entity.Null;
    }

    static bool IsImmortal(EntityManager em, Entity unit) =>
        Caravans.Has<Immortal>(em, unit) && em.GetComponentData<Immortal>(unit).IsImmortal;

    // "immortal=False, buffs: A, B" for the log.
    static string AnimalState(EntityManager em, PrefabCollectionSystem prefabs, Entity unit)
    {
        var names = new List<string>();
        if (em.HasBuffer<BuffBuffer>(unit))
        {
            var buffs = em.GetBuffer<BuffBuffer>(unit);
            for (int i = 0; i < buffs.Length && names.Count < 12; i++)
            {
                var guid = buffs[i].PrefabGuid;
                names.Add(prefabs != null && prefabs._PrefabLookupMap.TryGetName(guid, out string n) ? n : guid.GuidHash.ToString());
            }
        }
        return $"immortal={IsImmortal(em, unit)}, buffs: {(names.Count == 0 ? "none" : string.Join(", ", names))}";
    }

    // Removes the game's spawn buffs (Buff_General_Spawn_Unit and its kin) from a unit or cart of a start that
    // appeared long enough ago (RunLogic.PastSpawn). Written to the log once per unit.
    static void RemoveSpawnBuffs(EntityManager em, PrefabCollectionSystem prefabs, Run run, Entity unit, DateTime now)
    {
        if (prefabs == null || !em.Exists(unit) || !em.HasBuffer<BuffBuffer>(unit)) return;
        var buffs = em.GetBuffer<BuffBuffer>(unit);
        var drop = new List<(Entity Buff, string Name)>();
        for (int i = 0; i < buffs.Length; i++)
        {
            if (!prefabs._PrefabLookupMap.TryGetName(buffs[i].PrefabGuid, out string name) || name == null) continue;
            if (name.StartsWith("Buff_General_Spawn_", StringComparison.Ordinal)) drop.Add((buffs[i].Entity, name));
        }
        foreach (var (buff, name) in drop)
        {
            if (buff == Entity.Null || !em.Exists(buff)) continue;
            DestroyUtility.Destroy(em, buff, DestroyDebugReason.TryRemoveBuff);
            if (!run.BuffsRemoved.Add(Key(unit))) continue;
            string what = Caravans.Has<PrefabGUID>(em, unit) && prefabs._PrefabLookupMap.TryGetName(em.GetComponentData<PrefabGUID>(unit), out string n) ? n : "unit";
            Plugin.Warn($"CarriageTracker start: {Id(run)}: {name} was still on {what} {unit.Index}:{unit.Version} " +
                        $"{Math.Round((now - run.State.SpawnedAt).TotalSeconds)} s after the squad appeared; removed it");
        }
    }

    const int CarriageConnectBuff = 1084521444; // Buff_CarriageHorse_Connect: the animal pulls its cart

    static bool Hitched(EntityManager em, Entity animal)
    {
        if (!em.HasBuffer<BuffBuffer>(animal)) return false;
        var buffs = em.GetBuffer<BuffBuffer>(animal);
        for (int i = 0; i < buffs.Length; i++)
            if (buffs[i].PrefabGuid.GuidHash == CarriageConnectBuff) return true;
        return false;
    }

    // Once a second: the animal's health may only go down (RunLogic.AnimalHealth, setting AnimalKeepsDamage).
    // A heal of 5% of its health or more at once is written to the log (the first five of a run), with whether the
    // animal was hitched to its cart then; smaller ones (its regeneration, about 1 point a second, 04.11) are counted
    // and told when the run finishes.
    static void KeepAnimalDamage(EntityManager em, PrefabCollectionSystem prefabs, Run run, List<Entity> units)
    {
        var animal = AnimalOf(em, prefabs, units);
        if (animal == Entity.Null || !Caravans.Has<Health>(em, animal)) return;
        var health = em.GetComponentData<Health>(animal);
        var back = RunLogic.AnimalHealth(run.State, Key(animal), health.Value);
        if (back is not float lowest) return;
        float was = health.Value;
        health.Value = lowest;
        em.SetComponentData(animal, health);
        float max = health.MaxHealth._Value;
        if (was - lowest < 0.05f * max) { run.AnimalRegen++; return; }
        if (++run.AnimalHeals <= 5)
            Plugin.Info($"CarriageTracker start: {Id(run)}: the animal healed from {Math.Round(lowest)} to {Math.Round(was)} " +
                        $"of {Math.Round(max)} at once (hitched to its cart: {Hitched(em, animal)}); set back to {Math.Round(lowest)}");
    }

    static void LogAnimal(EntityManager em, PrefabCollectionSystem prefabs, Run run, List<Entity> units, string when)
    {
        var animal = AnimalOf(em, prefabs, units);
        if (animal == Entity.Null) return;
        Plugin.Info($"CarriageTracker start: {Id(run)}: animal {when}: {AnimalState(em, prefabs, animal)}");
    }

    // After its cart is gone the animal must be killable.
    static void MakeAnimalMortal(EntityManager em, PrefabCollectionSystem prefabs, Run run, List<Entity> units)
    {
        var animal = AnimalOf(em, prefabs, units);
        if (animal == Entity.Null || !IsImmortal(em, animal)) return;
        var immortal = em.GetComponentData<Immortal>(animal);
        immortal.IsImmortal = false;
        em.SetComponentData(animal, immortal);
        Plugin.Warn($"CarriageTracker start: {Id(run)}: the animal was still immortal after losing its cart; made it mortal");
    }
}
