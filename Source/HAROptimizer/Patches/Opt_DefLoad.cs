namespace HAROptimizer.Patches;

using System;
using System.Diagnostics;
using HarmonyLib;
using HAROptimizer.Cache;
using Verse;

/// <summary>
/// Builds all def-based caches (<see cref="RaceConditionCache"/>, <see cref="HediffConditionCache"/>,
/// <see cref="ThoughtCache"/>, <see cref="RestrictionCache"/>, <see cref="BodyOverrideCache"/>,
/// <see cref="RenderOffsetCache"/>), installs Stage 8 dynamic WorkGiver patches, and emits the
/// full <see cref="XenonReport"/> diagnostic block right after all <c>[StaticConstructorOnStartup]</c>
/// mods have finished patching.
/// </summary>
public static class Opt_DefLoad
{
    private static Harmony held;
    private static bool initialRunCompleted;

    public static void Patch(Harmony harmony)
    {
        held = harmony;

        // StartupInit runs inside StaticConstructorOnStartupUtility.CallAll(), which is invoked at
        // the very end of PlayDataLoader.DoPlayLoad(). Queueing via LongEventHandler makes our
        // Postfix runs on the main thread right after all other [StaticConstructorOnStartup] mods
        // (such as MeleeAnim, VEF, FacialAnimation) have finished applying their Harmony patches.
        LongEventHandler.QueueLongEvent(
            Postfix,
            textKey: null,
            doAsynchronously: false,
            exceptionHandler: null);

        // Also hook PlayDataLoader.DoPlayLoad in case defs are hot-reloaded later in the session.
        var target = AccessTools.Method("Verse.PlayDataLoader:DoPlayLoad");
        if (target != null)
        {
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(Opt_DefLoad), nameof(OnReloadPostfix)));
            PatchHelper.RegisterDirectPatch(target);
        }
    }

    public static void OnReloadPostfix()
    {
        if (!initialRunCompleted)
            return;
        Postfix();
    }

    public static void Postfix()
    {
        try
        {
            var sw = Stopwatch.StartNew();

            RaceConditionCache.Build();
            HediffConditionCache.Build();
            ThoughtCache.Build();
            RestrictionCache.Build();
            BodyOverrideCache.Build();
            RenderOffsetCache.Build();

            if (HAROptimizerFlags.stage8Restrict && held != null)
                Opt_WorkRestrictions.Patch(held);

            sw.Stop();
            initialRunCompleted = true;
            XenonReport.LogDefLoadSummary(held, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception e)
        {
            Log.Error($"{PatchHelper.Tag} cache build failed: {e}");
        }
    }
}
