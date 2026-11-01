using System.Collections.Generic;
using ProjectM;
using Stunlock.Core;
using Unity.Entities;

namespace CaravanTracker;

// The caravan's animal (horse or ox). On 01.11 the animal of squads spawned right after a wake took no damage,
// even after its cart was destroyed; on 31.10 it could be killed. Whether the game held it immortal (Immortal
// component) or a buff did is written to the log when the squad is assembled and when the cart is lost, and an
// animal still immortal after losing its cart is made mortal: the rest of a robbed caravan must be killable.
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

    static void LogAnimal(EntityManager em, PrefabCollectionSystem prefabs, Run run, List<Entity> units, string when)
    {
        var animal = AnimalOf(em, prefabs, units);
        if (animal == Entity.Null) return;
        Plugin.Info($"CaravanTracker start: {Id(run)}: animal {when}: {AnimalState(em, prefabs, animal)}");
    }

    // After its cart is gone the animal must be killable.
    static void MakeAnimalMortal(EntityManager em, PrefabCollectionSystem prefabs, Run run, List<Entity> units)
    {
        var animal = AnimalOf(em, prefabs, units);
        if (animal == Entity.Null || !IsImmortal(em, animal)) return;
        var immortal = em.GetComponentData<Immortal>(animal);
        immortal.IsImmortal = false;
        em.SetComponentData(animal, immortal);
        Plugin.Warn($"CaravanTracker start: {Id(run)}: the animal was still immortal after losing its cart; made it mortal");
    }
}
