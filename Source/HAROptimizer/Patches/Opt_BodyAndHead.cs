namespace HAROptimizer.Patches;

using AlienRace;
using HarmonyLib;
using HAROptimizer.Cache;
using Verse;

/// <summary>
/// Stage 9: fast-path prefix on <c>HarmonyPatches.ReplacedBody(Pawn)</c> (D3).
/// </summary>
public static class Opt_BodyAndHead
{
    public static void Patch(Harmony harmony)
    {
        BodyOverrideCache.EnsureBuilt();

        var target = AccessTools.Method(typeof(HarmonyPatches), nameof(HarmonyPatches.ReplacedBody));
        if (target == null)
        {
            Log.Warning($"{PatchHelper.Tag} Stage9 HarmonyPatches.ReplacedBody not found, skipped.");
            PerfTelemetry.RecordReplacement("Stage9 ReplacedBody: method not found, skipped");
            return;
        }

        harmony.Patch(target, prefix: new HarmonyMethod(typeof(Opt_BodyAndHead), nameof(ReplacedBodyPrefix)));
        PatchHelper.RegisterDirectPatch(target);
        PerfTelemetry.RecordReplacement("Stage9 ReplacedBody (D3): fast-path prefix installed (skip age lookup when no body override)");
    }

    public static bool ReplacedBodyPrefix(Pawn pawn, ref BodyDef __result)
    {
        if (!BodyOverrideCache.Built || pawn == null)
            return true;

        if (!PerfTelemetry.Enabled)
        {
            ThingDef def = pawn.def;
            if (def != null && !BodyOverrideCache.RaceOverridesBody(def))
            {
                __result = pawn.RaceProps.body;
                return false;
            }
            return true;
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.ReplacedBody);

        ThingDef pd = pawn.def;
        if (pd != null && !BodyOverrideCache.RaceOverridesBody(pd))
        {
            __result = pawn.RaceProps.body;
            PerfTelemetry.Fast(OptPatch.ReplacedBody, t0);
            return false;
        }

        PerfTelemetry.Executed(OptPatch.ReplacedBody, t0);
        return true;
    }
}
