namespace HAROptimizer.Patches;

using System.Reflection;
using AlienRace;
using HarmonyLib;
using HAROptimizer.Cache;
using RimWorld;
using Verse;

/// <summary>
/// Stage 12: gated replacements for HAR's three hottest restriction postfixes:
///   #23   JobGiver_OptimizeApparel.ApparelScoreGain -> ApparelScoreGainPostFix
///   #67   RaceProperties.CanEverEat               -> CanEverEatPostfix
///   #157  ApparelProperties.PawnCanWear           -> PawnCanWearPostfix
///
/// Fix F2 / F6: Uses <see cref="PatchHelper.ReplacePatch"/> for transactional replacement
/// and separates the zero-overhead production path when <see cref="PerfTelemetry.Enabled"/> is false.
/// </summary>
public static class Opt_ApparelAndFood
{
    private static bool installed;

    public static void Patch(Harmony harmony)
    {
        if (installed)
            return;

        RestrictionCache.EnsureBuilt();

        UnpatchAndReplace(harmony,
            AccessTools.Method(typeof(ApparelProperties), nameof(ApparelProperties.PawnCanWear),
                new[] { typeof(Pawn), typeof(bool) }),
            nameof(HarmonyPatches.PawnCanWearPostfix),
            nameof(PawnCanWearPostfix), "Stage12 PawnCanWear (#157)");

        UnpatchAndReplace(harmony,
            AccessTools.Method(typeof(JobGiver_OptimizeApparel), nameof(JobGiver_OptimizeApparel.ApparelScoreGain)),
            nameof(HarmonyPatches.ApparelScoreGainPostFix),
            nameof(ApparelScoreGainPostFix), "Stage12 ApparelScoreGain (#23)");

        UnpatchAndReplace(harmony,
            AccessTools.Method(typeof(RaceProperties), nameof(RaceProperties.CanEverEat), new[] { typeof(ThingDef) }),
            nameof(HarmonyPatches.CanEverEatPostfix),
            nameof(CanEverEatPostfix), "Stage12 CanEverEat (#67)");

        installed = true;
    }

    private static void UnpatchAndReplace(Harmony harmony, MethodBase target, string harName, string ourName, string label)
    {
        var original = AccessTools.Method(typeof(HarmonyPatches), harName);
        var ours = AccessTools.Method(typeof(Opt_ApparelAndFood), ourName);
        PatchHelper.ReplacePatch(harmony, target, original, ours, isPrefix: false, label: label);
    }

    // ---- #157 ApparelProperties.PawnCanWear(Pawn, bool) ----
    public static void PawnCanWearPostfix(ApparelProperties __instance, Pawn pawn, ref bool __result)
    {
        if (!PerfTelemetry.Enabled)
        {
            if (!__result || RestrictionCache.ApparelTriviallyAllowed(pawn?.def))
                return;
            __result &= RaceRestrictionSettings.CanWear(CachedData.GetApparelFromApparelProps(__instance), pawn.def);
            return;
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.ApparelFood);

        if (!__result || RestrictionCache.ApparelTriviallyAllowed(pawn?.def))
        {
            PerfTelemetry.Fast(OptPatch.ApparelFood, t0);
            return;
        }

        __result &= RaceRestrictionSettings.CanWear(CachedData.GetApparelFromApparelProps(__instance), pawn.def);
        PerfTelemetry.Executed(OptPatch.ApparelFood, t0);
    }

    // ---- #23 JobGiver_OptimizeApparel.ApparelScoreGain ----
    public static void ApparelScoreGainPostFix(Pawn pawn, Apparel ap, ref float __result)
    {
        if (!PerfTelemetry.Enabled)
        {
            if (!(__result >= 0f) || RestrictionCache.ApparelTriviallyAllowed(pawn?.def))
                return;
            if (!RaceRestrictionSettings.CanWear(ap.def, pawn.def))
                __result = -1001f;
            return;
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.ApparelFood);

        if (!(__result >= 0f) || RestrictionCache.ApparelTriviallyAllowed(pawn?.def))
        {
            PerfTelemetry.Fast(OptPatch.ApparelFood, t0);
            return;
        }

        if (!RaceRestrictionSettings.CanWear(ap.def, pawn.def))
            __result = -1001f;
        PerfTelemetry.Executed(OptPatch.ApparelFood, t0);
    }

    // ---- #67 RaceProperties.CanEverEat(ThingDef) ----
    public static void CanEverEatPostfix(ref bool __result, RaceProperties __instance, ThingDef t)
    {
        if (!PerfTelemetry.Enabled)
        {
            if (!__instance.Humanlike || !__result || RestrictionCache.FoodTriviallyAllowed)
                return;
            __result = RaceRestrictionSettings.CanEat(t, CachedData.GetRaceFromRaceProps(__instance));
            return;
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.ApparelFood);

        if (!__instance.Humanlike || !__result || RestrictionCache.FoodTriviallyAllowed)
        {
            PerfTelemetry.Fast(OptPatch.ApparelFood, t0);
            return;
        }

        __result = RaceRestrictionSettings.CanEat(t, CachedData.GetRaceFromRaceProps(__instance));
        PerfTelemetry.Executed(OptPatch.ApparelFood, t0);
    }
}
