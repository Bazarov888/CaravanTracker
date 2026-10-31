namespace CaravanTracker;

// Which found objects are caravans, and which caravans are announced.
public static class CaravanRules
{
    public const float PullDistance = 30f;

    // A cart is still pulled while its animal exists, stands on the map and is near it. In a fight the route
    // stops (StoppedDueToUnit) and the animal can end up farther away (31.10 21:53, MilitiaCotton: the icon
    // vanished for 1.5 minutes during the attack), so then any distance counts.
    public static bool Pulled(bool animalExists, bool animalOnMap, float distance, string move) =>
        animalExists && animalOnMap && (distance < PullDistance || move == "StoppedDueToUnit");

    // A loot container is a caravan while it has loot and stands on the map.
    public static bool ShowContainer(bool empty, bool onMap) => !empty && onMap;

    // A cart is a caravan when it stands on the map, its loot container isn't already shown in its place,
    // it isn't at the last stop (the game is about to remove it) and an animal still pulls it.
    public static bool ShowCart(bool onMap, bool lootInPlace, string move, bool orphan) =>
        onMap && !lootInPlace && move != "WaitForDespawn" && !orphan;

    // A cart is announced once, the first time it is seen setting out: on the map, moving (not frozen), pulled
    // by its animal and not at the last stop. Nothing is announced at the first check after the server start
    // (primed = false): those carts were already out.
    public static bool Announce(bool primed, bool announcedBefore, bool onMap, bool frozen, bool orphan, string move) =>
        primed && !announcedBefore && onMap && !frozen && !orphan && move != "WaitForDespawn";
}
