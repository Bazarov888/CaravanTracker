using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using VampireCommandFramework;

namespace CaravanTracker;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInProcess("VRisingServer.exe")]
[BepInDependency("gg.deca.VampireCommandFramework")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger;
    Harmony _harmony;

    // The BepInEx log has no time, so every line of the mod starts with the server's time: then the log
    // shows how long a squad took to appear, to assemble, to drive its route.
    static string Now => DateTime.Now.ToString("HH:mm:ss");
    internal static void Info(string message) => Logger.LogInfo($"{Now} {message}");
    internal static void Warn(string message) => Logger.LogWarning($"{Now} {message}");
    internal static void Error(string message) => Logger.LogError($"{Now} {message}");

    // Detailed lines (every check, every step of a started caravan) only with Debug.VerboseLog = true.
    internal static void Trace(string message)
    {
        if (Settings.VerboseLog != null && Settings.VerboseLog.Value) Info(message);
    }

    public override void Load()
    {
        Logger = Log;
        Settings.Bind(Config);
        try { _harmony = Harmony.CreateAndPatchAll(typeof(MapIconSpawnPatch), MyPluginInfo.PLUGIN_GUID); }
        catch (Exception e) { Log.LogError($"CaravanTracker: Harmony patch failed, proxy icons become global only on the next check: {e}"); }
        AddComponent<Ticker>();
        CommandRegistry.RegisterCommandType(typeof(Commands));
        if (Settings.EnableDiagnostics.Value) CommandRegistry.RegisterCommandType(typeof(DiagCommands));
        Log.LogInfo($"{MyPluginInfo.PLUGIN_NAME} {MyPluginInfo.PLUGIN_VERSION} loaded, source {Settings.Source.Value}, auto-start every {Settings.AutoInterval.Value} min");
        if (Settings.ModOnly && Settings.AutoInterval.Value <= 0)
            Log.LogWarning("CaravanTracker: Source = Mod and IntervalMinutes = 0: neither the game nor the mod starts caravans, there will be none.");
    }

    public override bool Unload()
    {
        CommandRegistry.UnregisterAssembly();
        _harmony?.UnpatchSelf();
        return true;
    }
}

// Runs every server frame. Forced caravan starts are checked every frame (the work itself once a second),
// the spawn block (Source) every 5 seconds, the caravan search and auto-start every CheckSeconds.
public class Ticker : UnityEngine.MonoBehaviour
{
    public Ticker(IntPtr ptr) : base(ptr) { }
    static DateTime _next;
    static DateTime _nextSource;

    public void Update()
    {
        if (Starter.Any)
        {
            try
            {
                var world = Caravans.ServerWorld();
                if (world != null) Starter.Update(world.EntityManager);
            }
            catch (Exception e) { Plugin.Error($"CaravanTracker start: {e}"); }
        }
        var now = DateTime.UtcNow;
        if (now >= _next)
        {
            _next = now.AddSeconds(Math.Max(5, Settings.CheckSeconds.Value));
            try { Tracker.Tick(false); }
            catch (Exception e) { Plugin.Error($"CaravanTracker: {e}"); }
            try { AutoStart.Tick(); }
            catch (Exception e) { Plugin.Error($"CaravanTracker auto: {e}"); }
        }
        // A route that has just started waiting for a new caravan is blocked within 5 seconds, so a player
        // standing next to it doesn't get a caravan from the game in between.
        if (now >= _nextSource && !Starter.Paused)
        {
            _nextSource = now.AddSeconds(5);
            try
            {
                var world = Caravans.ServerWorld();
                var prefabs = world == null ? null : Caravans.Prefabs(world);
                if (prefabs != null) Starter.ApplySource(world.EntityManager, prefabs);
            }
            catch (Exception e) { Plugin.Error($"CaravanTracker source: {e}"); }
        }
    }
}
