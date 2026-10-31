using System.Collections.Generic;
using System.Text;

namespace CaravanTracker;

// A chat message reaches the player as 512 bytes at most (VampireCommandFramework also cuts replies at
// 509 characters), and a Russian letter takes 2 bytes. Lists are therefore packed into several lines.
public static class ChatLines
{
    public const int MaxBytes = 450;

    // Joins items with the separator into lines of at most maxBytes (UTF-8); the first line starts with
    // prefix. An item longer than a line on its own gets a line of its own.
    public static List<string> Pack(IEnumerable<string> items, string separator, string prefix = "", int maxBytes = MaxBytes)
    {
        var lines = new List<string>();
        var current = new StringBuilder(prefix);
        bool empty = true;
        foreach (var item in items)
        {
            string next = empty ? item : separator + item;
            if (!empty && Encoding.UTF8.GetByteCount(current.ToString() + next) > maxBytes)
            {
                lines.Add(current.ToString());
                current.Clear();
                next = item;
            }
            current.Append(next);
            empty = false;
        }
        if (!empty || prefix.Length > 0) lines.Add(current.ToString());
        return lines;
    }
}
