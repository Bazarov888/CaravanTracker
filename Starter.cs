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

// Forced caravan start. The game updates a patrol and spawns its squad only when a player is near.
// Here the patrol is unfrozen, spawning is allowed without a player and without waiting, and the new
// units are kept from being frozen. While a start is running, it is checked once a second.
// Once a guard of the started squad is gone (killed), the game must not send replacements: such
// respawned units are removed at once, otherwise every kill spawns a new guard and memory runs out.
static class Starter
{
    class Run
    {
        public Entity Patrol;
        public string Route;
        public Entity User;
        public DateTime Started;
        public bool Spawned;
        public bool Restored;
        public bool Auto;
        public int Act;
        public string LastState = "";
        public Dictionary<string, object> Saved = new();
        public readonly HashSet<(int, int)> Known = new();
        public readonly HashSet<(int, int)> Awake = new();
        public bool LostAny;
        public int Blocked;
        public int OverLimit;
        public bool HadCart;
        public bool CartGone;
        public bool Reset;
    }

    static DateTime _nextUpdate;
    static (int, int) Key(Entity e) => (e.Index, e.Version);

    static readonly List<Run> Runs = new();
    static readonly string[] TimingFields = { "RespawnTimeModifier", "MinRespawnTime", "MaxRespawnTime" };

    public static bool Any => Runs.Count > 0;
    public static bool IsRunning(Entity patrol) => Runs.Exists(r => r.Patrol == patrol);

    // A route whose start failed (no squad appeared) is skipped by auto-start for an hour,
    // so one broken route doesn't take every start of its act.
    static readonly Dictionary<string, DateTime> FailedAt = new();
    public static bool RecentlyFailed(string route) =>
        FailedAt.TryGetValue(route, out var t) && DateTime.UtcNow < t.AddMinutes(60);

    // Caravans started by auto-start that still have their cart: all of them (act = 0) or in one act.
    // A run whose cart is destroyed goes on until its last units reach the end of the route,
    // but it no longer takes a place in the auto-start limits.
    public static int AutoCount(int act = 0)
    {
        int n = 0;
        foreach (var r in Runs) if (r.Auto && !r.CartGone && (act == 0 || r.Act == act)) n++;
        return n;
    }

    static readonly Dictionary<string, ComponentType?> Tags = new();

    static ComponentType? Tag(string name)
    {
        if (Tags.TryGetValue(name, out var cached)) return cached;
        var t = GameTypes.Find(name);
        ComponentType? ct = t == null ? null : ComponentType.ReadWrite(Il2CppType.From(t));
        if (t == null) Plugin.Logger.LogWarning($"CaravanTracker start: game type {name} not found");
        Tags[name] = ct;
        return ct;
    }

    static void RemoveTag(EntityManager em, Entity e, string name)
    {
        var ct = Tag(name);
        if (ct.HasValue && em.HasComponent(e, ct.Value)) em.RemoveComponent(e, ct.Value);
    }

    static void AddTag(EntityManager em, Entity e, string name)
    {
        var ct = Tag(name);
        if (ct.HasValue && !em.HasComponent(e, ct.Value)) em.AddComponent(e, ct.Value);
    }

