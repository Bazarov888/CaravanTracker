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

// Caravan routes. Each route is a game patrol (GlobalPatrolState + UnitCompositionSpawner)
// whose squad includes the horse CHAR_CarriageHorse_<route>.
static class Routes
{
    public record Route(Entity Patrol, string Key);

    static List<Route> _cache;
    static DateTime _cacheAt;

    // Routes don't change while the server runs, so the list is kept. It is built again every
    // 10 minutes or as soon as one of its patrols no longer exists.
    public static List<Route> All(EntityManager em, PrefabCollectionSystem prefabs)
    {
        var now = DateTime.UtcNow;
        if (_cache == null || now >= _cacheAt.AddMinutes(10) || !_cache.TrueForAll(r => em.Exists(r.Patrol)))
        {
            _cache = Scan(em, prefabs);
            _cacheAt = now;
        }
        return new List<Route>(_cache);
    }

    static List<Route> Scan(EntityManager em, PrefabCollectionSystem prefabs)
    {
        var result = new List<Route>();
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<GlobalPatrolState>()))
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<UnitCompositionSpawner>()))
            .WithOptions(EntityQueryOptions.IncludeDisabled);
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var patrols = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var p in patrols)
            {
                if (!em.HasBuffer<UnitCompositionGroupUnitEntry>(p)) continue;
                var units = em.GetBuffer<UnitCompositionGroupUnitEntry>(p);
                for (int i = 0; i < units.Length; i++)
                {
                    object unit = units[i].Unit;
                    if (unit is not PrefabGUID guid || !prefabs._PrefabLookupMap.TryGetName(guid, out string name)) continue;
                    const string prefix = "CHAR_CarriageHorse_";
                    if (name == null || !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    result.Add(new Route(p, name.Substring(prefix.Length)));
                    break;
                }
            }
        }
        finally
        {
            patrols.Dispose();
            query.Dispose();
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return result;
    }

    internal static Entity AsEntity(object o) =>
        o is Entity e ? e : o is NetworkedEntity ne ? ne.GetEntityOnServer() : Entity.Null;

    // Living units of the squad (horse, guards), from the UnitCompositionActiveUnit buffer.
    public static List<Entity> ActiveUnits(EntityManager em, Entity patrol)
    {
        var list = new List<Entity>();
        if (!em.HasBuffer<UnitCompositionActiveUnit>(patrol)) return list;
        var buf = em.GetBuffer<UnitCompositionActiveUnit>(patrol);
        for (int i = 0; i < buf.Length; i++)
        {
            var e = AsEntity(buf[i].UnitEntity);
            if (e != Entity.Null && em.Exists(e)) list.Add(e);
        }
        return list;
    }

    // Carts pulled by the horses in the list.
    public static List<Entity> CartsOf(EntityManager em, List<Entity> units)
    {
        var horses = new HashSet<(int, int)>();
        foreach (var u in units) horses.Add((u.Index, u.Version));
        var carts = new List<Entity>();
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<Script_CarriageData>()))
            .WithOptions(EntityQueryOptions.IncludeDisabled);
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var arr = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var c in arr)
            {
                var horse = em.GetComponentData<Script_CarriageData>(c).Horse.GetEntityOnServer();
                if (horses.Contains((horse.Index, horse.Version))) carts.Add(c);
            }
        }
        finally
        {
            arr.Dispose();
            query.Dispose();
        }
        return carts;
    }

    public static string MoveStatus(EntityManager em, Entity patrol) =>
        Caravans.Has<MovePatrolState>(em, patrol) ? em.GetComponentData<MovePatrolState>(patrol).Status.ToString() : "?";

    // Where a patrol is on its path. Index: the stop it stands at or heads to, from 0 (the first stop)
    // to Count - 1 (the last one, where the game removes the caravan). Count: stops of the path, 0 if unknown.
    // Number: the same stop counted from 1, as shown to players.
    public record Progress(int Index, int Start, int Count)
    {
        public bool AtStart => Index == Start;
        public int Number => Index + 1;
        public int Left => Count > 0 ? Math.Max(0, Count - 1 - Index) : 0;
        public string Log => Count > 0 ? $"{Number}/{Count}" : $"{Number}/?";
    }

    public static Progress StopsOf(EntityManager em, Entity patrol)
    {
        if (patrol == Entity.Null || !em.Exists(patrol) || !Caravans.Has<GlobalPatrolState>(em, patrol)) return null;
        var gp = em.GetComponentData<GlobalPatrolState>(patrol);
        return new Progress((int)gp.CurrentBusStopIndex, (int)gp.StartBusStopIndex, StopCount(em, patrol));
    }

    // "stop 3 of 7" for chat, empty if unknown.
    public static string StopText(Progress p) =>
        p == null ? "" : p.Count > 0 ? L.T("stops.of", p.Number, p.Count) : L.T("stops.at", p.Number);

    static Type _stopType;
    static bool _stopTypeLooked;
    static System.Reflection.MethodInfo _getBuffer;
    static readonly Dictionary<(int, int), int> StopCounts = new();

    // Number of stops of a patrol's path: length of its PatrolBusStopNode buffer. The path doesn't
    // change, so it is read once per patrol. The type is looked up by name, as its namespace may differ.
    static int StopCount(EntityManager em, Entity patrol)
    {
        var key = (patrol.Index, patrol.Version);
        if (StopCounts.TryGetValue(key, out int n)) return n;
        n = 0;
        try
        {
            if (!_stopTypeLooked)
            {
                _stopType = GameTypes.Find("PatrolBusStopNode");
                _stopTypeLooked = true;
                if (_stopType == null) Plugin.Logger.LogWarning("CaravanTracker: game type PatrolBusStopNode not found, stop counts unknown");
            }
            if (_stopType != null && em.HasComponent(patrol, ComponentType.ReadOnly(Il2CppType.From(_stopType))))
            {
                if (_getBuffer == null)
                    foreach (var m in typeof(EntityManager).GetMethods())
                        if (m.Name == "GetBuffer" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2) { _getBuffer = m; break; }
                if (_getBuffer != null)
                {
                    object buffer = _getBuffer.MakeGenericMethod(_stopType).Invoke(em, new object[] { patrol, true });
                    n = (int)buffer.GetType().GetProperty("Length").GetValue(buffer);
                }
            }
        }
        catch (Exception e)
        {
            var inner = e.InnerException ?? e;
            Plugin.Logger.LogWarning($"CaravanTracker: stops of patrol {patrol.Index}:{patrol.Version} unknown: {inner.Message}");
        }
        StopCounts[key] = n;
        return n;
    }

    public static string Describe(EntityManager em, Entity patrol, int units)
    {
        string spawner = em.GetComponentData<UnitCompositionSpawner>(patrol).SpawnerState.ToString();
        string move = MoveStatus(em, patrol);
        if (move == "WaitForDespawn")
            return units > 0 ? L.T("route.state.endguarded") : L.T("route.state.endempty");
        // The cart was destroyed: the horse (and maybe some guards) walk on to the end of the route.
        if (units > 0 && CartsOf(em, ActiveUnits(em, patrol)).Count == 0) return L.T("route.state.nocart");
        if (spawner == "WaitingForSpawn") return L.T("route.state.waiting");
        if (units > 0 && move == "Moving") return L.T("route.state.moving");
        return L.T("route.state.other", spawner, move, units);
    }
}
