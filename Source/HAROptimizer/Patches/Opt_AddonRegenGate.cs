namespace HAROptimizer.Patches;

using System;
using System.Collections.Generic;
using AlienRace;
using HarmonyLib;
using HAROptimizer.Cache;
using Verse;

/// <summary>
/// Stage 6: a universal prefix gate on HAR's addon-regeneration entry point
/// <c>AlienComp.RegenerateAddonGraphicsWithCondition(Pawn, HashSet&lt;Type&gt;)</c>.
/// </summary>
public static class Opt_AddonRegenGate
{
    public static void Patch(Harmony harmony)
    {
        var target = AccessTools.Method(
            typeof(AlienPartGenerator.AlienComp),
            nameof(AlienPartGenerator.AlienComp.RegenerateAddonGraphicsWithCondition),
            new[] { typeof(Pawn), typeof(HashSet<Type>) });
        if (target == null)
        {
            Log.Warning($"{PatchHelper.Tag} Stage6 static RegenerateAddonGraphicsWithCondition(Pawn,types) not found, gate skipped.");
            PerfTelemetry.RecordReplacement("Stage6 AddonRegenGate: static overload NOT found, skipped");
            return;
        }
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(Opt_AddonRegenGate), nameof(Prefix)));
        PatchHelper.RegisterDirectPatch(target);
        PerfTelemetry.RecordReplacement("Stage6 AddonRegenGate: prefix gate installed on (Pawn, types) overload");
    }

    public static bool Prefix(Pawn pawn, HashSet<Type> types)
    {
        if (!PerfTelemetry.Enabled)
        {
            return ShouldAllow(pawn, types);
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.AddonRegenGate);
        bool allow = ShouldAllow(pawn, types);
        if (allow)
            PerfTelemetry.Executed(OptPatch.AddonRegenGate, t0);
        else
            PerfTelemetry.Fast(OptPatch.AddonRegenGate, t0);
        return allow;
    }

    private static bool ShouldAllow(Pawn pawn, HashSet<Type> types)
    {
        if (pawn == null || types == null || types.Count == 0)
            return true;

        ThingDef def = pawn.def;
        if (def is not ThingDef_AlienRace)
            return false;

        RaceConditionCache.EnsureBuilt();
        return RaceConditionCache.RaceOwnsAnyType(def, types);
    }
}
