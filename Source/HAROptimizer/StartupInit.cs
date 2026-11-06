namespace HAROptimizer;

using System;
using HarmonyLib;
using HAROptimizer.Cache;
using HAROptimizer.Patches;
using Verse;

/// <summary>
/// Single startup entry point for Xenon (HAROptimizer). Runs from a StaticConstructorOnStartup,
/// which RimWorld invokes after all mod XML/defs are loaded. About.xml <c>loadAfter</c> also puts it
/// after the HAR assembly is already present.
/// </summary>
[StaticConstructorOnStartup]
public static class StartupInit
{
    static StartupInit()
    {
        var harmony = new Harmony(PatchHelper.XenonOwnerId);

        bool anyStageNeedsCaches =
            HAROptimizerFlags.stage1RaceCache
            || HAROptimizerFlags.stage3NeedLevel
            || HAROptimizerFlags.stage4Health
            || HAROptimizerFlags.stage6RegenGate
            || HAROptimizerFlags.stage7Thoughts
            || HAROptimizerFlags.stage8Restrict
            || HAROptimizerFlags.stage9Body
            || HAROptimizerFlags.stage10Render
            || HAROptimizerFlags.stage12ApparelFood;

        // ---- Stage 1: build the race/hediff data caches AFTER defs are loaded. ----
        if (anyStageNeedsCaches)
            Run("Stage1 def-load cache hook", () => Opt_DefLoad.Patch(harmony));

        // ---- Cache hygiene: drop per-pawn caches whenever a game session re-inits. ----
        if (HAROptimizerFlags.clearOnGameInit || anyStageNeedsCaches)
            Run("Opt_GameInit", () => Opt_GameInit.Patch(harmony));

        // ---- Stage 2: SetOverlap.Any() instead of Intersect().Any() in AlienComp. ----
        if (HAROptimizerFlags.stage2Overlaps)
            Run("Stage2 AlienComp Overlaps", () => Opt_AlienCompUpdate.Patch(harmony));

        // ---- Stage 3: replace HAR's unconditional Need.CurLevel postfix (#166). ----
        if (HAROptimizerFlags.stage3NeedLevel)
            Run("Stage3 Need.CurLevel", () => Opt_NeedLevel.Patch(harmony));

        // ---- Stage 4: gated replacements for HAR's hediff addon postfixes (#197-199). ----
        if (HAROptimizerFlags.stage4Health)
            Run("Stage4 hediff postfixes", () => Opt_HealthTracker.Patch(harmony));

        // ---- Stage 5: cached-cantReason replacement for HAR's CanEquip postfix (#210). ----
        if (HAROptimizerFlags.stage5Restrict)
            Run("Stage5 CanEquip", () => Opt_Restrictions.Patch(harmony));

        // ---- Telemetry clock: postfix Game.UpdatePlay to dump counters every 600 ticks. ----
        if (HAROptimizerFlags.benchmark)
            Run("Telemetry tick driver", () => Opt_TelemetryTick.Patch(harmony));

        // ---- A/B probe: count HAR's expensive regen requests in BOTH baseline and optimized. ----
        if (HAROptimizerFlags.benchmark)
            Run("A/B regen probe", () => Opt_ABProbe.Patch(harmony));

        // ---- Stage 6: universal prefix gate on the regen entry point ----
        if (HAROptimizerFlags.stage6RegenGate)
            Run("Stage6 AddonRegenGate", () => Opt_AddonRegenGate.Patch(harmony));

        // ---- Stage 7: gated replacements for HAR's 9 thought patches (#24-32). ----
        if (HAROptimizerFlags.stage7Thoughts)
            Run("Stage7 Opt_Thoughts", () => Opt_Thoughts.Patch(harmony));

        // ---- Stage 9: ReplacedBody (D3) fast-path prefix. ----
        if (HAROptimizerFlags.stage9Body)
            Run("Stage9 Opt_BodyAndHead", () => Opt_BodyAndHead.Patch(harmony));

        // ---- Stage 10: BaseHeadOffsetAt (#80-81) render gate. ----
        if (HAROptimizerFlags.stage10Render)
            Run("Stage10 Opt_RenderOffsets", () => Opt_RenderOffsets.Patch(harmony));

        // ---- Stage 12: gated PawnCanWear (#157) / ApparelScoreGain (#23) / CanEverEat (#67). ----
        if (HAROptimizerFlags.stage12ApparelFood)
            Run("Stage12 Opt_ApparelAndFood", () => Opt_ApparelAndFood.Patch(harmony));

        Log.Message(
            $"{PatchHelper.Tag} Initialized static Harmony hooks: " +
            $"<color=#5BC0FF>{PatchHelper.ActivePatchCount}</color> active patches across " +
            $"<color=#5BC0FF>{PatchHelper.PatchedMethodCount}</color> methods " +
            $"(awaiting DefLoad for dynamic WorkGiver patches & race cache build).");
    }

    private static void Run(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.Error($"{PatchHelper.Tag} '{name}' failed and was skipped: {e}");
        }
    }

    public static void RebuildAllCaches()
    {
        Run("RaceConditionCache",   () => { RaceConditionCache.Invalidate();   RaceConditionCache.Build();   });
        Run("HediffConditionCache", () => { HediffConditionCache.Invalidate(); HediffConditionCache.Build(); });
        Run("ThoughtCache",         () => { ThoughtCache.Invalidate();         ThoughtCache.Build();         });
        Run("RestrictionCache",     () => { RestrictionCache.Invalidate();     RestrictionCache.Build();     });
        Run("BodyOverrideCache",    () => { BodyOverrideCache.Invalidate();    BodyOverrideCache.Build();    });
        Run("RenderOffsetCache",    () => { RenderOffsetCache.Invalidate();    RenderOffsetCache.Build();    });
        Run("PawnCompCache",        () => PawnCompCache.Clear());
    }
}
