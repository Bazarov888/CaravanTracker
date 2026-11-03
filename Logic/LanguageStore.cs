using System;
using System.Collections.Generic;
using System.IO;

namespace CarriageTracker;

// Players' own chat languages in a text file of "steamid=lang" lines.
// A read that failed (the file locked for a moment, say) is tried again later, and the file is never written
// before a read has succeeded: until 1.0.10 a failed read left an empty list that the next .cv lang wrote over
// the file, and everyone else's choice was lost (audit 02.11). A choice made meanwhile is kept and written with
// the others once the file is read. Writes go to a temporary file that then replaces the old one, so a write cut
// short can't leave the file empty, and nothing is written when the choice didn't change.
public sealed class LanguageStore
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    readonly string _path;
    readonly string _oldPath;          // the file of the mod's old name (CaravanTracker), moved on the first read
    readonly Func<string, bool> _known;
    Dictionary<ulong, string> _saved;  // null until the file has been read
    readonly Dictionary<ulong, string> _unsaved = new();
    DateTime _retryAt;
    bool _warned;

    public Func<DateTime> Now = () => DateTime.UtcNow;
    public Action<string> Warn = _ => { };

    public LanguageStore(string path, Func<string, bool> known, string oldPath = null)
    {
        _path = path;
        _known = known;
        _oldPath = oldPath;
    }

    public bool Loaded => _saved != null;

    public string Of(ulong id)
    {
        Load();
        if (_unsaved.TryGetValue(id, out var lang)) return lang;
        return _saved != null && _saved.TryGetValue(id, out lang) ? lang : null;
    }

    // Returns true when the choice is in the file.
    public bool Set(ulong id, string lang)
    {
        Load();
        if (_saved != null && _unsaved.Count == 0 && _saved.TryGetValue(id, out var was) && was == lang) return true;
        _unsaved[id] = lang;
        return Flush();
    }

    void Load()
    {
        if (_saved != null || Now() < _retryAt) return;
        try
        {
            if (!File.Exists(_path) && _oldPath != null && File.Exists(_oldPath)) File.Move(_oldPath, _path);
            var read = new Dictionary<ulong, string>();
            if (File.Exists(_path))
                foreach (var line in File.ReadAllLines(_path))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2 && ulong.TryParse(parts[0].Trim(), out var id) && _known(parts[1].Trim()))
                        read[id] = parts[1].Trim();
                }
            _saved = read;
        }
        catch (Exception e)
        {
            _retryAt = Now() + RetryAfter;
            if (!_warned) Warn($"can't read player languages, trying again later: {e.Message}");
            _warned = true;
            return;
        }
        if (_unsaved.Count > 0) Flush();
    }

    bool Flush()
    {
        if (_saved == null) return false;
        var next = new Dictionary<ulong, string>(_saved);
        foreach (var kv in _unsaved) next[kv.Key] = kv.Value;
        try
        {
            var lines = new List<string>();
            foreach (var kv in next) lines.Add($"{kv.Key}={kv.Value}");
            string tmp = _path + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Move(tmp, _path, true);
        }
        catch (Exception e)
        {
            Warn($"can't save player languages: {e.Message}");
            return false;
        }
        _saved = next;
        _unsaved.Clear();
        return true;
    }
}
