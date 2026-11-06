namespace HAROptimizer;

using HAROptimizer.Patches;
using RimWorld;
using UnityEngine;
using Verse;

/// <summary>
/// Live (static) feature flags read by <see cref="StartupInit"/>. Persisted via
/// <see cref="HAROptimizerModSettings"/>. Toggles take effect on next launch (patches are
/// applied once at StaticConstructorOnStartup).
/// </summary>
public static class HAROptimizerFlags
{
    public static bool stage1RaceCache = true;
    public static bool stage2Overlaps  = true;
    public static bool stage3NeedLevel = true;
    public static bool stage4Health    = true;
    public static bool stage5Restrict  = true;
    public static bool stage6RegenGate = true;
    public static bool stage7Thoughts  = true;
    public static bool stage8Restrict  = true;
    public static bool stage9Body      = true;
    public static bool stage10Render   = true;
    public static bool stage12ApparelFood = true;
    public static bool clearOnGameInit = true;
    public static bool benchmark       = false;
    public static bool baseline        = false;
}

public class HAROptimizerModSettings : ModSettings
{
    public bool stage1RaceCache = true;
    public bool stage2Overlaps  = true;
    public bool stage3NeedLevel = true;
    public bool stage4Health    = true;
    public bool stage5Restrict  = true;
    public bool stage6RegenGate = true;
    public bool stage7Thoughts  = true;
    public bool stage8Restrict  = true;
    public bool stage9Body      = true;
    public bool stage10Render   = true;
    public bool stage12ApparelFood = true;
    public bool clearOnGameInit = true;
    public bool enableBenchmarkLogging = false;
    public bool baselineAB = false;

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref stage1RaceCache, "stage1RaceCache", true);
        Scribe_Values.Look(ref stage2Overlaps,  "stage2Overlaps",  true);
        Scribe_Values.Look(ref stage3NeedLevel, "stage3NeedLevel", true);
        Scribe_Values.Look(ref stage4Health,    "stage4Health",    true);
        Scribe_Values.Look(ref stage5Restrict,  "stage5Restrict",  true);
        Scribe_Values.Look(ref stage6RegenGate, "stage6RegenGate", true);
        Scribe_Values.Look(ref stage7Thoughts,  "stage7Thoughts",  true);
        Scribe_Values.Look(ref stage8Restrict,  "stage8Restrict",  true);
        Scribe_Values.Look(ref stage9Body,      "stage9Body",      true);
        Scribe_Values.Look(ref stage10Render,   "stage10Render",   true);
        Scribe_Values.Look(ref stage12ApparelFood, "stage12ApparelFood", true);
        Scribe_Values.Look(ref clearOnGameInit, "clearOnGameInit", true);
        Scribe_Values.Look(ref enableBenchmarkLogging, "enableBenchmarkLogging", false);
        Scribe_Values.Look(ref baselineAB, "baselineAB", false);
    }
}

public class HAROptimizerMod : Mod
{
    public static HAROptimizerMod instance;
    private readonly HAROptimizerModSettings settings;

    public HAROptimizerMod(ModContentPack content) : base(content)
    {
        instance = this;
        settings = GetSettings<HAROptimizerModSettings>();

        HAROptimizerFlags.stage1RaceCache = settings.stage1RaceCache;
        HAROptimizerFlags.stage2Overlaps  = settings.stage2Overlaps;
        HAROptimizerFlags.stage3NeedLevel = settings.stage3NeedLevel;
        HAROptimizerFlags.stage4Health    = settings.stage4Health;
        HAROptimizerFlags.stage5Restrict  = settings.stage5Restrict;
        HAROptimizerFlags.stage6RegenGate = settings.stage6RegenGate;
        HAROptimizerFlags.stage7Thoughts  = settings.stage7Thoughts;
        HAROptimizerFlags.stage8Restrict  = settings.stage8Restrict;
        HAROptimizerFlags.stage9Body      = settings.stage9Body;
        HAROptimizerFlags.stage10Render   = settings.stage10Render;
        HAROptimizerFlags.stage12ApparelFood = settings.stage12ApparelFood;
        HAROptimizerFlags.clearOnGameInit = settings.clearOnGameInit;
        HAROptimizerFlags.benchmark       = settings.enableBenchmarkLogging;
        HAROptimizerFlags.baseline        = settings.baselineAB;

        bool markerBaseline = System.IO.File.Exists(System.IO.Path.Combine(content.RootDir, "benchmark_baseline.txt"));
        bool markerEnable   = System.IO.File.Exists(System.IO.Path.Combine(content.RootDir, "benchmark_enable.txt"));
        if (markerBaseline)
            HAROptimizerFlags.baseline = true;

        if (HAROptimizerFlags.baseline)
        {
            HAROptimizerFlags.stage2Overlaps  = false;
            HAROptimizerFlags.stage3NeedLevel = false;
            HAROptimizerFlags.stage4Health    = false;
            HAROptimizerFlags.stage5Restrict  = false;
            HAROptimizerFlags.stage6RegenGate = false;
            HAROptimizerFlags.stage7Thoughts  = false;
            HAROptimizerFlags.stage8Restrict  = false;
            HAROptimizerFlags.stage9Body      = false;
            HAROptimizerFlags.stage10Render   = false;
            HAROptimizerFlags.stage12ApparelFood = false;
            PerfTelemetry.SetBaselineMode("A/B baseline: HAR original NeedLevel/hediff/equip patches left active, opt stages skipped.");
        }

        bool telemetry = settings.enableBenchmarkLogging || markerBaseline || markerEnable;
#if DEBUG
        telemetry = true;
#endif
        PerfTelemetry.Enabled = telemetry;
        HAROptimizerFlags.benchmark = telemetry;
        if (telemetry)
            PerfTelemetry.Init(content.RootDir);
    }

