using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;

namespace CaravanTracker;

// Acts as in the game's V Blood menu (K). A player's act is the highest act among the V Blood they have drunk.
// A route's act comes from the level of the caravan guards, with the same bounds as the bosses in the menu:
// up to 39 - I, 40-59 - II, 60-65 - III, 66 and above - IV.
static class Acts
{
    public record Boss(int Act, string Name, int Level);
    public record PlayerAct(int Act, string Boss, int BossLevel, int Drunk);
    public record RouteAct(int Level, int Act, bool Manual);

    // The list from the wiki (V Blood Carriers page), prefab names as in the game.
    static readonly Dictionary<string, Boss> Bosses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CHAR_Forest_Wolf_VBlood"] = new(1, "Alpha the White Wolf", 16),
        ["CHAR_Bandit_Frostarrow_VBlood"] = new(1, "Keely the Frost Archer", 20),
        ["CHAR_Bandit_StoneBreaker_VBlood"] = new(1, "Errol the Stonebreaker", 20),
        ["CHAR_Bandit_Foreman_VBlood"] = new(1, "Rufus the Foreman", 20),
        ["CHAR_Bandit_Stalker_VBlood"] = new(1, "Grayson the Armourer", 27),
        ["CHAR_Undead_BishopOfDeath_VBlood"] = new(1, "Goreswine the Ravager", 27),
        ["CHAR_Bandit_Chaosarrow_VBlood"] = new(1, "Lidia the Chaos Archer", 30),
        ["CHAR_Bandit_Bomber_VBlood"] = new(1, "Clive the Firestarter", 30),
        ["CHAR_Vermin_DireRat_VBlood"] = new(1, "Nibbles the Putrid Rat", 30),
        ["CHAR_Bandit_Fisherman_VBlood"] = new(1, "Finn the Fisherman", 32),
        ["CHAR_Poloma_VBlood"] = new(1, "Polora the Feywalker", 35),
        ["CHAR_Forest_Bear_Dire_Vblood"] = new(1, "Kodia the Ferocious Bear", 35),
        ["CHAR_Undead_Priest_VBlood"] = new(1, "Nicholaus the Fallen", 35),
        ["CHAR_Bandit_Tourok_VBlood"] = new(1, "Quincey the Bandit King", 37),

        ["CHAR_Villager_Tailor_VBlood"] = new(2, "Beatrice the Tailor", 40),
        ["CHAR_Militia_Guard_VBlood"] = new(2, "Vincent the Frostbringer", 44),
        ["CHAR_Militia_Nun_VBlood"] = new(2, "Christina the Sun Priestess", 44),
        ["CHAR_VHunter_Leader_VBlood"] = new(2, "Tristan the Vampire Hunter", 44),
        ["CHAR_Militia_Fabian_VBlood"] = new(2, "Sir Erwin the Gallant Cavalier", 46),
        ["CHAR_Undead_Leader_Vblood"] = new(2, "Kriig the Undead General", 47),
        ["CHAR_Undead_BishopOfShadows_VBlood"] = new(2, "Leandra the Shadow Priestess", 47),
        ["CHAR_Militia_Scribe_VBlood"] = new(2, "Maja the Dark Savant", 47),
        ["CHAR_Undead_Infiltrator_VBlood"] = new(2, "Bane the Shadowblade", 50),
        ["CHAR_Militia_Glassblower_VBlood"] = new(2, "Grethel the Glassblower", 50),
        ["CHAR_Militia_Longbowman_LightArrow_Vblood"] = new(2, "Meredith the Bright Archer", 50),
        ["CHAR_Geomancer_Human_VBlood"] = new(2, "Terah the Geomancer", 53),
        ["CHAR_Wendigo_VBlood"] = new(2, "Frostmaw the Mountain Terror", 53),
        ["CHAR_Vampire_IceRanger_VBlood"] = new(2, "General Elena the Hollow", 53),
        ["CHAR_Undead_ArenaChampion_VBlood"] = new(2, "Gaius the Cursed Champion", 55),
        ["CHAR_Vampire_HighLord_VBlood"] = new(2, "General Cassius the Betrayer", 57),
        ["CHAR_VHunter_Jade_VBlood"] = new(2, "Jade the Vampire Hunter", 57),
        ["CHAR_Militia_BishopOfDunley_VBlood"] = new(2, "Raziel the Shepherd", 57),
        ["CHAR_Militia_Leader_VBlood"] = new(2, "Octavian the Militia Captain", 58),