    static void SetField(object boxed, string name, object value)
    {
        try
        {
            var f = boxed.GetType().GetField(name);
            if (f == null) { Plugin.Logger.LogWarning($"CaravanTracker start: spawner field {name} not found"); return; }
            if (value != null && f.FieldType != value.GetType() && value is IConvertible) value = Convert.ChangeType(value, f.FieldType);
            f.SetValue(boxed, value);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"CaravanTracker start: can't set spawner field {name}: {e.Message}");
        }
    }

    // A unit must not freeze without players. If the game already froze it and moved it
    // to (10000, 10000), unfreeze it and put it back at the patrol.
    static void KeepAwake(EntityManager em, Run run, Entity unit, float3 patrolPos)
    {
        if (run.Awake.Add(Key(unit))) RemoveTag(em, unit, "DisableWhenNoPlayersInRange");
        var frozen = Tag("DisabledDueToNoPlayersInRange");
        if (frozen.HasValue && em.HasComponent(unit, frozen.Value))
        {
            em.RemoveComponent(unit, frozen.Value);
            em.SetEnabled(unit, true);
            if (!Caravans.OnMap(Caravans.PositionOf(em, unit)))
            {
                if (Caravans.Has<Translation>(em, unit)) em.SetComponentData(unit, new Translation { Value = patrolPos });
                if (Caravans.Has<LocalTransform>(em, unit))
                {
                    var lt = em.GetComponentData<LocalTransform>(unit);
                    lt.Position = patrolPos;
                    em.SetComponentData(unit, lt);
                }
            }
            Plugin.Trace($"CaravanTracker start: unit {unit.Index}:{unit.Version} was frozen, woke it up");
        }
    }

    public static string Start(EntityManager em, Routes.Route route, Entity userEntity, bool auto = false, int act = 0)
    {
        var patrol = route.Patrol;
        string name = L.Route(route.Key);
        if (IsRunning(patrol)) return L.T("start.already", name);
        var run = new Run { Patrol = patrol, Route = route.Key, User = userEntity, Started = DateTime.UtcNow, Auto = auto, Act = act };

        // 1. The old squad (if stuck) is removed completely, together with its carts.
        var units = Routes.ActiveUnits(em, patrol);
        var carts = Routes.CartsOf(em, units);
        foreach (var e in carts) DestroyUtility.Destroy(em, e);
        foreach (var e in units) DestroyUtility.Destroy(em, e);

        // A route left mid-way by an earlier caravan is put back to the first stop of its path.
        var before = Routes.StopsOf(em, patrol);
        var posBefore = Caravans.PositionOf(em, patrol);
        bool reset = Settings.ResetToStart.Value && ResetToStart(em, patrol, before);
        run.Reset = reset;

        // 2. Spawning without a player nearby and without waiting. Old values are saved to restore them.
        var sp = em.GetComponentData<UnitCompositionSpawner>(patrol);
        object boxed = sp;
        var rangeField = boxed.GetType().GetField("PlayerRange");
        run.Saved["PlayerRange"] = rangeField?.GetValue(boxed);
        SetField(boxed, "PlayerRange", new float2(0f, 100000f));
        foreach (var f in TimingFields)
        {
            run.Saved[f] = boxed.GetType().GetField(f)?.GetValue(boxed);
            SetField(boxed, f, 0);
        }
        // A respawn time the game scheduled earlier (up to 45 minutes ahead, after the previous squad was
        // destroyed) would hold the new squad back until then, so it is cleared.
        ResetField(boxed, "RespawnTime");
        em.SetComponentData(patrol, (UnitCompositionSpawner)boxed);

        // 3. Unfreeze the patrol and keep the game from freezing it again.
        RemoveTag(em, patrol, "DisableWhenNoPlayersInRange");
        RemoveTag(em, patrol, "DisabledDueToNoPlayersInRange");
        em.SetEnabled(patrol, true);

        // The squad appears at the stop the route stands at.
        var stops = Routes.StopsOf(em, patrol);
        Runs.Add(run);
        Plugin.Logger.LogInfo($"CaravanTracker start: patrol {patrol.Index}:{patrol.Version} route {route.Key}{(auto ? $" (auto, act {act})" : "")}, " +
                              $"stop {stops?.Log}{(reset ? $" (put back from stop {before?.Log})" : "")} at ({Math.Round(posBefore.x)}, {Math.Round(posBefore.z)}), " +
                              $"removed {units.Count} units and {carts.Count} carts");
        bool midway = stops != null && !stops.AtStart && Routes.MoveStatus(em, patrol) != "WaitForDespawn";
        return L.T("start.ok", name, units.Count, carts.Count, Routes.StopText(stops)) +
               (reset ? L.T("start.reset", Routes.StopText(before)) : midway ? L.T("start.midway") : "");
    }

    // Puts a route that stands mid-way back to the first stop of its path, the same way the game does it
    // after a caravan reaches the end: stop index to the start, movement waiting for a new path. The game
    // spawns a squad at the stop the route stands at, so the caravan then sets out from the start.
    // A route at its last stop (WaitForDespawn) isn't touched: the game returns it to the start itself.
    static bool ResetToStart(EntityManager em, Entity patrol, Routes.Progress stops)
    {
        if (stops == null || stops.AtStart || Routes.MoveStatus(em, patrol) == "WaitForDespawn") return false;
        try
        {
            object gp = em.GetComponentData<GlobalPatrolState>(patrol);
            SetField(gp, "CurrentBusStopIndex", stops.Start);
            SetField(gp, "TargetBusStopIndex", stops.Start);
            em.SetComponentData(patrol, (GlobalPatrolState)gp);
            object mp = em.GetComponentData<MovePatrolState>(patrol);
            var status = mp.GetType().GetField("Status");
            if (status != null && status.FieldType.IsEnum && Enum.IsDefined(status.FieldType, "WaitingForPath"))
            {
                status.SetValue(mp, Enum.Parse(status.FieldType, "WaitingForPath"));
                em.SetComponentData(patrol, (MovePatrolState)mp);
            }
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"CaravanTracker start: can't put patrol {patrol.Index}:{patrol.Version} back to the start: {e.Message}");
            return false;
        }
    }

    static void Restore(EntityManager em, Run run)
    {
        if (run.Restored || !em.Exists(run.Patrol)) return;
        var sp = em.GetComponentData<UnitCompositionSpawner>(run.Patrol);
        object boxed = sp;
        foreach (var kv in run.Saved)
            if (kv.Value != null) SetField(boxed, kv.Key, kv.Value);
        ResetField(boxed, "RespawnTime");
        em.SetComponentData(run.Patrol, (UnitCompositionSpawner)boxed);
        run.Restored = true;
    }

    // Sets a field to its empty value (for RespawnTime: "no respawn scheduled"), so the game computes
    // the next respawn again from the restored timers instead of a time computed while they were zero.
    static void ResetField(object boxed, string name)
    {
        try
        {
            var f = boxed.GetType().GetField(name);
            if (f == null) { Plugin.Logger.LogWarning($"CaravanTracker start: spawner field {name} not found"); return; }
            f.SetValue(boxed, f.FieldType.IsValueType ? Activator.CreateInstance(f.FieldType) : null);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"CaravanTracker start: can't reset spawner field {name}: {e.Message}");
        }
    }

    static bool IsZero(object o)
    {
        try { return o is IConvertible c && Convert.ToDouble(c) == 0; }
        catch { return false; }
    }

    // After a crash or restart in the middle of a start, a route may keep the start settings: no freeze
    // without players, zero timers, endless player range. Such routes get the game's normal values back.
    public static int RepairLeftovers(EntityManager em, PrefabCollectionSystem prefabs)
    {
        int repaired = 0;
        var tag = Tag("DisableWhenNoPlayersInRange");
        foreach (var r in Routes.All(em, prefabs))
        {
            if (IsRunning(r.Patrol) || !em.Exists(r.Patrol)) continue;
            var sp = em.GetComponentData<UnitCompositionSpawner>(r.Patrol);
            object boxed = sp;
            bool farRange = Caravans.Field(boxed, "PlayerRange") is float2 range && range.y > 1000f;
            bool zeroTimers = IsZero(Caravans.Field(boxed, "MinRespawnTime")) && IsZero(Caravans.Field(boxed, "MaxRespawnTime"));
            bool noTag = tag.HasValue && !em.HasComponent(r.Patrol, tag.Value);
            if (!farRange && !zeroTimers && !noTag) continue;
            if (farRange) SetField(boxed, "PlayerRange", new float2(0f, 90f));
            if (zeroTimers)
            {
                SetField(boxed, "RespawnTimeModifier", 1);
                SetField(boxed, "MinRespawnTime", 1000);
                SetField(boxed, "MaxRespawnTime", 2700);
            }
            ResetField(boxed, "RespawnTime");
            em.SetComponentData(r.Patrol, (UnitCompositionSpawner)boxed);
            AddTag(em, r.Patrol, "DisableWhenNoPlayersInRange");
            repaired++;
            Plugin.Logger.LogInfo($"CaravanTracker: route {r.Key} patrol {r.Patrol.Index}:{r.Patrol.Version} had start settings left (range {farRange}, timers {zeroTimers}, no freeze {noTag}), restored");
        }
        return repaired;
    }

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

    public static int ApplySource(EntityManager em, PrefabCollectionSystem prefabs)
    {
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

    // End of a run: the route gets its settings back, and units that are still alive (the run timed out
    // or was stopped by command) may freeze without players again, as all units of the game do.
    static void Finish(EntityManager em, Run run, string why)
    {
        Restore(em, run);
        int back = 0;
        if (em.Exists(run.Patrol))
        {
            AddTag(em, run.Patrol, "DisableWhenNoPlayersInRange");
            var units = Routes.ActiveUnits(em, run.Patrol);
            var rest = new List<Entity>(units);
            if (units.Count > 0) rest.AddRange(Routes.CartsOf(em, units));
            foreach (var e in rest)
            {
                if (!run.Awake.Contains(Key(e))) continue;
                AddTag(em, e, "DisableWhenNoPlayersInRange");
                back++;
            }
        }
        Plugin.Logger.LogInfo($"CaravanTracker start: patrol {run.Patrol.Index}:{run.Patrol.Version} route {run.Route} finished: {why}" +
                              (back > 0 ? $", {back} units left may freeze again" : ""));
    }

    public static int StopAll(EntityManager em)
    {
        int n = Runs.Count;
        foreach (var run in Runs) Finish(em, run, "stopped by command");
        Runs.Clear();
        return n;
    }

    public static string Status(EntityManager em)
    {
        if (Runs.Count == 0) return L.T("status.none");
        var parts = new List<string>();
        foreach (var run in Runs)
        {
            int units = em.Exists(run.Patrol) ? Routes.ActiveUnits(em, run.Patrol).Count : 0;
            string state = em.Exists(run.Patrol) ? Routes.Describe(em, run.Patrol, units) : L.T("status.gone");
            if (run.CartGone) state += L.T("status.nocart");
            string stops = Routes.StopText(Routes.StopsOf(em, run.Patrol));
            if (stops.Length > 0) state += ", " + stops;
            parts.Add(L.T("status.item", L.Route(run.Route), run.Auto ? L.T("status.auto", L.Act(run.Act)) : "", state,
                          Math.Round((DateTime.UtcNow - run.Started).TotalMinutes)));
        }
        return string.Join("; ", parts);
    }

    // Every frame while there are runs; the work itself is done once a second.
    public static void Update(EntityManager em)
    {
        var now = DateTime.UtcNow;
        if (now < _nextUpdate) return;
        _nextUpdate = now.AddSeconds(1);

        int spawnTimeout = Math.Max(1, Settings.SpawnTimeoutMinutes.Value);
        int maxRun = Math.Max(10, Settings.MaxRunMinutes.Value);
        int maxSquad = Math.Max(5, Settings.MaxSquadSize.Value);

        for (int i = Runs.Count - 1; i >= 0; i--)
        {
            var run = Runs[i];
            if (!em.Exists(run.Patrol)) { Runs.RemoveAt(i); continue; }

            var patrolPos = Caravans.PositionOf(em, run.Patrol);
            var units = Routes.ActiveUnits(em, run.Patrol);
            var sp = em.GetComponentData<UnitCompositionSpawner>(run.Patrol);
            var gp = em.GetComponentData<GlobalPatrolState>(run.Patrol);
            string move = Routes.MoveStatus(em, run.Patrol);

            // A route that ended stays at its last stop (WaitForDespawn). There the game first puts out
            // the squad at the end and removes it, and only then returns the route to the start. Units seen
            // at the end are not the new caravan, so the start counts only once the route left that state.
            bool atEnd = move == "WaitForDespawn";
            string name = L.Route(run.Route);
            var age = now - run.Started;

            if (!run.Spawned && units.Count > 0 && !atEnd)
            {
                run.Spawned = true;
                Restore(em, run);
                Chat.Send(em, run.User, L.T("start.spawned", name));
                // Where the squad appeared, to see that a route put back to the start really sets out from there.
                var p = Routes.StopsOf(em, run.Patrol);
                var at = Caravans.PositionOf(em, units[0]);
                string msg = $"CaravanTracker start: patrol {run.Patrol.Index}:{run.Patrol.Version} route {run.Route}: squad appeared at stop {p?.Log} at ({Math.Round(at.x)}, {Math.Round(at.z)})";
                if (run.Reset && p != null && !p.AtStart) Plugin.Logger.LogWarning(msg + ", although the route was put back to the start");
                else Plugin.Logger.LogInfo(msg);
            }

            // Respawn guard. While the squad is assembling, every new unit is part of it. Once one of
            // them is gone (killed), any new unit is a replacement sent by the game: it is removed.
            // Units above MaxSquadSize are removed in any case.
            if (run.Spawned)
            {
                var present = new HashSet<(int, int)>();
                foreach (var u in units) present.Add(Key(u));
                if (!run.LostAny)
                    foreach (var k in run.Known)
                        if (!present.Contains(k)) { run.LostAny = true; break; }
                int blocked = 0, over = 0;
                for (int k = units.Count - 1; k >= 0; k--)
                {
                    var key = Key(units[k]);
                    if (run.Known.Contains(key)) continue;
                    if (run.LostAny || run.Known.Count >= maxSquad)
                    {
                        DestroyUtility.Destroy(em, units[k]);
                        units.RemoveAt(k);
                        if (run.LostAny) blocked++; else over++;
                    }
                    else run.Known.Add(key);
                }
                if (blocked > 0)
                {
                    run.Blocked += blocked;
                    Plugin.Logger.LogWarning($"CaravanTracker start: patrol {run.Patrol.Index}:{run.Patrol.Version} route {run.Route}: removed {blocked} respawned guards (total {run.Blocked})");
                }
                if (over > 0)
                {
                    run.OverLimit += over;
                    Plugin.Logger.LogWarning($"CaravanTracker start: patrol {run.Patrol.Index}:{run.Patrol.Version} route {run.Route}: squad over MaxSquadSize {maxSquad}, removed {over} units (total {run.OverLimit})");
                }
            }

            var carts = units.Count > 0 ? Routes.CartsOf(em, units) : new List<Entity>();
            foreach (var u in units) KeepAwake(em, run, u, patrolPos);
            foreach (var c in carts) KeepAwake(em, run, c, patrolPos);

            // The cart is destroyed, the horse (and maybe some guards) go on to the end of the route,
            // where the game removes them and returns the route to the start.
            if (carts.Count > 0) run.HadCart = true;
            else if (run.HadCart && !run.CartGone && units.Count > 0)
            {
                run.CartGone = true;
                Plugin.Logger.LogInfo($"CaravanTracker start: patrol {run.Patrol.Index}:{run.Patrol.Version} route {run.Route}: the cart is gone, " +
                                      $"{units.Count} units go on to the end of the route; not counted in auto-start limits any more");
            }

            // State changes go to the verbose log (movement flickers between Moving and Stopped, it isn't compared).
            string state = $"spawner={sp.SpawnerState} stop={gp.CurrentBusStopIndex} units={units.Count}";
            if (state != run.LastState)
            {
                Plugin.Trace($"CaravanTracker start: patrol {run.Patrol.Index}:{run.Patrol.Version} {state} move={move} at ({Math.Round(patrolPos.x)}, {Math.Round(patrolPos.z)})");
                run.LastState = state;
            }

            if (!run.Spawned && age.TotalMinutes > spawnTimeout)
            {
                Finish(em, run, $"no units after {spawnTimeout} minutes; auto-start skips this route for an hour");
                FailedAt[run.Route] = now;
                Chat.Send(em, run.User, L.T("start.failed", name, spawnTimeout));
                Runs.RemoveAt(i);
            }
            else if (run.Spawned && units.Count == 0)
            {
                Finish(em, run, "route completed or caravan destroyed");
                Chat.Send(em, run.User, L.T("start.done", name));
                Runs.RemoveAt(i);
            }
            else if (age.TotalMinutes > maxRun)
            {
                Finish(em, run, $"timeout {maxRun} minutes");
                Runs.RemoveAt(i);
            }
        }
    }
}
