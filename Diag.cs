using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using VampireCommandFramework;

namespace CaravanTracker;

// Developer tools: dumps of caravan objects and patrols. Registered only with EnableDiagnostics = true.
[CommandGroup("cvdiag")]
public class DiagCommands
{
    [Command("comps", adminOnly: true, usage: "[radius]", description: "Components of the nearest caravan object")]
    public void Comps(ChatCommandContext ctx, int radius = 10)
    {
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) { ctx.Reply(L.T("world.notready")); return; }
        var em = world.EntityManager;
        float3 me = Caravans.PositionOf(em, ctx.Event.SenderCharacterEntity);

        Entity best = Entity.Null;
        float bestDist = float.MaxValue;
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<PrefabGUID>()))
            .WithOptions(EntityQueryOptions.IncludeDisabled);
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var e in entities)
            {
                if (Caravans.Classify(prefabs, em.GetComponentData<PrefabGUID>(e)).Kind == Kind.None) continue;
                float d = Caravans.Dist2(Caravans.PositionOf(em, e), me);
                if (d <= radius && d < bestDist) { bestDist = d; best = e; }
            }
        }
        finally
        {
            entities.Dispose();
            query.Dispose();
        }
        if (best == Entity.Null) { ctx.Reply($"No caravan objects within {radius} m."); return; }

        var names = new List<string>();
        var types = em.GetComponentTypes(best);
        try
        {
            for (int i = 0; i < types.Length; i++) names.Add(TypeManager.GetType(types[i].TypeIndex).Name);
        }
        finally
        {
            types.Dispose();
        }
        names.Sort(StringComparer.Ordinal);
        if (!prefabs._PrefabLookupMap.TryGetName(em.GetComponentData<PrefabGUID>(best), out string prefabName)) prefabName = "?";
        Plugin.Logger.LogInfo($"CaravanTracker comps {prefabName}: {string.Join(", ", names)}");
        ctx.Reply($"{prefabName}, {Math.Round(bestDist)} m, components: {names.Count}. Full list in the server log.");
        for (int i = 0; i < names.Count; i += 6)
            ctx.Reply(string.Join(", ", names.GetRange(i, Math.Min(6, names.Count - i))));
    }

    [Command("near", adminOnly: true, usage: "[radius]", description: "Objects around you with names and ids")]
    public void Near(ChatCommandContext ctx, int radius = 8)
    {
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) { ctx.Reply(L.T("world.notready")); return; }
        var em = world.EntityManager;
        float3 me = Caravans.PositionOf(em, ctx.Event.SenderCharacterEntity);

        var seen = new Dictionary<int, (string Name, int Count, float Dist, bool Net)>();
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<PrefabGUID>()))
            .WithOptions(EntityQueryOptions.IncludeDisabled);
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var e in entities)
            {
                if (!Caravans.Has<LocalToWorld>(em, e) && !Caravans.Has<Translation>(em, e)) continue;
                float d = Caravans.Dist2(Caravans.PositionOf(em, e), me);
                if (d > radius) continue;
                var guid = em.GetComponentData<PrefabGUID>(e);
                if (seen.TryGetValue(guid.GuidHash, out var s))
                {
                    seen[guid.GuidHash] = (s.Name, s.Count + 1, Math.Min(s.Dist, d), s.Net);
                    continue;
                }
                if (!prefabs._PrefabLookupMap.TryGetName(guid, out string name)) name = "?";
                seen[guid.GuidHash] = (name, 1, d, Caravans.Has<NetworkId>(em, e));
            }
        }
        finally
        {
            entities.Dispose();
            query.Dispose();
        }

        var list = new List<KeyValuePair<int, (string Name, int Count, float Dist, bool Net)>>(seen);
        list.Sort((a, b) => a.Value.Dist.CompareTo(b.Value.Dist));
        ctx.Reply($"Different objects within {radius} m: {list.Count}. First 20:");
        for (int i = 0; i < list.Count && i < 20; i++)
        {
            var v = list[i].Value;
            ctx.Reply($"{v.Name} ({list[i].Key}) x{v.Count}, {Math.Round(v.Dist)} m, network={(v.Net ? "yes" : "no")}");
        }
    }

    // ---- dump: everything about caravan carts and their data ----

    static readonly string[] ScriptTypes =
        { "Script_CarriageData", "Script_CarriageDriverData", "Script_CarriageCollidersData", "Script_RespawnCarriageUponLost_Data" };

    static readonly string[] Interesting =
        { "Carriage", "Script_", "Respawn", "Spawn", "Route", "Travel", "Path", "Patrol", "Waypoint", "Timer", "Lifetime",
          "Follow", "Horse", "Driver", "Mount", "Lost", "Teleport", "Move", "Idle", "Disable", "Hidden", "Owner", "Attach",
          "Translation", "Transform", "Chain", "Leader", "Group", "Squad", "Minion", "Master", "Region", "Range" };

    // Objects referenced by dumped fields (SpawnedBy, Follower etc.). Dumped in full in a second pass.
    static readonly List<Entity> Refs = new();

    static readonly Dictionary<string, Type> ManagedTypes = new();

    // Finds the C# wrapper of a game type by its full name.
    static Type ManagedByFullName(string fullName)
    {
        if (ManagedTypes.TryGetValue(fullName, out var cached)) return cached;
        Type found = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { found = asm.GetType(fullName, false); } catch { }
            if (found != null) break;
        }
        ManagedTypes[fullName] = found;
        return found;
    }

    // Finds the C# wrapper of a game type by its short name, preferring ProjectM namespaces.
    static Type ManagedBySimpleName(string name)
    {
        Type fallback = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            catch { continue; }
            foreach (var t in types)
            {
                if (t == null || t.Name != name) continue;
                if (t.Namespace != null && t.Namespace.StartsWith("ProjectM", StringComparison.Ordinal)) return t;
                fallback ??= t;
            }
        }
        return fallback;
    }

    static bool IsInteresting(string name)
    {
        foreach (var w in Interesting)
            if (name.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    static string EntityName(EntityManager em, PrefabCollectionSystem prefabs, Entity e)
    {
        if (e == Entity.Null) return "null";
        if (!em.Exists(e)) return $"{e.Index}:{e.Version} (gone)";
        string n = "?";
        if (Caravans.Has<PrefabGUID>(em, e) && prefabs._PrefabLookupMap.TryGetName(em.GetComponentData<PrefabGUID>(e), out string pn)) n = pn;
        return $"{e.Index}:{e.Version} {n}";
    }

    static string Fmt(object v, EntityManager em, PrefabCollectionSystem prefabs, int depth)
    {
        switch (v)
        {
            case null: return "null";
            case Entity e:
                if (e != Entity.Null && em.Exists(e)) Refs.Add(e);
                return EntityName(em, prefabs, e);
            case PrefabGUID g: return prefabs._PrefabLookupMap.TryGetName(g, out string gn) ? $"{g.GuidHash} {gn}" : g.GuidHash.ToString();
            case float3 f3: return $"({f3.x:0.#}, {f3.y:0.#}, {f3.z:0.#})";
            case float2 f2: return $"({f2.x:0.#}, {f2.y:0.#})";
            case NetworkedEntity ne: return Fmt(ne.GetEntityOnServer(), em, prefabs, depth);
            case string s: return s;
        }
        var t = v.GetType();
        if (t.IsPrimitive || t.IsEnum) return v.ToString();
        if (t.Name.StartsWith("FixedString", StringComparison.Ordinal))
        {
            try { return "\"" + v.ToString() + "\""; } catch { return "<FixedString>"; }
        }
        if (depth < 2 && t.IsValueType) return "{" + Fields(v, em, prefabs, depth + 1) + "}";
        return v.ToString();
    }

    static string Fields(object boxed, EntityManager em, PrefabCollectionSystem prefabs, int depth)
    {
        var parts = new List<string>();
        foreach (var f in boxed.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            try { parts.Add($"{f.Name}={Fmt(f.GetValue(boxed), em, prefabs, depth)}"); }
            catch (Exception ex) { parts.Add($"{f.Name}=<{ex.GetType().Name}>"); }
        }
        var text = string.Join(", ", parts);
        return text.Length > 700 ? text.Substring(0, 700) + "..." : text;
    }

    static unsafe object ReadRaw(EntityManager em, Entity e, ComponentType ct, Type managed)
    {
        void* ptr = em.GetComponentDataRawRO(e, ct.TypeIndex);
        if (ptr == null) return null;
        return Marshal.PtrToStructure(new IntPtr(ptr), managed);
    }

    static void DumpEntity(EntityManager em, PrefabCollectionSystem prefabs, Entity e, List<string> lines, bool all)
    {
        var pos = Caravans.PositionOf(em, e);
        lines.Add("");
        lines.Add($"== {EntityName(em, prefabs, e)}  pos=({pos.x:0}, {pos.y:0}, {pos.z:0})  onMap={Caravans.OnMap(pos)}  disabled={Caravans.Has<Disabled>(em, e)}");
        var names = new List<string>();
        var types = em.GetComponentTypes(e);
        try
        {
            for (int i = 0; i < types.Length; i++)
            {
                var ct = types[i];
                var il2 = TypeManager.GetType(ct.TypeIndex);
                string shortName = il2 == null ? "?" : il2.Name;
                string fullName = il2 == null ? "?" : il2.FullName;
                names.Add(shortName);
                if (!all && !IsInteresting(shortName)) continue;
                var managed = ManagedByFullName(fullName);
                if (managed == null || !managed.IsValueType) { lines.Add($"  {shortName}: (unreadable)"); continue; }
                if (managed.GetFields(BindingFlags.Public | BindingFlags.Instance).Length == 0) { lines.Add($"  {shortName}: (tag without data)"); continue; }
                try
                {
                    var value = ReadRaw(em, e, ct, managed);
                    lines.Add($"  {shortName}: {(value == null ? "null" : Fields(value, em, prefabs, 0))}");
                }
                catch (Exception ex)
                {
                    string msg = (ex.Message ?? "").Split('\n')[0];
                    lines.Add($"  {shortName}: <{(msg.Length > 150 ? msg.Substring(0, 150) : msg)}>");
                }
            }
        }
        finally
        {
            types.Dispose();
        }
        names.Sort(StringComparer.Ordinal);
        lines.Add("  all components: " + string.Join(", ", names));
    }

    [Command("dump", adminOnly: true, description: "Everything about caravan carts into BepInEx/CaravanTracker-dump.txt")]
    public void Dump(ChatCommandContext ctx)
    {
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) { ctx.Reply(L.T("world.notready")); return; }
        var em = world.EntityManager;
        var lines = new List<string> { $"CaravanTracker dump {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
        var targets = new List<Entity>();
        var seen = new HashSet<(int, int)>();

        // 1. Carts, horses and containers, including parked ones outside the map.
        foreach (var f in Caravans.FullScan(em, prefabs, out _))
            if (seen.Add((f.Entity.Index, f.Entity.Version))) targets.Add(f.Entity);
        lines.Add($"caravan objects: {targets.Count}");

        // 2. Everything with Script_* caravan data, even if it is not a cart.
        foreach (var name in ScriptTypes)
        {
            try
            {
                var managed = ManagedBySimpleName(name);
                if (managed == null) { lines.Add($"type {name}: not found"); continue; }
                var builder = new EntityQueryBuilder(Allocator.Temp)
                    .AddAll(ComponentType.ReadOnly(Il2CppType.From(managed)))
                    .WithOptions(EntityQueryOptions.IncludeDisabled);
                var query = em.CreateEntityQuery(ref builder);
                builder.Dispose();
                var arr = query.ToEntityArray(Allocator.Temp);
                try
                {
                    lines.Add($"type {managed.FullName}: {arr.Length} entities");
                    foreach (var e in arr)
                        if (seen.Add((e.Index, e.Version))) targets.Add(e);
                }
                finally
                {
                    arr.Dispose();
                    query.Dispose();
                }
            }
            catch (Exception ex)
            {
                lines.Add($"type {name}: error {ex.GetType().Name}: {ex.Message}");
            }
        }

        Refs.Clear();
        int count = Math.Min(targets.Count, 80);
        for (int i = 0; i < count; i++) DumpEntity(em, prefabs, targets[i], lines, false);

        // 3. Objects referenced by carts and horses (who spawned them, whom they follow), in full.
        var refs = new List<Entity>(Refs);
        int refCount = 0;
        lines.Add("");
        lines.Add("==== referenced entities ====");
        foreach (var r in refs)
        {
            if (refCount >= 25) break;
            if (!seen.Add((r.Index, r.Version))) continue;
            // Objects without a prefab name are dumped in full, named ones as usual.
            bool named = Caravans.Has<PrefabGUID>(em, r) && prefabs._PrefabLookupMap.TryGetName(em.GetComponentData<PrefabGUID>(r), out string _);
            DumpEntity(em, prefabs, r, lines, !named);
            refCount++;
        }
        count += refCount;

        var path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "CaravanTracker-dump.txt");
        System.IO.File.WriteAllLines(path, lines);
        ctx.Reply($"Objects written: {count} of {targets.Count}, lines: {lines.Count}. File: BepInEx/CaravanTracker-dump.txt");
    }

    // ---- patrols: all squad spawners and patrols of the world ----

    static unsafe object ReadComp(EntityManager em, Entity e, Type managed)
    {
        if (managed == null) return null;
        var ct = ComponentType.ReadOnly(Il2CppType.From(managed));
        if (!em.HasComponent(e, ct)) return null;
        return ReadRaw(em, e, ct, managed);
    }

    static object FieldOf(object boxed, string name) => boxed?.GetType().GetField(name)?.GetValue(boxed);

    static MethodInfo _getBuffer;

    // Elements of a DynamicBuffer whose type is known only at runtime.
    static string ReadBuffer(EntityManager em, PrefabCollectionSystem prefabs, Entity e, Type elemType, int max)
    {
        if (elemType == null) return "";
        try
        {
            if (!em.HasComponent(e, ComponentType.ReadOnly(Il2CppType.From(elemType)))) return "";
            if (_getBuffer == null)
                foreach (var m in typeof(EntityManager).GetMethods())
                    if (m.Name == "GetBuffer" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2) { _getBuffer = m; break; }
            if (_getBuffer == null) return "<no GetBuffer>";
            object buffer = _getBuffer.MakeGenericMethod(elemType).Invoke(em, new object[] { e, true });
            var bt = buffer.GetType();
            int length = (int)bt.GetProperty("Length").GetValue(buffer);
            var item = bt.GetProperty("Item") ?? null;
            var getItem = item != null ? item.GetGetMethod() : bt.GetMethod("get_Item");
            var parts = new List<string>();
            for (int i = 0; i < length && i < max; i++)
                parts.Add("{" + Fields(getItem.Invoke(buffer, new object[] { i }), em, prefabs, 1) + "}");
            return $"[{length}] " + string.Join(" ", parts);
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            string msg = (inner.Message ?? "").Split('\n')[0];
            return $"<{inner.GetType().Name}: {(msg.Length > 120 ? msg.Substring(0, 120) : msg)}>";
        }
    }

    [Command("patrols", adminOnly: true, description: "All squad spawners and patrols into BepInEx/CaravanTracker-patrols.txt")]
    public void Patrols(ChatCommandContext ctx)
    {
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) { ctx.Reply(L.T("world.notready")); return; }
        var em = world.EntityManager;
        var lines = new List<string> { $"CaravanTracker patrols {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };

        var spawnerT = ManagedBySimpleName("UnitCompositionSpawner");
        var patrolT = ManagedBySimpleName("GlobalPatrolState");
        var moveT = ManagedBySimpleName("MovePatrolState");
        var nameT = ManagedBySimpleName("UnitCompositionSpawnerDebugName");
        var groupT = ManagedBySimpleName("UnitCompositionGroupEntry");
        var unitT = ManagedBySimpleName("UnitCompositionGroupUnitEntry");
        var activeT = ManagedBySimpleName("UnitCompositionActiveUnit");
        var busT = ManagedBySimpleName("PatrolBusStopNode");
        foreach (var (label, t) in new[] { ("spawner", spawnerT), ("patrol", patrolT), ("move", moveT), ("debugName", nameT),
                                           ("group", groupT), ("unit", unitT), ("active", activeT), ("busStop", busT) })
            lines.Add($"type {label}: {(t == null ? "not found" : t.FullName)}");
        if (spawnerT == null) { System.IO.File.WriteAllLines(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "CaravanTracker-patrols.txt"), lines); ctx.Reply("Type UnitCompositionSpawner not found."); return; }

        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(ComponentType.ReadOnly(Il2CppType.From(spawnerT)))
            .WithOptions(EntityQueryOptions.IncludeDisabled);
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var arr = query.ToEntityArray(Allocator.Temp);
        int total = arr.Length;
        var states = new Dictionary<string, int>();
        int patrols = 0, carriagePatrols = 0;
        var patrolLines = new List<string>();
        var otherLines = new List<string>();
        try
        {
            foreach (var e in arr)
            {
                var sp = ReadComp(em, e, spawnerT);
                string state = FieldOf(sp, "SpawnerState")?.ToString() ?? "?";
                states[state] = states.TryGetValue(state, out var n) ? n + 1 : 1;
                var pos = Caravans.PositionOf(em, e);
                string name = nameT == null ? "" : (ReadComp(em, e, nameT) is object dn ? Fields(dn, em, prefabs, 0) : "");
                string units = ReadBuffer(em, prefabs, e, unitT, 12);
                bool isPatrol = patrolT != null && ReadComp(em, e, patrolT) != null;
                string head = $"{e.Index}:{e.Version} pos=({pos.x:0}, {pos.z:0}) onMap={Caravans.OnMap(pos)} disabled={Caravans.Has<Disabled>(em, e)} state={state} name={name}";
                if (!isPatrol)
                {
                    otherLines.Add(head + " units=" + units);
                    continue;
                }
                patrols++;
                bool carriage = units.IndexOf("Carriage", StringComparison.Ordinal) >= 0;
                if (carriage) carriagePatrols++;
                patrolLines.Add("");
                patrolLines.Add($"== PATROL{(carriage ? " CARRIAGE" : "")} " + head);
                patrolLines.Add("  units: " + units);
                patrolLines.Add("  groups: " + ReadBuffer(em, prefabs, e, groupT, 6));
                patrolLines.Add("  active: " + ReadBuffer(em, prefabs, e, activeT, 6));
                patrolLines.Add("  busStops: " + ReadBuffer(em, prefabs, e, busT, 30));
                patrolLines.Add("  GlobalPatrolState: " + Fields(ReadComp(em, e, patrolT), em, prefabs, 0));
                if (ReadComp(em, e, moveT) is object mv) patrolLines.Add("  MovePatrolState: " + Fields(mv, em, prefabs, 0));
                patrolLines.Add("  UnitCompositionSpawner: " + (sp == null ? "null" : Fields(sp, em, prefabs, 0)));
            }
        }
        finally
        {
            arr.Dispose();
            query.Dispose();
        }

        lines.Add($"spawners: {total}, patrols: {patrols}, carriage patrols: {carriagePatrols}");
        foreach (var kv in states) lines.Add($"  state {kv.Key}: {kv.Value}");
        lines.Add("");
        lines.Add("==== patrols ====");
        lines.AddRange(patrolLines);
        lines.Add("");
        lines.Add("==== other spawners ====");
        lines.AddRange(otherLines);
        System.IO.File.WriteAllLines(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "CaravanTracker-patrols.txt"), lines);
        ctx.Reply($"Spawners: {total}, patrols: {patrols}, with carts: {carriagePatrols}. File: BepInEx/CaravanTracker-patrols.txt");
    }
}
