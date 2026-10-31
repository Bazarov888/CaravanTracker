namespace CaravanTracker;

// Values the mod writes into a route's UnitCompositionSpawner, and when.
// PlayerRange (x, y): the game spawns the squad only for a player within y meters of the patrol point.
public static class SpawnRange
{
    // The game's own range, seen on every route of a live server.
    public const float NormalY = 90f;
    // Source = Mod: nobody stands within 1 mm of a patrol point, so the game doesn't spawn. Kept above 0
    // on purpose, in case the game takes 0 for "no limit".
    public const float BlockedY = 0.001f;
    // A forced start: any distance.
    public const float StartY = 100000f;
    // The game's own respawn timers (seconds), seen on every route of a live server.
    public const int NormalMinRespawn = 1000;
    public const int NormalMaxRespawn = 2700;

    public static bool IsBlocked(float x, float y) => y > 0f && y < 0.01f;
    public static bool IsZero(float x, float y) => x == 0f && y == 0f;
    public static bool IsStart(float x, float y) => y > 1000f;

    // A route with no caravan out: waiting for a new squad, or standing at its last stop until the game removes
    // what is left and returns it to the start (WaitingForDespawn). The second kind must be blocked too: a player
    // coming near wakes it, the game resets it and would spawn its own squad before the next check.
    public static bool Idle(string spawnerState) => spawnerState == "WaitingForSpawn" || spawnerState == "WaitingForDespawn";

    // Source: what to write into a route that isn't being started by the mod; null = leave it.
    // With Mod a route waiting for a new squad is blocked; any other route gets the game's range back
    // if it is blocked (or zero, which an earlier version could leave).
    public static float? SourceTarget(bool modOnly, bool waitingForSpawn, float x, float y)
    {
        if (modOnly && waitingForSpawn) return IsBlocked(x, y) ? null : BlockedY;
        return IsBlocked(x, y) || IsZero(x, y) ? NormalY : null;
    }

    // End of a start's assembly window: the saved range comes back, but a blocked or zero one becomes the
    // game's range, so the squad on the road behaves as the game's own. The block returns once the route
    // waits for a new squad (SourceTarget).
    public static float RestoreY(float savedX, float savedY) =>
        IsBlocked(savedX, savedY) || IsZero(savedX, savedY) ? NormalY : savedY;

    // .cv cleanup: everything the mod may have written becomes the game's range.
    public static bool CleanupOpens(float x, float y) => IsBlocked(x, y) || IsZero(x, y) || IsStart(x, y);

    // A start sets both respawn timers to 0; the game's own are never 0.
    public static bool TimersZero(double min, double max) => min == 0 && max == 0;
}
