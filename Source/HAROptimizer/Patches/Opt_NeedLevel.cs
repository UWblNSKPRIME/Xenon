namespace HAROptimizer.Patches;

using System;
using System.Collections.Generic;
using AlienRace;
using AlienRace.ExtendedGraphics;
using HarmonyLib;
using HAROptimizer.Cache;
using RimWorld;
using Verse;

/// <summary>
/// Stage 3: replacement for HAR's patch #166 (postfix on <see cref="Need.CurLevel"/> setter).
///
/// Original (AlienRace.HarmonyPatches.NeedLevelPostfix) fires for EVERY CurLevel write on EVERY
/// pawn, humans included, and always queues a full addon regeneration:
/// <c>RegenerateAddonGraphicsWithCondition(pawn, {ConditionMood, ConditionNeed})</c>.
///
/// This postfix gates hard and only enqueues work when a condition actually FLIPS:
///   - non-alien / insensitive race  -> exit before touching the comp list (fast path)
///   - a NeedDef this race ignores   -> exit (food/sleep/rest/comfort/chemical when unused)
///   - raw float drift (0.001f)      -> no flip detected -> exit (no threshold crossings)
///   - pawn not fully generated yet  -> null guards (mentalBreaker may be missing)
///   - the type set is static readonly and only the changed type(s) are passed
/// </summary>
public static class Opt_NeedLevel
{
    private static readonly HashSet<Type> MoodOnly    = new() { typeof(ConditionMood) };
    private static readonly HashSet<Type> NeedOnly    = new() { typeof(ConditionNeed) };
    private static readonly HashSet<Type> MoodAndNeed = new() { typeof(ConditionMood), typeof(ConditionNeed) };

    public static void Patch(Harmony harmony)
    {
        var setter = AccessTools.PropertySetter(typeof(Need), nameof(Need.CurLevel));
        var original = AccessTools.Method(typeof(HarmonyPatches), nameof(HarmonyPatches.NeedLevelPostfix));
        var ours = AccessTools.Method(typeof(Opt_NeedLevel), nameof(Postfix));

        PatchHelper.ReplacePatch(harmony, setter, original, ours, isPrefix: false,
            label: "Need.CurLevel setter (#166 NeedLevelPostfix)");
    }

    public static void Postfix(Need __instance, Pawn ___pawn)
    {
        if (__instance == null || ___pawn == null)
            return;

        if (!PerfTelemetry.Enabled)
        {
            ExecuteCore(__instance, ___pawn);
            return;
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.NeedLevel);
        int outcome = ExecuteCore(__instance, ___pawn);
        if (outcome == 0)
            PerfTelemetry.Fast(OptPatch.NeedLevel, t0);
        else if (outcome == 1)
            PerfTelemetry.Thresh(OptPatch.NeedLevel, t0);
        else
            PerfTelemetry.Executed(OptPatch.NeedLevel, t0);
    }

    // Returns 0 = Fast skip, 1 = Threshold skip, 2 = Executed.
    private static int ExecuteCore(Need need, Pawn pawn)
    {
        ThingDef race = pawn.def;
        // Fix F7: fast-path exit for Humans and non-alien pawns in 1 type check (no Dictionary lookup,
        // and never falls into !CanOptimizeNeeds which previously re-invoked HAR's allocating NeedLevelPostfix).
        if (race is not ThingDef_AlienRace)
            return 0;

        RaceConditionCache.EnsureBuilt();
        if (!RaceConditionCache.CanOptimizeNeeds(race))
        {
            HarmonyPatches.NeedLevelPostfix(pawn);
            return 2;
        }

        if (!RaceConditionCache.IsRaceSensitive(race))
            return 0;

        NeedDef needDef = need.def;
        if (!RaceConditionCache.IsNeedRelevant(race, needDef))
            return 0;

        if (!PawnCompCache.TryGetAlienCompFast(pawn, out AlienPartGenerator.AlienComp comp))
            return 0;

        List<ConditionNeed> needConditions = RaceConditionCache.GetNeedConditions(race);
        if (pawn.Drawer?.renderer?.renderTree == null || !pawn.Drawer.renderer.renderTree.Resolved)
            return 1;

        int count = needConditions?.Count ?? 0;
        PawnCompCache.PawnNeedState state = PawnCompCache.GetOrCreateState(pawn, count);

        bool changedNeed = DetectNeedFlip(needConditions, state, need, RaceConditionCache.NeedIndices(race, needDef));
        bool changedMood = RaceConditionCache.RaceHasMood(race)
                           && needDef == AlienDefOf.Mood
                           && DetectMoodFlip(pawn, state, need.CurLevel);

        if (!changedNeed && !changedMood)
            return 1;

        HashSet<Type> types =
            changedMood && changedNeed ? MoodAndNeed :
            changedMood               ? MoodOnly :
                                        NeedOnly;

        comp.RegenerateAddonGraphicsWithCondition(types);
        return 2;
    }

    private static bool DetectNeedFlip(List<ConditionNeed> conditions, PawnCompCache.PawnNeedState state,
                                       Need need, int[] indices)
    {
        if (conditions == null || indices.Length == 0)
            return false;

        bool changed = false;
        float cur = need.CurLevel;
        float pct = need.CurLevelPercentage;

        for (int j = 0; j < indices.Length; j++)
        {
            int i = indices[j];
            ConditionNeed cn = conditions[i];
            if (cn.need != need.def)
                continue;

            bool satisfied = (cn.percentage ? pct : cur) > cn.level;
            if (!state.needInitialized[i] || satisfied != state.lastNeedSatisfied[i])
            {
                state.needInitialized[i] = true;
                state.lastNeedSatisfied[i] = satisfied;
                changed = true;
            }
        }
        return changed;
    }

    private static bool DetectMoodFlip(Pawn pawn, PawnCompCache.PawnNeedState state, float mood)
    {
        var breaker = pawn.mindState?.mentalBreaker;
        if (breaker == null)
            return false;

        int band = MoodBand(breaker.BreakThresholdExtreme, breaker.BreakThresholdMinor, mood);
        if (band == state.lastMoodBand)
            return false;

        state.lastMoodBand = band;
        return true;
    }

    private static int MoodBand(float breakThresholdExtreme, float breakThresholdMinor, float need)
    {
        if (need < breakThresholdExtreme)         return 0; // ABOUT_TO_BREAK
        if (need < breakThresholdExtreme + 0.05f) return 1; // ON_EDGE
        if (need < breakThresholdMinor)           return 2; // STRESSED
        if (need < 0.65f)                         return 3; // NEUTRAL
        if (need < 0.9f)                          return 4; // CONTENT
        return 5;                                          // HAPPY
    }
}
