namespace HAROptimizer.Patches;

using System.Collections.Generic;
using System.Reflection;
using AlienRace;
using HarmonyLib;
using HAROptimizer.Cache;
using RimWorld;
using Verse;

/// <summary>
/// Stage 7: optimized replacements for HAR's 9 hot-path thought patches (#24-32).
///
/// Fix F3 / F5 / F6:
///   - In HAR, <see cref="ThoughtSettings.CanGetThought(ThoughtDef, ThingDef)"/> ALREADY memoizes
///     every (ThoughtDef.shortHash, race.shortHash) pair in a single <c>Dictionary&lt;uint, bool&gt;</c>
///     lookup (~15 IL). Performing two <c>HashSet&lt;Def&gt;.Contains</c> lookups before calling
///     <c>ThoughtSettings.CanGetThought</c> executed 2.6x-4.6x MORE instructions than HAR!
///     Instead, we check a single static boolean <see cref="ThoughtCache.AnyThoughtRestrictionOrCustomization"/>:
///     when false (no race restricts or customizes thoughts), we return immediately in 2 IL;
///     when true, we call <see cref="ThoughtSettings.CanGetThought(ThoughtDef, ThingDef)"/> directly
///     (1 integer shift + 1 uint dictionary lookup, avoiding the extra wrapper hop).
///   - In #25-32, instead of hashing <c>ThingDef</c> in <c>HashSet&lt;ThingDef&gt;</c>, we check
///     <see cref="ThoughtCache.AnyReplacers"/> (1 bool) and then directly read
///     <c>race.alienRace.thoughtSettings.replacerList != null</c> (1 pointer load).
/// </summary>
public static class Opt_Thoughts
{
    public static void Patch(Harmony harmony)
    {
        ThoughtCache.EnsureBuilt();

        // ---- #24 ThoughtUtility.CanGetThought (postfix) ----
        var canGet = AccessTools.Method(typeof(ThoughtUtility), nameof(ThoughtUtility.CanGetThought));
        var canGetOrig = AccessTools.Method(typeof(HarmonyPatches), nameof(HarmonyPatches.CanGetThoughtPostfix));
        var canGetOurs = AccessTools.Method(typeof(Opt_Thoughts), nameof(CanGetThoughtPostfix));
        PatchHelper.ReplacePatch(harmony, canGet, canGetOrig, canGetOurs, isPrefix: false,
            label: "Stage7 CanGetThought (#24)");

        // ---- #25 MemoryThoughtHandler.TryGainMemory (prefix) ----
        var tryGain = AccessTools.Method(typeof(MemoryThoughtHandler), "TryGainMemory",
            new[] { typeof(Thought_Memory), typeof(Pawn) });
        var tryGainOrig = AccessTools.Method(typeof(HarmonyPatches), nameof(HarmonyPatches.TryGainMemoryPrefix));
        var tryGainOurs = AccessTools.Method(typeof(Opt_Thoughts), nameof(TryGainMemoryPrefix));
        PatchHelper.ReplacePatch(harmony, tryGain, tryGainOrig, tryGainOurs, isPrefix: true,
            label: "Stage7 TryGainMemory (#25)");

        // ---- #26 SituationalThoughtHandler.TryCreateThought (prefix) ----
        var tryCreate = AccessTools.Method(typeof(SituationalThoughtHandler), "TryCreateThought");
        var tryCreateOrig = AccessTools.Method(typeof(HarmonyPatches), nameof(HarmonyPatches.TryCreateThoughtPrefix));
        var tryCreateOurs = AccessTools.Method(typeof(Opt_Thoughts), nameof(TryCreateThoughtPrefix));
        PatchHelper.ReplacePatch(harmony, tryCreate, tryCreateOrig, tryCreateOurs, isPrefix: true,
            label: "Stage7 TryCreateThought (#26)");

        // ---- #27-32 MemoryThoughtHandler thought-replacement (prefix, shared) ----
        var replaceOrig = AccessTools.Method(typeof(HarmonyPatches), nameof(HarmonyPatches.ThoughtReplacementPrefix));
        var replaceOurs = AccessTools.Method(typeof(Opt_Thoughts), nameof(ThoughtReplacementPrefix));
        string[] targets =
        {
            "RemoveMemoriesOfDef", "RemoveMemoriesOfDefIf", "RemoveMemoriesOfDefWhereOtherPawnIs",
            "OldestMemoryOfDef", "NumMemoriesOfDef", "GetFirstMemoryOfDef",
        };
        for (int i = 0; i < targets.Length; i++)
        {
            MethodInfo m = AccessTools.Method(typeof(MemoryThoughtHandler), targets[i]);
            if (m == null) continue;
            PatchHelper.ReplacePatch(harmony, m, replaceOrig, replaceOurs, isPrefix: true,
                label: $"Stage7 ThoughtReplacement.{targets[i]} (#27-32)");
        }
    }

