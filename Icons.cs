using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace CarriageTracker;

static class Icons
{
    // Icons the mod treats as its own: the current one and all used before.
    public static HashSet<int> Ours()
    {
        var set = new HashSet<int> { Settings.IconGuid };
        foreach (var s in Settings.OldIcons.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(s, out int g)) set.Add(g);
        return set;
    }

    // Removes the "discover first" requirement and lets every player see the icon on the whole map
    // (the same way KindredPortals does it). Returns true if something had to be changed.
    internal static bool MakeGlobal(EntityManager em, Entity icon)
    {
        if (!Settings.GlobalIcons.Value || !Caravans.Has<MapIconData>(em, icon)) return false;
        var data = em.GetComponentData<MapIconData>(icon);
        if (!data.RequiresReveal && data.AllySetting == MapIconShowSettings.Global && data.EnemySetting == MapIconShowSettings.Global)
            return false;
        data.RequiresReveal = false;
        data.AllySetting = MapIconShowSettings.Global;
        data.EnemySetting = MapIconShowSettings.Global;
        em.SetComponentData(icon, data);
        return true;
    }

    // Removes "show only to some players" restrictions.
    internal static void Unrestrict(EntityManager em, Entity e)
    {
        if (Caravans.Has<SyncToUserBitMask>(em, e))
            em.RemoveComponent(e, ComponentType.ReadWrite(Il2CppType.Of<SyncToUserBitMask>()));
        if (em.HasBuffer<SyncToUserBuffer>(e)) em.GetBuffer<SyncToUserBuffer>(e).Clear();
        if (Caravans.Has<OnlySyncToUsersTag>(em, e))
            em.RemoveComponent(e, ComponentType.ReadWrite(Il2CppType.Of<OnlySyncToUsersTag>()));
    }

    public static (int Created, int Removed, int Globalized) Sync(EntityManager em, PrefabCollectionSystem prefabs, List<Caravans.Caravan> caravans)
    {
        int iconGuid = Settings.IconGuid;
        var ours = Ours();
        var mine = new HashSet<int>(ours);
        mine.UnionWith(Settings.IconPresets.Values);
        var wanted = new Dictionary<(int, int), Entity>();
        foreach (var c in caravans) wanted[(c.Target.Index, c.Target.Version)] = c.Target;

        // Only icons the mod could have made are touched. On a caravan: icons of the mod's kinds (current, used
        // before, or a named preset, in case Icon was changed in the config by hand); such an icon stays if it is
        // of the current kind and the caravan is still wanted, otherwise it is removed. Icons of other kinds on a
        // caravan (another mod's) are left alone. Off a caravan: only an icon of the current or an earlier kind
        // whose object has vanished is removed. Icons on marker objects (they have Attach) are never touched:
        // the game's own merchant icons hang on such markers.
        var covered = new HashSet<(int, int)>();
        int removed = 0, globalized = 0;
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<MapIconTargetEntity>()))
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<PrefabGUID>()));
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var icons = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var icon in icons)
            {
                int pg = em.GetComponentData<PrefabGUID>(icon).GuidHash;
                var t = em.GetComponentData<MapIconTargetEntity>(icon).TargetEntity.GetEntityOnServer();
                bool exists = em.Exists(t);
                bool caravanTarget = exists && Caravans.IsCaravanEntity(em, prefabs, t);
                if (caravanTarget ? !mine.Contains(pg) : (!ours.Contains(pg) || exists || Caravans.Has<Attach>(em, icon))) continue;
                var key = (t.Index, t.Version);
                if (caravanTarget && pg == iconGuid && wanted.ContainsKey(key) && covered.Add(key))
                {
                    if (MakeGlobal(em, icon)) globalized++;
                    continue;
                }
                StatChangeUtility.KillOrDestroyEntity(em, icon, icon, Entity.Null, 0, StatChangeReason.Any, true);
                removed++;
            }
        }
        finally
        {
            icons.Dispose();
            query.Dispose();
        }

        int created = 0;
        if (!prefabs._PrefabGuidToEntityMap.TryGetValue(new PrefabGUID(iconGuid), out Entity iconPrefab))
        {
            Plugin.Warn($"CarriageTracker: icon prefab {iconGuid} not found, no icons created");
            return (0, removed, globalized);
        }
        foreach (var kv in wanted)
        {
            if (covered.Contains(kv.Key)) continue;
            var target = kv.Value;
            var icon = em.Instantiate(iconPrefab);
            if (!Caravans.Has<MapIconTargetEntity>(em, icon))
                em.AddComponent(icon, ComponentType.ReadWrite(Il2CppType.Of<MapIconTargetEntity>()));
            em.SetComponentData(icon, new MapIconTargetEntity { TargetEntity = target, TargetNetworkId = em.GetComponentData<NetworkId>(target) });
            Unrestrict(em, icon);
            if (MakeGlobal(em, icon)) globalized++;
            created++;
        }
        return (created, removed, globalized);
    }
}

