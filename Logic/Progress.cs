namespace CaravanTracker;

// Where a patrol is on its path. Index: the stop it stands at or heads to, from 0 to Count - 1 (the last stop,
// where the game removes the caravan and returns the route to Start). Count: stops of the path, 0 if unknown.
// Number: the same stop counted from 1, as shown to players ("stop 1 of 8" at the start, "8 of 8" at the end).
public sealed record Progress(int Index, int Start, int Count)
{
    public bool AtStart => Index == Start;
    public bool AtLast => Count > 0 && Index == Count - 1;
    public int Number => Index + 1;
    public int Left => Count > 0 ? System.Math.Max(0, Count - 1 - Index) : 0;
    public string Log => Count > 0 ? $"{Number}/{Count}" : $"{Number}/?";
}
