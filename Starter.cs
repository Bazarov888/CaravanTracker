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

// Forced caravan start. The game updates a patrol and spawns its squad only when a player is near.
// Here the patrol is unfrozen, spawning is allowed without a player and without waiting, and the new
// units are kept from being frozen. While a start is running, it is checked once a second.
// Once a guard of the started squad is gone (killed), the game must not send replacements: such
// respawned units are removed at once, otherwise every kill spawns a new guard and memory runs out.
static partial class Starter
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
        public DateTime SpawnedAt;
        public DateTime LastGrow;
        public int Errors;
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
        if (Paused) return L.T("cleanup.paused");
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

    // Gives the route its own spawner settings back. With Source = Mod the saved player range is the
    // blocked one; a squad on the road gets the game's normal range instead, and the block comes back
    // once the route waits for a new caravan again (ApplySource).
    static void Restore(EntityManager em, Run run)
    {
        if (run.Restored || !em.Exists(run.Patrol)) return;
        var sp = em.GetComponentData<UnitCompositionSpawner>(run.Patrol);
        object boxed = sp;
        foreach (var kv in run.Saved)
        {
            object value = kv.Value;
            if (kv.Key == "PlayerRange" && value is float2 range && (IsBlockedRange(range) || (range.x == 0f && range.y == 0f)))
                value = NormalRange;
            if (value != null) SetField(boxed, kv.Key, value);
        }
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

    // End of a run: the route gets its settings back, and units that are still alive (the run timed out
    // or was stopped by command) may freeze without players again, as all units of the game do.
    static void Finish(EntityManager em, Run run, string why)
    {
        Restore(em, run);
        int back = 0;
        if (em.Exists(run.Patrol))
        {
            BlockIfWaiting(em, run.Patrol);
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
            try
            {
                if (UpdateRun(em, run, now, spawnTimeout, maxRun, maxSquad)) Runs.RemoveAt(i);
                run.Errors = 0;
            }
            catch (Exception e)
            {
                // One broken start must not stop the others. After 10 errors in a row it is given up.
                run.Errors++;
                if (run.Errors == 1 || run.Errors == 10)
                    Plugin.Logger.LogError($"CaravanTracker start: route {run.Route}, error {run.Errors}: {e}");
                if (run.Errors >= 10)
                {
                    try { Finish(em, run, "given up after 10 errors in a row"); }
                    catch (Exception e2) { Plugin.Logger.LogError($"CaravanTracker start: route {run.Route} can't be finished: {e2.Message}"); }
                    Runs.RemoveAt(i);
                }
            }
        }
    }

    // One second of one start. Returns true when the start is over and must be removed.
    static bool UpdateRun(EntityManager em, Run run, DateTime now, int spawnTimeout, int maxRun, int maxSquad)
    {
        if (!em.Exists(run.Patrol)) return true;

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
            run.SpawnedAt = now;
            run.LastGrow = now;
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
                else
                {
                    run.Known.Add(key);
                    run.LastGrow = now;
                }
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

            // The game puts the squad out in parts: the animal with the cart first, the guards up to a minute
            // later. The start settings stay until the squad has stopped growing for 90 seconds (at most
            // 4 minutes after the first unit), so the rest of the squad comes for sure.
            if (!run.Restored && ((now - run.LastGrow).TotalSeconds >= 90 || (now - run.SpawnedAt).TotalMinutes >= 4))
            {
                Restore(em, run);
                Plugin.Logger.LogInfo($"CaravanTracker start: patrol {run.Patrol.Index}:{run.Patrol.Version} route {run.Route}: squad assembled, {units.Count} units");
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
            return true;
        }
        if (run.Spawned && units.Count == 0)
        {
            Finish(em, run, "route completed or caravan destroyed");
            Chat.Send(em, run.User, L.T("start.done", name));
            return true;
        }
        if (age.TotalMinutes > maxRun)
        {
            Finish(em, run, $"timeout {maxRun} minutes");
            return true;
        }
        return false;
    }
}