// Proxy mode: the icon hangs not on the caravan itself but on a separate map marker object
// (MapIcon_ProxyObject_POI_Unknown), like KindredPortals does. The game sends such markers to players
// regardless of distance. The marker is placed at the caravan and recreated when the caravan
// moves farther than MoveThreshold meters. Also used for frozen caravans in direct mode.
// Other mods make the same markers (KindredPortals: SpawnedBy = a player's character, an icon of the player's
// choice), so only markers of this mod are touched (Owned).
static class Proxies
{
    const int ProxyGuid = 636813227; // MapIcon_ProxyObject_POI_Unknown
    const float MoveThreshold = 10f;
    static (int, int) Key(Entity e) => (e.Index, e.Version);

    // Markers this mod made since the server start, and of them those whose icon the game hasn't made yet
    // (it is made global as soon as it appears, see GlobalizeNewIcons).
    static readonly HashSet<(int, int)> Created = new();
    static readonly HashSet<(int, int)> Pending = new();
    static DateTime _pendingUntil;

    internal static bool HasPending => Pending.Count > 0;

    // One icon requested, of one of the given kinds.
    static bool RequestsOneOf(EntityManager em, Entity p, HashSet<int> kinds)
    {
        if (!em.HasBuffer<AttachMapIconsToEntity>(p)) return false;
        var buf = em.GetBuffer<AttachMapIconsToEntity>(p);
        return buf.Length == 1 && kinds.Contains(buf[0].Prefab.GuidHash);
    }

    // A marker of this mod: made by it since the server start; or, after a restart (the list above isn't saved
    // and entity numbers change), a marker that asks for one icon of the mod's kinds and was spawned by a caravan
    // object or by something that no longer exists (a caravan gone while the server was off). Until 1.0.10 every
    // such marker not on a wanted caravan was removed, KindredPortals' included (audit 02.11).
    static bool Owned(EntityManager em, PrefabCollectionSystem prefabs, Entity p, HashSet<int> kinds)
    {
        if (Created.Contains(Key(p))) return true;
        if (!RequestsOneOf(em, p, kinds)) return false;
        var t = em.GetComponentData<SpawnedBy>(p).Value;
        return t == Entity.Null || !em.Exists(t) || Caravans.IsCaravanEntity(em, prefabs, t);
    }