        ["CHAR_Gloomrot_Iva_VBlood"] = new(3, "Ziva the Engineer", 60),
        ["CHAR_Gloomrot_Voltage_VBlood"] = new(3, "Domina the Blade Dancer", 60),
        ["CHAR_Gloomrot_Purifier_VBlood"] = new(3, "Angram the Purifier", 61),
        ["CHAR_Spider_Queen_VBlood"] = new(3, "Ungora the Spider Queen", 63),
        ["CHAR_Villager_CursedWanderer_VBlood"] = new(3, "Ben the Old Wanderer", 63),
        ["CHAR_Undead_ZealousCultist_VBlood"] = new(3, "Foulrot the Soultaker", 63),
        ["CHAR_Cursed_ToadKing_VBlood"] = new(3, "Albert the Duke of Balaton", 64),
        ["CHAR_WerewolfChieftain_Human"] = new(3, "Willfred the Village Elder", 64),
        ["CHAR_Undead_CursedSmith_VBlood"] = new(3, "Cyril the Cursed Smith", 65),

        ["CHAR_ChurchOfLight_Overseer_VBlood"] = new(4, "Sir Magnus the Overseer", 66),
        ["CHAR_ChurchOfLight_Sommelier_VBlood"] = new(4, "Baron du Bouchon the Sommelier", 70),
        ["CHAR_Harpy_Matriarch_VBlood"] = new(4, "Morian the Stormwing Matriarch", 70),
        ["CHAR_ArchMage_VBlood"] = new(4, "Mairwyn the Elementalist", 70),
        ["CHAR_Gloomrot_TheProfessor_VBlood"] = new(4, "Henry Blackbrew the Doctor", 74),
        ["CHAR_Blackfang_Livith_VBlood"] = new(4, "Jakira the Shadow Huntress", 75),
        ["CHAR_Blackfang_CarverBoss_VBlood"] = new(4, "Stavros the Carver", 75),
        ["CHAR_Blackfang_Lucie_VBlood"] = new(4, "Lucile the Venom Alchemist", 76),
        ["CHAR_Cursed_Witch_VBlood"] = new(4, "Matka the Curse Weaver", 76),
        ["CHAR_Winter_Yeti_VBlood"] = new(4, "Terrorclaw the Ogre", 76),
        ["CHAR_ChurchOfLight_Cardinal_VBlood"] = new(4, "Azariel the Sunbringer", 79),
        ["CHAR_Gloomrot_RailgunSergeant_VBlood"] = new(4, "Voltatia the Power Master", 79),
        ["CHAR_Blackfang_Valyr_VBlood"] = new(4, "Dantos the Forgebinder", 82),
        ["CHAR_BatVampire_VBlood"] = new(4, "Lord Styx the Night Champion", 84),
        ["CHAR_Cursed_MountainBeast_VBlood"] = new(4, "Gorecrusher the Behemoth", 84),
        ["CHAR_Vampire_BloodKnight_VBlood"] = new(4, "General Valencia the Depraved", 84),
        ["CHAR_ChurchOfLight_Paladin_VBlood"] = new(4, "Solarus the Immaculate", 86),
        ["CHAR_Manticore_VBlood"] = new(4, "Talzur the Winged Horror", 86),
        ["CHAR_Gloomrot_Monster_VBlood"] = new(4, "Adam the Firstborn", 88),
        ["CHAR_Blackfang_Morgana_VBlood"] = new(4, "Megara the Serpent Queen", 88),
        ["CHAR_Vampire_Dracula_VBlood"] = new(4, "Dracula the Immortal King", 91),
    };

    public static string Roman(int act) => act switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", _ => "?" };

    public static int OfLevel(int level) => ActLogic.OfLevel(level);

    // Unit level from its prefab (UnitLevel), 0 if not found.
    public static int LevelOf(EntityManager em, PrefabCollectionSystem prefabs, PrefabGUID guid)
    {
        if (!prefabs._PrefabGuidToEntityMap.TryGetValue(guid, out Entity prefab)) return 0;
        if (!Caravans.Has<UnitLevel>(em, prefab)) return 0;
        return em.GetComponentData<UnitLevel>(prefab).Level._Value;
    }

    // A player's act by the V Blood they drank. A V Blood missing from the list counts by its level.
    public static PlayerAct OfUser(EntityManager em, PrefabCollectionSystem prefabs, Entity userEntity)
    {
        int act = 1, bossLevel = -1, drunk = 0;
        string boss = null;
        if (!em.Exists(userEntity) || !Caravans.Has<ProgressionMapper>(em, userEntity)) return new(1, null, 0, 0);
        var progression = em.GetComponentData<ProgressionMapper>(userEntity).ProgressionEntity.GetEntityOnServer();
        if (progression == Entity.Null || !em.Exists(progression) || !em.HasBuffer<UnlockedVBlood>(progression))
            return new(1, null, 0, 0);
        var buf = em.GetBuffer<UnlockedVBlood>(progression);
        for (int i = 0; i < buf.Length; i++)
        {
            PrefabGUID guid = buf[i].VBlood;
            drunk++;
            int a, level;
            string title;
            if (prefabs._PrefabLookupMap.TryGetName(guid, out string name) && name != null && Bosses.TryGetValue(name, out var known))
            {
                a = known.Act; level = known.Level; title = known.Name;
            }
            else
            {
                level = LevelOf(em, prefabs, guid);
                a = OfLevel(level);
                title = name ?? guid.GuidHash.ToString();
            }
            if (a > act || (a == act && level > bossLevel))
            {
                act = a; bossLevel = level; boss = title;
            }
        }
        return new(act, boss, bossLevel, drunk);
    }

    public record OnlinePlayer(string Name, PlayerAct Act);

    // Players online with their acts.
    public static List<OnlinePlayer> Online(EntityManager em, PrefabCollectionSystem prefabs)
    {
        var result = new List<OnlinePlayer>();
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<User>()));
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var users = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var ue in users)
            {
                var user = em.GetComponentData<User>(ue);
                if (!user.IsConnected) continue;
                result.Add(new OnlinePlayer(user.CharacterName.ToString(), OfUser(em, prefabs, ue)));
            }
        }
        finally
        {
            users.Dispose();
            query.Dispose();
        }
        return result;
    }

    // The most advanced of the players. null if the list is empty.
    public static OnlinePlayer Leader(List<OnlinePlayer> players)
    {
        OnlinePlayer best = null;
        foreach (var p in players)
            if (best == null || p.Act.Act > best.Act.Act || (p.Act.Act == best.Act.Act && p.Act.BossLevel > best.Act.BossLevel))
                best = p;
        return best;
    }

    // Manual acts of routes from the RouteActs setting: "MilitiaIron=2,ChuchSilver=4".
    // Parsed again only when the text of the setting changes.
    static string _manualRaw;
    static Dictionary<string, int> _manual = new(StringComparer.OrdinalIgnoreCase);

    static Dictionary<string, int> Manual()
    {
        string raw = Settings.RouteActs.Value ?? "";
        if (raw == _manualRaw) return _manual;
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && int.TryParse(kv[1].Trim(), out int a) && a >= 1 && a <= 4) result[kv[0].Trim()] = a;
        }
        _manual = result;
        _manualRaw = raw;
        return result;
    }

    // Acts that have at least one route.
    public static HashSet<int> WithRoutes(EntityManager em, PrefabCollectionSystem prefabs)
    {
        var acts = new HashSet<int>();
        foreach (var r in Routes.All(em, prefabs)) acts.Add(OfRoute(em, prefabs, r).Act);
        return acts;
    }

    // The act whose caravans a player gets: their own act, or the nearest lower act with routes. 0 if none.
    public static int Served(int playerAct, HashSet<int> actsWithRoutes) => ActLogic.Served(playerAct, actsWithRoutes);

    static readonly Dictionary<string, int> LevelCache = new();

    // Route level: the highest level in the squad (the horse not counted).
    public static RouteAct OfRoute(EntityManager em, PrefabCollectionSystem prefabs, Routes.Route route)
    {
        if (!LevelCache.TryGetValue(route.Key, out int level))
        {
            level = 0;
            if (em.Exists(route.Patrol) && em.HasBuffer<UnitCompositionGroupUnitEntry>(route.Patrol))
            {
                var units = em.GetBuffer<UnitCompositionGroupUnitEntry>(route.Patrol);
                for (int i = 0; i < units.Length; i++)
                {
                    object unit = units[i].Unit;
                    if (unit is not PrefabGUID guid) continue;
                    if (prefabs._PrefabLookupMap.TryGetName(guid, out string name) && name != null &&
                        name.StartsWith("CHAR_CarriageHorse_", StringComparison.Ordinal)) continue;
                    level = Math.Max(level, LevelOf(em, prefabs, guid));
                }
            }
            LevelCache[route.Key] = level;
        }
        if (Manual().TryGetValue(route.Key, out int manual)) return new(level, manual, true);
        return new(level, OfLevel(level), false);
    }

    // Act of a route by its key (as found caravans are labeled). 0 if unknown.
    public static int OfRouteKey(EntityManager em, PrefabCollectionSystem prefabs, string key)
    {
        foreach (var r in Routes.All(em, prefabs))
            if (r.Key == key) return OfRoute(em, prefabs, r).Act;
        return 0;
    }
}
