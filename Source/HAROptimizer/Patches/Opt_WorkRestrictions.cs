namespace HAROptimizer.Patches;

using System;
using System.Collections.Generic;
using System.Reflection;
using AlienRace;
using HarmonyLib;
using HAROptimizer.Cache;
using RimWorld;
using Verse;
using Verse.AI;

/// <summary>
/// Stage 8: replace HAR's two dynamic work-giver postfixes (D1 <c>GenericJobOnThingPostfix</c> and
/// D2 <c>GenericHasJobOnThingPostfix</c>) with allocation-free versions backed by
/// <see cref="RestrictionCache"/>.
///
/// Fix F2: Deduplicates target <see cref="MethodInfo"/> via <see cref="HashSet{T}"/> so inherited
/// <c>JobOnThing</c> methods on base classes (e.g. <c>WorkGiver_Scanner</c>) are never patched
/// multiple times, and uses <see cref="PatchHelper.ReplacePatch"/> for transactional rollback.
/// </summary>
public static class Opt_WorkRestrictions
{
    private static bool installed;

    public static void Patch(Harmony harmony)
    {
        if (installed) return;

        RestrictionCache.EnsureBuilt();
        if (!RestrictionCache.Built)
        {
            Log.Warning("<color=#5BC0FF>[Xenon]</color> Stage8 RestrictionCache not built, work-giver optimization skipped.");
            PerfTelemetry.RecordReplacement("Stage8 WorkRestrictions: cache not built, skipped");
            return;
        }

        var d1Orig = AccessTools.Method(typeof(HarmonyPatches), nameof(HarmonyPatches.GenericJobOnThingPostfix));
        var d2Orig = AccessTools.Method(typeof(HarmonyPatches), nameof(HarmonyPatches.GenericHasJobOnThingPostfix));
        var d1Ours = AccessTools.Method(typeof(Opt_WorkRestrictions), nameof(JobOnThingPostfix));
        var d2Ours = AccessTools.Method(typeof(Opt_WorkRestrictions), nameof(HasJobOnThingPostfix));

        var seenJobMethods = new HashSet<MethodInfo>();
        var seenHasMethods = new HashSet<MethodInfo>();
        int n1 = 0, n2 = 0;

        foreach (Type giverClass in RestrictionCache.GiverClasses())
        {
            MethodInfo job = AccessTools.Method(giverClass, "JobOnThing");
            if (job != null && seenJobMethods.Add(job))
            {
                if (PatchHelper.ReplacePatch(harmony, job, d1Orig, d1Ours, isPrefix: false,
                        label: $"Stage8 D1 {job.DeclaringType?.Name}.JobOnThing"))
                    n1++;
            }

            MethodInfo has = AccessTools.Method(giverClass, "HasJobOnThing");
            if (has != null && has.IsDeclaredMember() && seenHasMethods.Add(has))
            {
                if (PatchHelper.ReplacePatch(harmony, has, d2Orig, d2Ours, isPrefix: false,
                        label: $"Stage8 D2 {has.DeclaringType?.Name}.HasJobOnThing"))
                    n2++;
            }
        }

        installed = true;
        PerfTelemetry.RecordReplacement($"Stage8 WorkRestrictions (D1/D2): {n1} JobOnThing + {n2} HasJobOnThing unique methods replaced (O(1) cache)");
    }

    public static void HasJobOnThingPostfix(WorkGiver __instance, Pawn pawn, ref bool __result)
    {
        if (!PerfTelemetry.Enabled)
        {
            if (!__result) return;
            __result = Allowed(__instance, pawn);
            return;
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.WorkRestriction);

        if (!__result)
        {
            PerfTelemetry.Fast(OptPatch.WorkRestriction, t0);
            return;
        }

        __result = Allowed(__instance, pawn);
        PerfTelemetry.Executed(OptPatch.WorkRestriction, t0);
    }

    public static void JobOnThingPostfix(WorkGiver __instance, Pawn pawn, ref Job __result)
    {
        if (!PerfTelemetry.Enabled)
        {
            if (__result == null) return;
            if (!Allowed(__instance, pawn))
                __result = null;
            return;
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.WorkRestriction);

        if (__result == null)
        {
            PerfTelemetry.Fast(OptPatch.WorkRestriction, t0);
            return;
        }

        if (!Allowed(__instance, pawn))
            __result = null;

        PerfTelemetry.Executed(OptPatch.WorkRestriction, t0);
    }

    private static bool Allowed(WorkGiver wg, Pawn pawn)
    {
        if (!RestrictionCache.Built)
            return true; // fail-open if cache is invalidated mid-session

        ThingDef pd = pawn?.def;
        if (pd == null || wg == null)
            return true;

        if (RestrictionCache.RaceListsGiverClass(pd, wg.GetType()))
            return true;

        return !RestrictionCache.AnyOtherRaceListsDef(pd, wg.def);
    }

    public static void Invalidate() => installed = false;
}
