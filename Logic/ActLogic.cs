using System.Collections.Generic;

namespace CaravanTracker;

public static class ActLogic
{
    // Act by level, with the bounds of the V Blood menu: up to 39 - I, 40-59 - II, 60-65 - III, 66+ - IV.
    public static int OfLevel(int level) => level <= 0 ? 0 : level < 40 ? 1 : level < 60 ? 2 : level < 66 ? 3 : 4;

    // The act whose caravans a player gets: their own act, or the nearest lower act with routes. 0 if none.
    public static int Served(int playerAct, ICollection<int> actsWithRoutes)
    {
        int act = playerAct;
        while (act >= 1 && !actsWithRoutes.Contains(act)) act--;
        return act < 0 ? 0 : act;
    }
}
