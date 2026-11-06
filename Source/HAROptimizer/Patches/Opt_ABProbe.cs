namespace HAROptimizer.Patches;

using System;
using System.Collections.Generic;
using AlienRace;
using HarmonyLib;
using Verse;

/// <summary>
/// Measures only the instance overload: static callers forward to it, so they are not counted twice.
/// Measures synchronous requests including early returns and surrounding patches, not deferred rendering.
/// Installed only in benchmark mode. Thread allocation samples do not include worker allocations.
/// </summary>
public static class Opt_ABProbe
{
    public static void Patch(Harmony harmony)
    {
        var t = typeof(AlienPartGenerator.AlienComp);
        int installed = 0;

        var instanceTarget = AccessTools.Method(t,
            nameof(AlienPartGenerator.AlienComp.RegenerateAddonGraphicsWithCondition),
            new[] { typeof(HashSet<Type>) });
        if (instanceTarget != null)
        {
            harmony.Patch(instanceTarget,
                prefix:  new HarmonyMethod(typeof(Opt_ABProbe), nameof(PrefixInstance)),
                postfix: new HarmonyMethod(typeof(Opt_ABProbe), nameof(PostfixInstance)));
            installed++;
        }

        if (installed == 0)
            Log.Warning("<color=#5BC0FF>[Xenon]</color> no regen overload found, A/B probe skipped.");
        else
            PerfTelemetry.RecordReplacement($"A/B probe on AlienComp.RegenerateAddonGraphicsWithCondition: {installed} overload(s) installed");
    }

    public struct Sample
    {
        public long Timestamp;
        public long AllocatedBytes;
    }

    public static void PrefixInstance(out Sample __state)
    {
        __state = default;
        if (!PerfTelemetry.Enabled) return;
        __state.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        __state.Timestamp = PerfTelemetry.RegenEnter();
    }

    public static void PostfixInstance(Sample __state, bool __runOriginal)
    {
        if (!PerfTelemetry.Enabled || !__runOriginal || __state.Timestamp == 0) return;
        PerfTelemetry.RegenExit(__state.Timestamp, GC.GetAllocatedBytesForCurrentThread() - __state.AllocatedBytes);
    }
}
