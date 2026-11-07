using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Skinwalker;

/// <summary>
/// The Skinwalker is the game's "Enemy - Hidden" kidnapper with a new brain. It keeps that enemy's health, stuns, physics,
/// networking and its pick up, drag and release states. These patches add one to the level and take away its own
/// decisions about when to attack, so the brain can make them.
/// </summary>
internal static class EnemyPatches
{
    private static EnemySetup setup;
    private static bool pending;

    private static EnemySetup FindSetup(EnemyDirector director)
    {
        var all = director.enemiesDifficulty1.Concat(director.enemiesDifficulty2).Concat(director.enemiesDifficulty3);
        return all.FirstOrDefault(s => s && s.spawnObjects != null && s.spawnObjects.Count == 1 &&
                                       s.spawnObjects[0].PrefabName.Contains("Hidden"));
    }

    // The level's enemy list is built by calling GetEnemy totalAmount times, so one more call gets us on the level.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(EnemyDirector), nameof(EnemyDirector.AmountSetup))]
    private static void AddOne(EnemyDirector __instance)
    {
        pending = false;
        if (Random.Range(0, 100) >= Plugin.SpawnChance.Value) return;

        setup = FindSetup(__instance);
        if (!setup)
        {
            Plugin.Log.LogWarning("Couldn't find the game's Hidden enemy, so no Skinwalker on this level.");
            return;
        }
        pending = true;
        __instance.totalAmount++;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(EnemyDirector), nameof(EnemyDirector.GetEnemy))]
    private static bool UseOurs(ref EnemySetup __result)
    {
        if (!pending) return true;
        pending = false;
        __result = setup;
        Plugin.Log.LogInfo("Skinwalker added to this level.");
        return false;
    }

    private static bool Ours(EnemyHidden hidden) => SkinwalkerManager.Instance && SkinwalkerManager.Instance.Hidden == hidden;

    private static bool BrainHolding(EnemyHidden hidden) => Ours(hidden) && SkinwalkerManager.Instance.Brain != null && SkinwalkerManager.Instance.Brain.Holding;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(EnemyHidden), nameof(EnemyHidden.StateIdle))]
    private static bool Idle(EnemyHidden __instance) => !BrainHolding(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(EnemyHidden), nameof(EnemyHidden.StateRoam))]
    private static bool Roam(EnemyHidden __instance) => !BrainHolding(__instance);

    // Leave walks it across the level. It stays put instead, so going from there to Idle is enough.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(EnemyHidden), nameof(EnemyHidden.StateLeave))]
    private static bool Leave(EnemyHidden __instance)
    {
        if (!Ours(__instance)) return true;
        __instance.UpdateState(EnemyHidden.State.Idle);
        return false;
    }

    // It only attacks when the brain says so, and never just because it saw someone or was grabbed.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(EnemyHidden), nameof(EnemyHidden.OnVision))]
    private static bool Vision(EnemyHidden __instance) => !Ours(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(EnemyHidden), nameof(EnemyHidden.OnGrabbed))]
    private static bool Grabbed(EnemyHidden __instance) => !Ours(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(EnemyHidden), nameof(EnemyHidden.OnInvestigate))]
    private static bool Investigate(EnemyHidden __instance) => !BrainHolding(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.PlayerDeathRPC))]
    private static void Death(PlayerAvatar __instance) => SkinwalkerManager.Instance?.Brain?.OnPlayerDied(__instance);
}
