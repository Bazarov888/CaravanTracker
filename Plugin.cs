using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using VampireCommandFramework;

namespace CaravanTracker;

public static class MyPluginInfo
{
    public const string PLUGIN_GUID = "Bazarov.CaravanTracker";
    public const string PLUGIN_NAME = "CaravanTracker";
    public const string PLUGIN_VERSION = "1.0.4";
}

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInProcess("VRisingServer.exe")]
[BepInDependency("gg.deca.VampireCommandFramework")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger;
    Harmony _harmony;

    // Detailed lines (every check, every step of a started caravan) only with Debug.VerboseLog = true.
    internal static void Trace(string message)
    {
        if (Settings.VerboseLog != null && Settings.VerboseLog.Value) Logger.LogInfo(message);
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
    }

    public override bool Unload()
    {
        CommandRegistry.UnregisterAssembly();
        _harmony?.UnpatchSelf();
        return true;
    }
}

// Runs every server frame. Forced caravan starts are checked every frame,
// the caravan search and auto-start run every CheckSeconds.
public class Ticker : UnityEngine.MonoBehaviour
{
    public Ticker(IntPtr ptr) : base(ptr) { }
    static DateTime _next;

    public void Update()
    {
        if (Starter.Any)
        {
            try
            {
                var world = Caravans.ServerWorld();
                if (world != null) Starter.Update(world.EntityManager);
            }
            catch (Exception e) { Plugin.Logger.LogError($"CaravanTracker start: {e}"); }
        }
        var now = DateTime.UtcNow;
        if (now < _next) return;
        _next = now.AddSeconds(Math.Max(5, Settings.CheckSeconds.Value));
        try { Tracker.Tick(false); }
        catch (Exception e) { Plugin.Logger.LogError($"CaravanTracker: {e}"); }
        try { AutoStart.Tick(); }
        catch (Exception e) { Plugin.Logger.LogError($"CaravanTracker auto: {e}"); }
        try
        {
            var world = Caravans.ServerWorld();
            var prefabs = world == null ? null : Caravans.Prefabs(world);
            if (prefabs != null) Starter.ApplySource(world.EntityManager, prefabs);
        }
        catch (Exception e) { Plugin.Logger.LogError($"CaravanTracker source: {e}"); }
    }
}