    // ---- #24 ----
    public static void CanGetThoughtPostfix(ref bool __result, ThoughtDef def, Pawn pawn)
    {
        if (!PerfTelemetry.Enabled)
        {
            if (!__result || def == null || pawn?.def == null)
                return;
            if (ThoughtCache.Built && !ThoughtCache.AnyThoughtRestrictionOrCustomization)
                return;
            __result = ThoughtSettings.CanGetThought(def, pawn.def);
            return;
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.ThoughtCanGet);

        if (!__result || def == null || pawn?.def == null)
        {
            PerfTelemetry.Fast(OptPatch.ThoughtCanGet, t0);
            return;
        }

        if (ThoughtCache.Built && !ThoughtCache.AnyThoughtRestrictionOrCustomization)
        {
            PerfTelemetry.Fast(OptPatch.ThoughtCanGet, t0);
            return;
        }

        __result = ThoughtSettings.CanGetThought(def, pawn.def);
        PerfTelemetry.Executed(OptPatch.ThoughtCanGet, t0);
    }

    // ---- #25 ----
    public static bool TryGainMemoryPrefix(ref Thought_Memory newThought, MemoryThoughtHandler __instance)
    {
        if (!PerfTelemetry.Enabled)
        {
            if (ThoughtCache.Built && !ThoughtCache.AnyReplacers)
                return true;
            if (newThought == null || __instance?.pawn?.def is not ThingDef_AlienRace r)
                return true;
            var ts = r.alienRace?.thoughtSettings;
            if (ts?.replacerList == null)
                return true;
            ThoughtDef rep = ts.ReplaceIfApplicable(newThought.def);
            if (rep != newThought.def)
                newThought = ThoughtMaker.MakeThought(rep, newThought.CurStageIndex);
            return true;
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.ThoughtReplace);

        if ( (ThoughtCache.Built && !ThoughtCache.AnyReplacers)
             || newThought == null
             || __instance?.pawn?.def is not ThingDef_AlienRace race
             || race.alienRace?.thoughtSettings?.replacerList == null)
        {
            PerfTelemetry.Fast(OptPatch.ThoughtReplace, t0);
            return true;
        }

        ThoughtDef replaced = race.alienRace.thoughtSettings.ReplaceIfApplicable(newThought.def);
        if (replaced != newThought.def)
            newThought = ThoughtMaker.MakeThought(replaced, newThought.CurStageIndex);

        PerfTelemetry.Executed(OptPatch.ThoughtReplace, t0);
        return true;
    }

    // ---- #26 ----
    public static bool TryCreateThoughtPrefix(ref ThoughtDef def, SituationalThoughtHandler __instance,
        ref List<Thought_Situational> ___cachedThoughts)
    {
        if (!PerfTelemetry.Enabled)
        {
            if (ThoughtCache.Built && !ThoughtCache.AnyReplacers)
                return true;
            if (def == null || __instance?.pawn?.def is not ThingDef_AlienRace r)
                return true;
            var ts = r.alienRace?.thoughtSettings;
            if (ts?.replacerList == null)
                return true;
            if (ts.ReplaceIfApplicable(ref def) && ___cachedThoughts != null)
                for (int i = 0; i < ___cachedThoughts.Count; i++)
                    if (___cachedThoughts[i].def == def)
                        return false;
            return true;
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.ThoughtReplace);

        if ((ThoughtCache.Built && !ThoughtCache.AnyReplacers)
            || def == null
            || __instance?.pawn?.def is not ThingDef_AlienRace race
            || race.alienRace?.thoughtSettings?.replacerList == null)
        {
            PerfTelemetry.Fast(OptPatch.ThoughtReplace, t0);
            return true;
        }

        if (race.alienRace.thoughtSettings.ReplaceIfApplicable(ref def) && ___cachedThoughts != null)
            for (int i = 0; i < ___cachedThoughts.Count; i++)
                if (___cachedThoughts[i].def == def)
                {
                    PerfTelemetry.Executed(OptPatch.ThoughtReplace, t0);
                    return false;
                }

        PerfTelemetry.Executed(OptPatch.ThoughtReplace, t0);
        return true;
    }

    // ---- #27-32 ----
    public static void ThoughtReplacementPrefix(MemoryThoughtHandler __instance, ref ThoughtDef def)
    {
        if (!PerfTelemetry.Enabled)
        {
            if (ThoughtCache.Built && !ThoughtCache.AnyReplacers)
                return;
            if (def == null || __instance?.pawn?.def is not ThingDef_AlienRace r)
                return;
            var ts = r.alienRace?.thoughtSettings;
            if (ts?.replacerList != null)
                def = ts.ReplaceIfApplicable(def);
            return;
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.ThoughtReplace);

        if ((ThoughtCache.Built && !ThoughtCache.AnyReplacers)
            || def == null
            || __instance?.pawn?.def is not ThingDef_AlienRace race
            || race.alienRace?.thoughtSettings?.replacerList == null)
        {
            PerfTelemetry.Fast(OptPatch.ThoughtReplace, t0);
            return;
        }

        def = race.alienRace.thoughtSettings.ReplaceIfApplicable(def);
        PerfTelemetry.Executed(OptPatch.ThoughtReplace, t0);
    }
}