    public override string SettingsCategory() => "<color=#5BC0FF>Xenon</color> (HAR Optimizer)";

    public override void DoSettingsWindowContents(Rect inRect)
    {
        base.DoSettingsWindowContents(inRect);

        var list = new Listing_Standard();
        var view = inRect.ContractedBy(18f);
        list.Begin(view);

        list.Label(
            $"<color=#5BC0FF><b>Xenon</b></color>, Active Harmony patches: " +
            $"<color=#5BC0FF>{PatchHelper.ActivePatchCount}</color> across " +
            $"<color=#5BC0FF>{PatchHelper.PatchedMethodCount}</color> methods " +
            $"({PatchHelper.ReplacedHarCount} HAR patches replaced, {PatchHelper.DirectPatchCount} fast-path hooks, " +
            $"<color=#F2D45C>{PatchHelper.ConflictCount} shared targets</color>)");
        list.Gap(4f);
        list.Label("Optimization modules (each can be disabled independently; full game restart required to apply)");
        list.Gap(4f);
        Stage(list, "Stage 1: Race / need condition caches", ref settings.stage1RaceCache,
            "Precomputes which alien races customize moods, needs and hediffs so every hot-path gate is an O(1) set probe instead of a per-call scan.");
        Stage(list, "Stage 2: Intersect().Any() -> SetOverlap.Any()", ref settings.stage2Overlaps,
            "Transpiles HAR's AlienComp update so an allocation-free set overlap replaces the LINQ Intersect().Any() closure.");
        Stage(list, "Stage 3: Need.CurLevel gated postfix (#166)", ref settings.stage3NeedLevel,
            "Only asks HAR to regenerate addon graphics when a need actually crosses a threshold that can change a condition. Biggest single win.");
        Stage(list, "Stage 4: Hediff change gates (#197-199)", ref settings.stage4Health,
            "Gated AddHediff / RemoveHediff / Notify_HediffChanged postfixes: skip races with no hediff-keyed addon conditions.");
        Stage(list, "Stage 5: CanEquip cached cantReason (#210)", ref settings.stage5Restrict,
            "Replaces HAR's CanEquip postfix; the '{race} can't wear/equip' reason string is built once per race instead of every failed query.");
        Stage(list, "Stage 6: Addon-regen prefix gate", ref settings.stage6RegenGate,
            "Universal prefix on RegenerateAddonGraphicsWithCondition: never enters the expensive regen when no addon condition changed.");
        Stage(list, "Stage 7: Gated thought patches (#24-32)", ref settings.stage7Thoughts,
            "Fast exit for pawns whose race customizes no thoughts; delegates to HAR's exact logic for the customizable ones.");
        Stage(list, "Stage 8: O(1) work-giver postfixes (D1/D2)", ref settings.stage8Restrict,
            "Turns the per-work-query LINQ scan over all races x workGivers into two set probes. Installs only for giver classes a race actually lists.");
        Stage(list, "Stage 9: ReplacedBody fast-path prefix (D3)", ref settings.stage9Body,
            "Skips the pawn life-stage lookup when the pawn's race never overrides its body.");
        Stage(list, "Stage 10: BaseHeadOffsetAt render gate (#80-81)", ref settings.stage10Render,
            "Skips the head-offset field-chain per frame for races with no non-zero alien head offset.");
        Stage(list, "Stage 12: Apparel / food restriction gates (#23/#67/#157)", ref settings.stage12ApparelFood,
            "Skips PawnCanWear / ApparelScoreGain / CanEverEat restriction work when a race restricts no apparel or food (chemicals included).");
        Stage(list, "Clear per-pawn caches on game init", ref settings.clearOnGameInit,
            "Drops the pawn->AlienComp cache whenever a new game session initializes, so loaded/saved pawns never go stale.");

        list.Gap(10f);
        list.Label("Diagnostics & Telemetry");
        list.Gap(4f);
        Stage(list, "Enable Benchmark Logging (writes Xenon_Perf.log)", ref settings.enableBenchmarkLogging,
            "When OFF (default): zero-overhead production mode - no Stopwatch.GetTimestamp in the hot path, no per-600-tick file writes.\n" +
            "When ON: logs a startup patch report + per-patch counters and memory each 600 ticks to <mod>/Xenon_Perf.log. Also forced on by benchmark_baseline.txt / benchmark_enable.txt markers.");

        string summary = PerfTelemetry.Enabled ? PerfTelemetry.LiveSummary() : "Zero-overhead production mode active.";
        Rect sumRect = list.GetRect(Text.LineHeight);
        Widgets.Label(sumRect, summary);
        TooltipHandler.TipRegion(sumRect, summary);

        Rect btnRect = list.GetRect(30f);
        if (Widgets.ButtonText(btnRect, "Rebuild Caches Now"))
            StartupInit.RebuildAllCaches();
        TooltipHandler.TipRegion(btnRect, "Re-derives all def-based caches immediately, without a full relaunch.");

        list.Gap(10f);
        Stage(list, "A/B Baseline mode (disable all optimizations)", ref settings.baselineAB,
            "For benchmarking: leaves HAR's ORIGINAL patches active so the log's regen-request count shows the un-optimized cost. Full restart required to apply.");
        list.Label("Changes apply on the next full game launch.");

        list.End();
        // Fix F8: removed per-frame WriteSettings() call; RimWorld automatically invokes Mod.WriteSettings() when the dialog closes.
    }

    private static void Stage(Listing_Standard list, string label, ref bool flag, string tooltip)
    {
        Rect r = list.GetRect(Text.LineHeight);
        Widgets.CheckboxLabeled(r, label, ref flag);
        TooltipHandler.TipRegion(r, tooltip);
    }
}
