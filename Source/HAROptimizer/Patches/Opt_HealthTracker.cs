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
/// Stage 4: gated replacements for HAR's three health-tracker addon postfixes (#197-#199).
///
/// Uses <see cref="PatchHelper.ReplacePatch"/> for transactional installation and preserves
/// HAR's exact <c>___pawn.Spawned</c> check in <see cref="HediffChangedPostfix"/> (Fix F1/F2).
/// </summary>
public static class Opt_HealthTracker
{
    private static readonly HashSet<Type> HediffOnly   = new() { typeof(ConditionHediff) };
    private static readonly HashSet<Type> SeverityOnly = new() { typeof(ConditionHediffSeverity) };

    public static void Patch(Harmony harmony)
    {
        var addHediff = AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.AddHediff),
                        new[] { typeof(Hediff), typeof(BodyPartRecord), typeof(DamageInfo?), typeof(DamageWorker.DamageResult) });
        var removeHediff = AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.RemoveHediff));
        var hediffChanged = AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.Notify_HediffChanged));

        PatchHelper.ReplacePatch(harmony, addHediff,
            AccessTools.Method(typeof(HarmonyPatches), nameof(HarmonyPatches.AddHediffPostfix)),
            AccessTools.Method(typeof(Opt_HealthTracker), nameof(AddHediffPostfix)),
            isPrefix: false, label: "Pawn_HealthTracker.AddHediff (#197)");

        PatchHelper.ReplacePatch(harmony, removeHediff,
            AccessTools.Method(typeof(HarmonyPatches), nameof(HarmonyPatches.RemoveHediffPostfix)),
            AccessTools.Method(typeof(Opt_HealthTracker), nameof(RemoveHediffPostfix)),
            isPrefix: false, label: "Pawn_HealthTracker.RemoveHediff (#198)");

        PatchHelper.ReplacePatch(harmony, hediffChanged,
            AccessTools.Method(typeof(HarmonyPatches), nameof(HarmonyPatches.HediffChangedPostfix)),
            AccessTools.Method(typeof(Opt_HealthTracker), nameof(HediffChangedPostfix)),
            isPrefix: false, label: "Pawn_HealthTracker.Notify_HediffChanged (#199)");
    }

    public static void AddHediffPostfix(Pawn ___pawn, Hediff hediff)
    {
        if (___pawn == null || hediff == null)
            return;

        if (!PerfTelemetry.Enabled)
        {
            if (___pawn.def is not ThingDef_AlienRace)
                return;
            HediffConditionCache.EnsureBuilt();
            if (!HediffConditionCache.IsHediffRelevant(___pawn.def, hediff.def))
                return;
            if (!hediff.def.hairColorOverride.HasValue && !hediff.def.HasDefinedGraphicProperties)
                Regen(___pawn, HediffOnly);
            return;
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.AddHediff);
        if (___pawn.def is not ThingDef_AlienRace)
        {
            PerfTelemetry.Fast(OptPatch.AddHediff, t0);
            return;
        }
        HediffConditionCache.EnsureBuilt();
        if (!HediffConditionCache.IsHediffRelevant(___pawn.def, hediff.def))
        {
            PerfTelemetry.Fast(OptPatch.AddHediff, t0);
            return;
        }
        if (!hediff.def.hairColorOverride.HasValue && !hediff.def.HasDefinedGraphicProperties)
        {
            Regen(___pawn, HediffOnly);
            PerfTelemetry.Executed(OptPatch.AddHediff, t0);
        }
        else
            PerfTelemetry.Thresh(OptPatch.AddHediff, t0);
    }

    public static void RemoveHediffPostfix(Pawn ___pawn, Hediff hediff)
    {
        if (___pawn == null || hediff == null)
            return;

        if (!PerfTelemetry.Enabled)
        {
            if (___pawn.def is not ThingDef_AlienRace)
                return;
            HediffConditionCache.EnsureBuilt();
            if (!HediffConditionCache.IsHediffRelevant(___pawn.def, hediff.def))
                return;
            if (!hediff.def.HasDefinedGraphicProperties && !hediff.def.forceRenderTreeRecache)
                Regen(___pawn, HediffOnly);
            return;
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.RemoveHediff);
        if (___pawn.def is not ThingDef_AlienRace)
        {
            PerfTelemetry.Fast(OptPatch.RemoveHediff, t0);
            return;
        }
        HediffConditionCache.EnsureBuilt();
        if (!HediffConditionCache.IsHediffRelevant(___pawn.def, hediff.def))
        {
            PerfTelemetry.Fast(OptPatch.RemoveHediff, t0);
            return;
        }
        if (!hediff.def.HasDefinedGraphicProperties && !hediff.def.forceRenderTreeRecache)
        {
            Regen(___pawn, HediffOnly);
            PerfTelemetry.Executed(OptPatch.RemoveHediff, t0);
        }
        else
            PerfTelemetry.Thresh(OptPatch.RemoveHediff, t0);
    }

    public static void HediffChangedPostfix(Pawn ___pawn, Hediff hediff)
    {
        // Fix F1: preserve HAR's exact ___pawn.Spawned check so world/unspawned pawns are skipped immediately.
        if (Current.ProgramState != ProgramState.Playing || ___pawn == null || !___pawn.Spawned || hediff == null)
            return;

        if (!PerfTelemetry.Enabled)
        {
            if (___pawn.def is not ThingDef_AlienRace)
                return;
            HediffConditionCache.EnsureBuilt();
            if (!HediffConditionCache.IsSeverityRelevant(___pawn.def, hediff.def))
                return;
            Regen(___pawn, SeverityOnly);
            return;
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.HediffChanged);
        if (___pawn.def is not ThingDef_AlienRace)
        {
            PerfTelemetry.Fast(OptPatch.HediffChanged, t0);
            return;
        }
        HediffConditionCache.EnsureBuilt();
        if (!HediffConditionCache.IsSeverityRelevant(___pawn.def, hediff.def))
        {
            PerfTelemetry.Fast(OptPatch.HediffChanged, t0);
            return;
        }
        Regen(___pawn, SeverityOnly);
        PerfTelemetry.Executed(OptPatch.HediffChanged, t0);
    }

    private static void Regen(Pawn pawn, HashSet<Type> types)
    {
        if (PawnCompCache.TryGetAlienCompFast(pawn, out AlienPartGenerator.AlienComp comp))
            comp.RegenerateAddonGraphicsWithCondition(types);
    }
}
