using System;
using System.Collections.Generic;

namespace CarriageTracker;

// How a player's act is found (setting PlayerAct).
// VBlood: by the V Blood drunk. Level: by the character's level (with Bloodcraft leveling that is its level,
// without it the gear level the game shows). Highest: the higher of the two.
public enum PlayerActBy { VBlood, Level, Highest }

// Which routes a player gets when their act has none (setting EmptyAct). Act III has none in the game:
// there are no carriages in Gloomrot and the Cursed Forest (wiki, Carriage page).
public enum EmptyActUse { Higher, Lower, Both }

public static class ActLogic
{
    // Act by level, with the bounds of the V Blood menu: up to 39 - I, 40-59 - II, 60-65 - III, 66+ - IV.
    public static int OfLevel(int level) => level <= 0 ? 0 : level < 40 ? 1 : level < 60 ? 2 : level < 66 ? 3 : 4;

    // A player's act by V Blood: the act of the highest V Blood drunk, and the next act as soon as the final
    // V Blood of an act is drunk: the strongest one of that act in the list (Quincey, Octavian, Cyril). The game
    // itself moves acts on by its journal; the beaten final boss is what players go by. Before 1.0.11 the next
    // act began only with its own first V Blood, so a player who had beaten act II still got act II caravans.
    public static int ByVBlood(IEnumerable<(int Act, bool Final)> drunk)
    {
        int act = 1;
        foreach (var d in drunk)
            act = Math.Max(act, d.Final ? Math.Min(4, d.Act + 1) : d.Act);
        return act;
    }

    public static int Of(PlayerActBy by, int vbloodAct, int level)
    {
        int byLevel = Math.Max(1, OfLevel(level));
        return by switch
        {
            PlayerActBy.Level => byLevel,
            PlayerActBy.Highest => Math.Max(vbloodAct, byLevel),
            _ => vbloodAct,
        };
    }

    // Route acts whose caravans a player of playerAct gets: their own act if it has routes, otherwise the nearest
    // act with routes as EmptyAct says (Higher: the next act up, Lower: the next one down, Both: one of each; if
    // there is none in that direction, the other one). Empty if no act has routes.
    public static List<int> Served(int playerAct, ICollection<int> actsWithRoutes, EmptyActUse use)
    {
        var result = new List<int>();
        if (actsWithRoutes.Contains(playerAct))
        {
            result.Add(playerAct);
            return result;
        }
        int lower = 0, higher = 0;
        for (int a = playerAct - 1; a >= 1; a--) if (actsWithRoutes.Contains(a)) { lower = a; break; }
        for (int a = playerAct + 1; a <= 4; a++) if (actsWithRoutes.Contains(a)) { higher = a; break; }
        if (use == EmptyActUse.Both)
        {
            if (lower > 0) result.Add(lower);
            if (higher > 0) result.Add(higher);
            return result;
        }
        int first = use == EmptyActUse.Lower ? lower : higher;
        int second = use == EmptyActUse.Lower ? higher : lower;
        if (first > 0) result.Add(first);
        else if (second > 0) result.Add(second);
        return result;
    }

    // Region of a route by its key (wiki, Carriage page): bandit routes are in Farbane Woods, militia routes and
    // the church prison carriage (ChurchLegion, "Draculin Prison") in Dunley Farmlands, the other church routes
    // in Silverlight Hills, Venom Blades (Blackfang) routes in Oakveil Woodlands. null for a route the wiki
    // doesn't know (added by a game update).
    public static readonly string[] Regions = { "Farbane", "Dunley", "Silverlight", "Oakveil" };

    public static string Region(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (key.StartsWith("Bandit", StringComparison.Ordinal)) return "Farbane";
        if (key.StartsWith("Militia", StringComparison.Ordinal) || key == "ChurchLegion") return "Dunley";
        if (key.StartsWith("Church", StringComparison.Ordinal) || key.StartsWith("Chuch", StringComparison.Ordinal)) return "Silverlight";
        if (key.StartsWith("Blackfang", StringComparison.Ordinal)) return "Oakveil";
        return null;
    }

    // The RouteActs setting: "MilitiaIron=2, Silverlight=3+4". The left side is a route key or a region,
    // the right side one act or several joined by +. Wrong parts are skipped.
    public static Dictionary<string, SortedSet<int>> ParseRouteActs(string raw)
    {
        var result = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2 || kv[0].Trim().Length == 0) continue;
            var acts = new SortedSet<int>();
            bool ok = true;
            foreach (var a in kv[1].Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(a.Trim(), out int n) && n >= 1 && n <= 4) acts.Add(n);
                else ok = false;
            }
            if (ok && acts.Count > 0) result[kv[0].Trim()] = acts;
        }
        return result;
    }

    // Acts of a route: set by hand for its key, else for its region, else from its level (OfLevel).
    // Empty if the level is unknown and nothing is set by hand.
    public static SortedSet<int> RouteActs(string key, int level, IReadOnlyDictionary<string, SortedSet<int>> manual, out bool isManual)
    {
        isManual = true;
        if (key != null && manual.TryGetValue(key, out var own)) return new SortedSet<int>(own);
        string region = Region(key);
        if (region != null && manual.TryGetValue(region, out var byRegion)) return new SortedSet<int>(byRegion);
        isManual = false;
        var result = new SortedSet<int>();
        int act = OfLevel(level);
        if (act > 0) result.Add(act);
        return result;
    }
}
