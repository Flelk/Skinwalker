using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Skinwalker;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static ConfigEntry<bool> ShareMyVoice, ShowVoiceNotice, TestKeys;
    internal static ConfigEntry<int> SpawnChance, SwapChance, DropDamage;
    internal static ConfigEntry<float> LureMinSeconds, LureMaxSeconds, TrapSeconds, IsolationDistance, LineVolume;

    private void Awake()
    {
        Log = Logger;

        ShareMyVoice = Config.Bind("Voice", "ShareMyVoice", true,
            "Capture clips of your voice chat and share them with your lobby. Clips stay in memory and are never saved. False = your mic is not captured.");
        ShowVoiceNotice = Config.Bind("Voice", "ShowVoiceNotice", true, "Show a reminder that voices are being copied when a level starts.");

        LineVolume = Config.Bind("Voice", "LineVolume", 0.7f,
            new ConfigDescription("How loud the copied voices are for you.", new AcceptableValueRange<float>(0f, 1f)));

        SpawnChance = Config.Bind("Monster", "SpawnChance", 100,
            new ConfigDescription("Percent chance it's on a level. Host only.", new AcceptableValueRange<int>(0, 100)));
        LureMinSeconds = Config.Bind("Monster", "LureMinSeconds", 25f,
            new ConfigDescription("Shortest time between lures. Host only.", new AcceptableValueRange<float>(5f, 600f)));
        LureMaxSeconds = Config.Bind("Monster", "LureMaxSeconds", 60f,
            new ConfigDescription("Longest time between lures. Host only.", new AcceptableValueRange<float>(5f, 600f)));
        SwapChance = Config.Bind("Monster", "SwapChance", 35,
            new ConfigDescription("Percent chance it swaps with you after a kidnapping. Host only.", new AcceptableValueRange<int>(0, 100)));
        DropDamage = Config.Bind("Monster", "DropDamage", 25,
            new ConfigDescription("Damage when it drops you. Host only.", new AcceptableValueRange<int>(0, 200)));
        TrapSeconds = Config.Bind("Monster", "TrapSeconds", 25f,
            new ConfigDescription("How long the Swap keeps you stuck. Host only.", new AcceptableValueRange<float>(3f, 120f)));

        IsolationDistance = Config.Bind("Monster", "IsolationDistance", 15f,
            new ConfigDescription("It only goes after someone with no teammate this close, in meters. Host only.", new AcceptableValueRange<float>(0f, 60f)));

        TestKeys = Config.Bind("Testing", "TestKeys", false, "F10 forces a lure and F11 forces the Swap. Host or solo.");

        Harmony harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll(typeof(EnemyPatches));
        harmony.PatchAll(typeof(VoiceFramePatch));

        Net.Init();

        var go = new GameObject("Skinwalker");
        go.hideFlags = HideFlags.HideAndDontSave;
        DontDestroyOnLoad(go);
        go.AddComponent<VoiceCapture>();
        go.AddComponent<SkinwalkerManager>();

        Log.LogInfo($"{MyPluginInfo.PLUGIN_NAME} {MyPluginInfo.PLUGIN_VERSION} loaded.");
    }
}
