using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using VampireCommandFramework;

namespace CaravanTracker;

// Each player's own chat language (.cv lang), kept in BepInEx/config/Bazarov.CaravanTracker.languages.txt
// as "steamid=en" lines. Players without one get General.Language from the config.
static class Languages
{
    static Dictionary<ulong, string> _byPlayer;
    static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "Bazarov.CaravanTracker.languages.txt");

    public static string Of(ulong platformId)
    {
        Load();
        return _byPlayer.TryGetValue(platformId, out var lang) ? lang : null;
    }

    public static void Set(ulong platformId, string lang)
    {
        Load();
        _byPlayer[platformId] = lang;
        try
        {
            var lines = new List<string>();
            foreach (var kv in _byPlayer) lines.Add($"{kv.Key}={kv.Value}");
            File.WriteAllLines(FilePath, lines);
        }
        catch (Exception e) { Plugin.Warn($"CaravanTracker: can't save player languages: {e.Message}"); }
    }

    static void Load()
    {
        if (_byPlayer != null) return;
        _byPlayer = new();
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2 && ulong.TryParse(parts[0].Trim(), out var id) && L.Known(parts[1].Trim()))
                    _byPlayer[id] = parts[1].Trim();
            }
        }
        catch (Exception e) { Plugin.Warn($"CaravanTracker: can't read player languages: {e.Message}"); }
    }
}

// Every command of this mod answers in the language of the player who typed it.
class LanguageMiddleware : CommandMiddleware
{
    static bool Ours(MethodInfo method) => method?.DeclaringType?.Assembly == typeof(Plugin).Assembly;

    public override void BeforeExecute(ICommandContext ctx, CommandAttribute attribute, MethodInfo method)
    {
        if (Ours(method) && ctx is ChatCommandContext chat) L.Use(chat.Event.User.PlatformId);
    }

    public override void AfterExecute(ICommandContext ctx, CommandAttribute attribute, MethodInfo method)
    {
        if (Ours(method)) L.UseDefault();
    }
}
