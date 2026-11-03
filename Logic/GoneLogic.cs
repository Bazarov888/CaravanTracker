namespace CarriageTracker;

public enum GoneReason
{
    Looted,             // a loot container was emptied by players
    ContainerRemoved,   // a loot container vanished (the game cleans them up after a while)
    ReachedEnd,         // the cart reached the last stop, the game removed it and returned the route to the start
    CartDestroyed,      // the cart was destroyed mid-way (robbed); the route stays where it was
    AtLastStop,         // the cart still exists at the last stop, the game is about to remove it
    LeftWithoutAnimal,  // the cart stands on the map, no animal pulls it
    Parked,             // the game moved the cart off the map
}

// Why a caravan seen at the previous check isn't there now.
public static class GoneLogic
{
    // wasLoot: it was a loot container. before: where its route was then. exists / onMap: the cart or container
    // entity now. route: the route now, null if its patrol is gone.
    public static GoneReason Reason(bool wasLoot, Progress before, bool exists, bool onMap, RouteSnap route)
    {
        if (wasLoot) return exists ? GoneReason.Looted : GoneReason.ContainerRemoved;
        if (exists)
        {
            if (route != null && route.AtEndState) return GoneReason.AtLastStop;
            return onMap ? GoneReason.LeftWithoutAnimal : GoneReason.Parked;
        }
        if (route == null) return GoneReason.CartDestroyed;
        // Checks are 30 seconds apart, so the cart is often last seen one stop before the end. When the game
        // removes a squad at the end it puts the route back to its first stop with no units; a robbed caravan's
        // route stays at the stop where it was robbed.
        if (route.AtEndState) return GoneReason.ReachedEnd;
        bool backToStart = route.Units == 0 && route.Stops != null && route.Stops.AtStart;
        bool wasMidway = before != null && !before.AtStart;
        if (backToStart && wasMidway) return GoneReason.ReachedEnd;
        return GoneReason.CartDestroyed;
    }

    public static string Text(GoneReason r) => r switch
    {
        GoneReason.Looted => "looted",
        GoneReason.ContainerRemoved => "container removed by the game",
        GoneReason.ReachedEnd => "reached the end of the route, the game removed it and returned the route to the start",
        GoneReason.CartDestroyed => "cart destroyed",
        GoneReason.AtLastStop => "reached the last stop, the game removes it there",
        GoneReason.LeftWithoutAnimal => "cart left without its animal",
        _ => "cart parked off the map",
    };
}
