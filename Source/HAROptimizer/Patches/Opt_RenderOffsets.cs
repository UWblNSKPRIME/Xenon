namespace HAROptimizer.Patches;

using AlienRace;
using HarmonyLib;
using HAROptimizer.Cache;
using RimWorld;
using UnityEngine;
using Verse;

/// <summary>
/// Stage 10: gate HAR's two per-frame head-offset render patches (<c>BaseHeadOffsetAtHelper</c> #81
/// and <c>BaseHeadOffsetAtPostfix</c> #80) so they short-circuit for pawns whose race has no
/// non-zero 2D or directional head offsets (<see cref="RenderOffsetCache"/>).
/// </summary>
public static class Opt_RenderOffsets
{
    public static void Patch(Harmony harmony)
    {
        RenderOffsetCache.EnsureBuilt();

        // ---- #81: prefix on the transpiler-inserted helper method ----
        var helper = AccessTools.Method(typeof(HarmonyPatches), nameof(HarmonyPatches.BaseHeadOffsetAtHelper));
        if (helper != null)
        {
            harmony.Patch(helper, prefix: new HarmonyMethod(typeof(Opt_RenderOffsets), nameof(HelperPrefix)));
            PatchHelper.RegisterDirectPatch(helper);
        }
        PerfTelemetry.RecordReplacement($"Stage10 BaseHeadOffsetAtHelper (#81): {(helper != null ? "fast-path prefix installed" : "method not found, skipped")}");

        // ---- #80: replace HAR's postfix with a guarded copy ----
        var target = AccessTools.Method(typeof(PawnRenderer), nameof(PawnRenderer.BaseHeadOffsetAt));
        var harOrig = AccessTools.Method(typeof(HarmonyPatches), nameof(HarmonyPatches.BaseHeadOffsetAtPostfix));
        var ours = AccessTools.Method(typeof(Opt_RenderOffsets), nameof(Postfix));
        PatchHelper.ReplacePatch(harmony, target, harOrig, ours, isPrefix: false,
            label: "Stage10 BaseHeadOffsetAtPostfix (#80)");
    }

    // ---- #81 helper ----
    public static bool HelperPrefix(Vector2 offset, Pawn pawn, ref Vector2 __result)
    {
        if (!RenderOffsetCache.Built)
            return true; // fail-open

        if (!PerfTelemetry.Enabled)
        {
            ThingDef def = pawn?.def;
            if (!RenderOffsetCache.RaceMayShiftHead2D(def))
            {
                __result = offset;
                return false;
            }
            __result = ComputeHelperOffset(offset, pawn);
            return false;
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.RenderOffset);

        ThingDef pd = pawn?.def;
        if (!RenderOffsetCache.RaceMayShiftHead2D(pd))
        {
            __result = offset;
            PerfTelemetry.Fast(OptPatch.RenderOffset, t0);
            return false;
        }

        __result = ComputeHelperOffset(offset, pawn);
        PerfTelemetry.Executed(OptPatch.RenderOffset, t0);
        return false;
    }

    private static Vector2 ComputeHelperOffset(Vector2 offset, Pawn pawn)
    {
        Vector2 alienHeadOffset = pawn.ageTracker.CurLifeStageRace is not LifeStageAgeAlien ageAlien
            ? Vector2.zero
            : (pawn.gender == Gender.Female ? ageAlien.headFemaleOffset : ageAlien.headOffset);
        return offset + alienHeadOffset;
    }

    // ---- #80 postfix (verbatim HAR body behind a value-aware race guard) ----
    public static void Postfix(ref Vector3 __result, Rot4 rotation, Pawn ___pawn)
    {
        if (!PerfTelemetry.Enabled)
        {
            ThingDef def = ___pawn?.def;
            if (!RenderOffsetCache.RaceMayShiftHeadDirectional(def))
                return;

            ApplyDirectionalOffset(ref __result, rotation, ___pawn);
            return;
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.RenderOffset);

        ThingDef pd = ___pawn?.def;
        if (!RenderOffsetCache.RaceMayShiftHeadDirectional(pd))
        {
            PerfTelemetry.Fast(OptPatch.RenderOffset, t0);
            return;
        }

        ApplyDirectionalOffset(ref __result, rotation, ___pawn);
        PerfTelemetry.Executed(OptPatch.RenderOffset, t0);
    }

    private static void ApplyDirectionalOffset(ref Vector3 __result, Rot4 rotation, Pawn pawn)
    {
        LifeStageAgeAlien stageAgeAlien = pawn.ageTracker.CurLifeStageRace as LifeStageAgeAlien;
        Vector3 offsetSpecific = (pawn.gender == Gender.Female
                                      ? stageAgeAlien?.headFemaleOffsetDirectional
                                      : stageAgeAlien?.headOffsetDirectional)
                                 ?.GetOffset(rotation)
                                 ?.GetOffset(false, pawn.story.bodyType, pawn.story.headType) ?? Vector3.zero;
        __result += offsetSpecific;
    }
}
