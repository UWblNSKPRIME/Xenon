namespace HAROptimizer.Patches;

using System;
using AlienRace;
using HarmonyLib;
using HAROptimizer.Cache;
using Verse;

/// <summary>
/// Two cache-hygiene jobs hooked to <c>Game.FinalizeInit</c> (runs for new AND loaded games,
/// always after DefDatabase is fully populated):
///   - Drop per-pawn caches so nothing leaks across saves.
///   - Deterministic cache build fallback if <see cref="Opt_DefLoad"/> was bypassed.
/// </summary>
public static class Opt_GameInit
{
    private static Harmony held;

    public static void Patch(Harmony harmony)
    {
        held = harmony;
        var target = AccessTools.Method(typeof(Game), nameof(Game.FinalizeInit));
        if (target == null)
            return;
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(Opt_GameInit), nameof(Postfix)));
        PatchHelper.RegisterDirectPatch(target);
    }

    public static void Postfix()
    {
        if (HAROptimizerFlags.clearOnGameInit)
            PawnCompCache.Clear();

        try
        {
            // Fix F8: Only build def caches if they were not already built by Opt_DefLoad.Postfix.
            if (!RaceConditionCache.Built)   RaceConditionCache.Build();
            if (!HediffConditionCache.Built) HediffConditionCache.Build();
            if (!ThoughtCache.Built)         ThoughtCache.Build();
            if (!RestrictionCache.Built)     RestrictionCache.Build();
            if (!BodyOverrideCache.Built)    BodyOverrideCache.Build();
            if (!RenderOffsetCache.Built)    RenderOffsetCache.Build();

            if (HAROptimizerFlags.stage8Restrict && held != null)
                Opt_WorkRestrictions.Patch(held);

            Log.Message(
                $"{PatchHelper.Tag} Session ready, active Harmony patches: <color=#5BC0FF>{PatchHelper.ActivePatchCount}</color> " +
                $"({RaceConditionCache.SensitiveRaceCount()} need/mood-sensitive, " +
                $"{HediffConditionCache.SensitiveRaceCount()} hediff-sensitive races).");

            PerfTelemetry.TotalAlienRaces = DefDatabase<ThingDef_AlienRace>.AllDefsListForReading.Count;
            PerfTelemetry.MoodSensitive   = RaceConditionCache.MoodSensitiveCount();
            PerfTelemetry.NeedSensitive   = RaceConditionCache.NeedSensitiveCount();
            PerfTelemetry.HediffSensitive = HediffConditionCache.SensitiveRaceCount();
            PerfTelemetry.HediffRaceNames = HediffConditionCache.SensitiveRaceNames();
            PerfTelemetry.LogStartup();
        }
        catch (Exception e)
        {
            Log.Error($"{PatchHelper.Tag} GameInit cache check failed: {e}");
        }
    }
}
