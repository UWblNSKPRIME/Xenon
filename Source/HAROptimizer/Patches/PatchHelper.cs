namespace HAROptimizer.Patches;

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

/// <summary>
/// Transactional replacement helper and active-patch tracker for Xenon (HAROptimizer).
/// Invariants (F1 / F5):
///   1) Only replaces a patch when HAR's original patch method is actually active on the target
///      (prevents silent double-execution if HAR renames or moves a patch in a future update).
///   2) Applies our replacement BEFORE unpatching HAR's original, and rolls back to HAR's original
///      if patching or post-verification fails (prevents leaving the target completely unpatched).
///   3) Verifies via <see cref="Harmony.GetPatchInfo"/> that exactly one implementation is active.
/// </summary>
public static class PatchHelper
{
    public const string HarOwnerId = "rimworld.erdelf.alien_race.main";
    public const string XenonOwnerId = "wevsy.xenon";
    public const string Tag = "<color=#5BC0FF>[Xenon]</color>";

    private static readonly List<MethodBase> patchedTargets = new();
    private static readonly HashSet<MethodBase> patchedTargetSet = new();

    public static int ReplacedHarCount { get; private set; }
    public static int DirectPatchCount { get; private set; }
    public static int FailedPatchCount { get; private set; }
    public static int ConflictCount { get; internal set; }

    public static int ActivePatchCount => ReplacedHarCount + DirectPatchCount;
    public static int PatchedMethodCount => patchedTargets.Count;
    public static IReadOnlyList<MethodBase> PatchedTargets => patchedTargets;

    public static void ResetStats()
    {
        patchedTargets.Clear();
        patchedTargetSet.Clear();
        ReplacedHarCount = 0;
        DirectPatchCount = 0;
        FailedPatchCount = 0;
        ConflictCount = 0;
    }

    public static void RegisterDirectPatch(MethodBase target, int count = 1)
    {
        if (target == null)
            return;
        if (patchedTargetSet.Add(target))
            patchedTargets.Add(target);
        if (count > 0)
            DirectPatchCount += count;
    }

    private static void RecordTarget(MethodBase target)
    {
        if (target != null && patchedTargetSet.Add(target))
            patchedTargets.Add(target);
    }

    public static bool ReplacePatch(
        Harmony harmony,
        MethodBase target,
        MethodInfo harOriginal,
        MethodInfo ourPatch,
        bool isPrefix,
        string label)
    {
        if (harmony == null || target == null || harOriginal == null || ourPatch == null)
        {
            FailedPatchCount++;
            Log.Warning($"{Tag} {label}: missing target or patch method, skipped (fail-open).");
            PerfTelemetry.RecordReplacement($"{label}: missing target or patch method, skipped (fail-open)");
            return false;
        }

        bool alreadyOurs = HasPatch(Harmony.GetPatchInfo(target), ourPatch, isPrefix);
        Patches before = Harmony.GetPatchInfo(target);
        if (!alreadyOurs && !HasPatch(before, harOriginal, isPrefix))
        {
            FailedPatchCount++;
            Log.Warning($"{Tag} {label}: original HAR patch '{harOriginal.Name}' not active on '{target.DeclaringType?.Name}.{target.Name}', replacement skipped to prevent double execution.");
            PerfTelemetry.RecordReplacement($"{label}: HAR original not active on target, skipped");
            return false;
        }

        try
        {
            if (!alreadyOurs)
            {
                var hm = new HarmonyMethod(ourPatch);
                if (isPrefix)
                    harmony.Patch(target, prefix: hm);
                else
                    harmony.Patch(target, postfix: hm);
            }

            harmony.Unpatch(target, harOriginal);

            Patches after = Harmony.GetPatchInfo(target);
            bool oursActive = HasPatch(after, ourPatch, isPrefix);
            bool harStillActive = HasPatch(after, harOriginal, isPrefix);

            if (!oursActive || harStillActive)
            {
                Rollback(harmony, target, harOriginal, ourPatch, isPrefix);
                FailedPatchCount++;
                Log.Warning($"{Tag} {label}: post-check failed (ours={oursActive}, har={harStillActive}), rolled back to HAR original.");
                PerfTelemetry.RecordReplacement($"{label}: post-check failed, rolled back to HAR original");
                return false;
            }

            RecordTarget(target);
            if (!alreadyOurs)
                ReplacedHarCount++;

            PerfTelemetry.RecordReplacement($"{label}: HAR unpatched + replaced (verified 1 active)");
            return true;
        }
        catch (Exception ex)
        {
            Rollback(harmony, target, harOriginal, ourPatch, isPrefix);
            FailedPatchCount++;
            Log.Warning($"{Tag} {label}: patch failed, restored HAR original: {ex.Message}");
            PerfTelemetry.RecordReplacement($"{label}: failed and rolled back to HAR original");
            return false;
        }
    }

    public static void Rollback(Harmony harmony, MethodBase target, MethodInfo harOriginal, MethodInfo ourPatch, bool isPrefix)
    {
        if (harmony == null || target == null)
            return;

        try
        {
            if (ourPatch != null)
                harmony.Unpatch(target, ourPatch);

            if (harOriginal != null)
            {
                Patches current = Harmony.GetPatchInfo(target);
                if (!HasPatch(current, harOriginal, isPrefix))
                {
                    var harHm = new HarmonyMethod(harOriginal);
                    if (isPrefix)
                        harmony.Patch(target, prefix: harHm);
                    else
                        harmony.Patch(target, postfix: harHm);
                }
            }
        }
        catch (Exception rollbackEx)
        {
            Log.Error($"{Tag} rollback failed on '{target.DeclaringType?.Name}.{target.Name}': {rollbackEx}");
        }
    }

    public static bool HasPatch(Patches info, MethodInfo method, bool isPrefix)
    {
        if (info == null || method == null)
            return false;
        var list = isPrefix ? info.Prefixes : info.Postfixes;
        if (list == null)
            return false;
        for (int i = 0; i < list.Count; i++)
            if (list[i].PatchMethod == method)
                return true;
        return false;
    }
}
