using System.Collections.Generic;

namespace CaravanTracker;

// Who gets the welcome lines: a player who is in the world now and wasn't at the previous check. A player
// who leaves is forgotten, so they are greeted again next time. Nobody is greeted at the first check after
// the server start (primed = false).
public static class Welcome
{
    public static List<ulong> Greet(HashSet<ulong> seen, IEnumerable<ulong> inWorld, bool primed)
    {
        var now = new HashSet<ulong>(inWorld);
        var greet = new List<ulong>();
        if (primed)
            foreach (var id in now)
                if (!seen.Contains(id)) greet.Add(id);
        seen.Clear();
        seen.UnionWith(now);
        return greet;
    }
}