    // Called from the patch before MapIconSpawnSystem while new icons on our markers are expected.
    // Each marker is done once its icon has been seen; with GlobalIcons off nothing is waited for.
    internal static void GlobalizeNewIcons()
    {
        if (DateTime.UtcNow > _pendingUntil || !Settings.GlobalIcons.Value) { Pending.Clear(); return; }
        var world = Caravans.ServerWorld();
        if (world == null) return;
        var em = world.EntityManager;
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<MapIconData>()))
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<Attach>()))
            .WithOptions(EntityQueryOptions.IncludeDisabled);
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var icons = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var icon in icons)
            {
                var parent = Key(em.GetComponentData<Attach>(icon).Parent);
                if (!Pending.Contains(parent)) continue;
                Icons.MakeGlobal(em, icon);
                Pending.Remove(parent);
                if (Pending.Count == 0) break;
            }
        }
        finally
        {
            icons.Dispose();
            query.Dispose();
        }
    }

    static void SetPos(EntityManager em, Entity e, float3 pos)
    {
        if (Caravans.Has<Translation>(em, e)) em.SetComponentData(e, new Translation { Value = pos });
        if (Caravans.Has<LocalToWorld>(em, e)) em.SetComponentData(e, new LocalToWorld { Value = float4x4.Translate(pos) });
    }


    static List<Entity> Attached(EntityManager em, Entity p)
    {
        var list = new List<Entity>();
        if (!em.HasBuffer<AttachedBuffer>(p)) return list;
        var buf = em.GetBuffer<AttachedBuffer>(p);
        for (int i = 0; i < buf.Length; i++)
        {
            var e = buf[i].Entity;
            if (e != Entity.Null && em.Exists(e)) list.Add(e);
        }
        return list;
    }

    // Destroys a marker together with its icons. Returns the number of removed icons.
    static int Destroy(EntityManager em, Entity p)
    {
        var attached = Attached(em, p);
        foreach (var e in attached) em.DestroyEntity(e);
        Created.Remove(Key(p));
        Pending.Remove(Key(p));
        em.DestroyEntity(p);
        return attached.Count;
    }

    public static (int Created, int Removed, int Globalized) Sync(EntityManager em, PrefabCollectionSystem prefabs, List<Caravans.Caravan> caravans)
    {
        int iconGuid = Settings.IconGuid;
        var current = new HashSet<int> { iconGuid };
        var kinds = Icons.Ours();
        kinds.UnionWith(Settings.IconPresets.Values);
        var wanted = new Dictionary<(int, int), Caravans.Caravan>();
        foreach (var c in caravans) wanted[(c.Target.Index, c.Target.Version)] = c;
        var covered = new HashSet<(int, int)>();
        var drop = new List<Entity>();
        int created = 0, removed = 0, globalized = 0;

        // Our markers (Owned): a marker stays if its caravan is still wanted, the icon is of the current kind and
        // the caravan hasn't moved away. Others of ours are removed; markers of other mods are left alone.
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<PrefabGUID>()))
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<SpawnedBy>()))
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<AttachMapIconsToEntity>()))
            .WithOptions(EntityQueryOptions.IncludeDisabled);
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var proxies = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var p in proxies)
            {
                if (em.GetComponentData<PrefabGUID>(p).GuidHash != ProxyGuid || !Owned(em, prefabs, p, kinds)) continue;
                var t = em.GetComponentData<SpawnedBy>(p).Value;
                var key = (t.Index, t.Version);
                if (em.Exists(t) && wanted.TryGetValue(key, out var c) && RequestsOneOf(em, p, current)
                    && Caravans.Dist2(Caravans.PositionOf(em, p), c.Pos) < MoveThreshold && covered.Add(key))
                {
                    foreach (var icon in Attached(em, p))
                        if (Icons.MakeGlobal(em, icon)) globalized++;
                    continue;
                }
                drop.Add(p);
            }
        }
        finally
        {
            proxies.Dispose();
            query.Dispose();
        }
        foreach (var p in drop) removed += Destroy(em, p);

        if (!prefabs._PrefabGuidToEntityMap.TryGetValue(new PrefabGUID(ProxyGuid), out Entity proxyPrefab))
        {
            Plugin.Warn($"CarriageTracker: proxy prefab {ProxyGuid} not found, no proxy icons created");
            return (0, removed, globalized);
        }
        foreach (var kv in wanted)
        {
            if (covered.Contains(kv.Key)) continue;
            var c = kv.Value;
            var proxy = em.Instantiate(proxyPrefab);
            if (!em.HasBuffer<AttachMapIconsToEntity>(proxy))
            {
                em.DestroyEntity(proxy);
                Plugin.Warn("CarriageTracker: proxy prefab has no AttachMapIconsToEntity, proxy mode can't work");
                break;
            }
            SetPos(em, proxy, c.Pos);
            if (!Caravans.Has<SpawnedBy>(em, proxy))
                em.AddComponent(proxy, ComponentType.ReadWrite(Il2CppType.Of<SpawnedBy>()));
            em.SetComponentData(proxy, new SpawnedBy { Value = c.Target });
            Icons.Unrestrict(em, proxy);
            var buf = em.GetBuffer<AttachMapIconsToEntity>(proxy);
            buf.Clear();
            buf.Add(new AttachMapIconsToEntity { Prefab = new PrefabGUID(iconGuid) });
            Created.Add(Key(proxy));
            if (Settings.GlobalIcons.Value) Pending.Add(Key(proxy));
            created++;
        }
        if (created > 0 && Settings.GlobalIcons.Value) _pendingUntil = DateTime.UtcNow.AddSeconds(10);
        return (created, removed, globalized);
    }
}

// As in KindredPortals: before the game processes new icons, icons on our markers are made
// visible to everyone. Active only for a few frames after markers are created.
[HarmonyPatch(typeof(MapIconSpawnSystem), nameof(MapIconSpawnSystem.OnUpdate))]
static class MapIconSpawnPatch
{
    public static void Prefix()
    {
        if (!Proxies.HasPending) return;
        try { Proxies.GlobalizeNewIcons(); }
        catch (Exception e) { Plugin.Error($"CarriageTracker: {e}"); }
    }
}
