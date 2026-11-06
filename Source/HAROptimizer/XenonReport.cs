namespace HAROptimizer;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using AlienRace;
using AlienRace.ExtendedGraphics;
using HarmonyLib;
using HAROptimizer.Cache;
using HAROptimizer.Patches;
using Verse;

/// <summary>
/// Produces structured, color-coded startup and def-load diagnostics in the RimWorld console
/// under the <c>&lt;color=#5BC0FF&gt;[Xenon]&lt;/color&gt;</c> element tag, matching the visual
/// format of <c>[MeleeAnim]</c> using <see cref="Log.Message(string)"/> with rich-text colors so
/// RimWorld does not flag informational lines as warnings/errors.
/// </summary>
public static class XenonReport
{
    public const string ElementName = "Xenon";
    public const string ElementSymbol = "Xe";
    public const int AtomicNumber = 54;
    public const string ColorHex = "#5BC0FF";
    public const string WarnColorHex = "#F2D45C";
    public const string Prefix = "<color=#5BC0FF>[Xenon]</color>";

    public static void LogDefLoadSummary(Harmony harmony, double preCacheMs)
    {
        try
        {
            var allMethods = CollectPatchedMethods(harmony);
            var fullBlocks = new List<string>(allMethods.Count);
            var conflictBlocks = new List<string>();

            for (int i = 0; i < allMethods.Count; i++)
            {
                MethodBase method = allMethods[i];
                if (method == null)
                    continue;

                HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
                string formattedBlock = FormatMethodPatchBlock(method, info, out bool hasOtherModOwners);
                fullBlocks.Add(formattedBlock);

                if (hasOtherModOwners)
                    conflictBlocks.Add(formattedBlock);
            }

            PatchHelper.ConflictCount = conflictBlocks.Count;

            string modeTag = HAROptimizerFlags.baseline
                ? " <color=#FF7B7B>[A/B BASELINE MODE, OPTIMIZATIONS DISABLED]</color>"
                : "";

            // 1. Active patch count headline
            Log.Message(
                $"{Prefix} Active Harmony patches: <color={ColorHex}>{PatchHelper.ActivePatchCount}</color> " +
                $"across <color={ColorHex}>{allMethods.Count}</color> methods " +
                $"({PatchHelper.ReplacedHarCount} HAR patches replaced, {PatchHelper.DirectPatchCount} fast-path hooks" +
                $"{(PatchHelper.FailedPatchCount > 0 ? $", {PatchHelper.FailedPatchCount} skipped/rolled back" : "")}).{modeTag}");

            // 2. Shared target methods (colored yellow via rich text in Log.Message so it is NOT treated as an error/warning)
            Log.Message($"{Prefix} <color={WarnColorHex}>Shared Harmony targets ({conflictBlocks.Count}):</color>");
            if (conflictBlocks.Count > 0)
            {
                Log.Message($"{Prefix} \n<color={WarnColorHex}>{string.Join("\n\n", conflictBlocks)}</color>");
            }

            // 3. Full patch list
            Log.Message($"{Prefix} Full patch list ({fullBlocks.Count}):");
            if (fullBlocks.Count > 0)
            {
                Log.Message($"{Prefix} \n{string.Join("\n\n", fullBlocks)}");
            }

            // 4. PreCache timing line
            Log.Message($"{Prefix} PreCached all alien race optimization data in {preCacheMs:F1}ms");

            // 5. Per-race optimization profile summary
            LogAlienRaceProfiles();
        }
        catch (Exception ex)
        {
            Log.Warning($"{Prefix} Could not complete diagnostic summary: {ex.Message}");
        }
    }

    private static string FormatMethodPatchBlock(MethodBase method, HarmonyLib.Patches info, out bool hasOtherModOwners)
    {
        hasOtherModOwners = false;
        var sb = new StringBuilder(256);
        sb.Append(SafeFullDescription(method));

        if (info == null)
            return sb.ToString();

        sb.Append("\nPrefixes:");
        AppendPatchList(sb, info.Prefixes, ref hasOtherModOwners);

        sb.Append("\nTranspilers:");
        AppendPatchList(sb, info.Transpilers, ref hasOtherModOwners);

        sb.Append("\nPostfixes:");
        AppendPatchList(sb, info.Postfixes, ref hasOtherModOwners);

        return sb.ToString();
    }

    private static void AppendPatchList(
        StringBuilder sb,
        System.Collections.ObjectModel.ReadOnlyCollection<Patch> patches,
        ref bool hasOtherModOwners)
    {
        if (patches == null || patches.Count == 0)
            return;

        for (int i = 0; i < patches.Count; i++)
        {
            Patch p = patches[i];
            if (p == null)
                continue;
            string owner = p.owner ?? "unknown";
            if (!string.Equals(owner, PatchHelper.XenonOwnerId, StringComparison.OrdinalIgnoreCase))
                hasOtherModOwners = true;

            sb.Append("\n  [").Append(owner).Append("] ").Append(p.PatchMethod?.Name ?? "Patch");
        }
    }

    private static void LogAlienRaceProfiles()
    {
        List<ThingDef_AlienRace> races = DefDatabase<ThingDef_AlienRace>.AllDefsListForReading;
        int raceCount = races?.Count ?? 0;

        Log.Message(
            $"{Prefix} Loaded optimization profiles for {raceCount} alien races " +
            $"({RaceConditionCache.SensitiveRaceCount()} need/mood-sensitive, " +
            $"{HediffConditionCache.SensitiveRaceCount()} hediff-sensitive).");

        if (races == null || races.Count == 0)
            return;

        for (int i = 0; i < races.Count; i++)
        {
            ThingDef_AlienRace race = races[i];
            if (race == null)
                continue;

            string pkgId = race.modContentPack?.PackageIdPlayerFacing
                           ?? race.modContentPack?.PackageId
                           ?? "local.mod";
            string modName = race.modContentPack?.Name
                             ?? (!string.IsNullOrEmpty(race.label) ? race.label : race.defName);

            CountAddonsAndConditions(race, out int addonCount, out int conditionCount);
            int fastPathGates = CountFastPathGatesForRace(race, out int totalGates);

            Log.Message(
                $"{Prefix} <color={WarnColorHex}>{pkgId} '{modName}' ({race.defName}) has " +
                $"{addonCount} body addons, {conditionCount} condition triggers " +
                $"({fastPathGates}/{totalGates} O(1) fast-path bypasses active).</color>");
        }
    }

    private static int CountFastPathGatesForRace(ThingDef_AlienRace race, out int totalGates)
    {
        totalGates = 8;
        int fast = 0;
        if (!RaceConditionCache.IsRaceSensitive(race)) fast++;
        if (!HediffConditionCache.IsSeverityRelevant(race, null)) fast++;
        if (!BodyOverrideCache.RaceOverridesBody(race)) fast++;
        if (!RenderOffsetCache.RaceMayShiftHead2D(race)) fast++;
        if (!RenderOffsetCache.RaceMayShiftHeadDirectional(race)) fast++;
        if (!ThoughtCache.HasReplacers(race)) fast++;
        if (RestrictionCache.ApparelTriviallyAllowed(race)) fast++;
        if (RestrictionCache.FoodTriviallyAllowed) fast++;
        return fast;
    }

    private static void CountAddonsAndConditions(ThingDef_AlienRace race, out int addonCount, out int conditionCount)
    {
        addonCount = 0;
        conditionCount = 0;
        var addons = race?.alienRace?.generalSettings?.alienPartGenerator?.bodyAddons;
        if (addons == null)
            return;

        addonCount = addons.Count;
        var stack = new Stack<AbstractExtendedGraphic>();
        for (int i = 0; i < addons.Count; i++)
        {
            var addon = addons[i];
            if (addon == null)
                continue;
            if (addon.conditions != null)
                conditionCount += addon.conditions.Count;
            if (addon.extendedGraphics != null)
            {
                for (int j = 0; j < addon.extendedGraphics.Count; j++)
                    if (addon.extendedGraphics[j] != null)
                        stack.Push(addon.extendedGraphics[j]);
            }
        }

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is AlienPartGenerator.ExtendedConditionGraphic ecg && ecg.conditions != null)
                conditionCount += ecg.conditions.Count;
            if (node.extendedGraphics != null)
            {
                for (int j = 0; j < node.extendedGraphics.Count; j++)
                    if (node.extendedGraphics[j] != null)
                        stack.Push(node.extendedGraphics[j]);
            }
        }
    }

    private static List<MethodBase> CollectPatchedMethods(Harmony harmony)
    {
        var result = new List<MethodBase>();
        var seen = new HashSet<MethodBase>();

        foreach (MethodBase m in PatchHelper.PatchedTargets)
        {
            if (m != null && seen.Add(m))
                result.Add(m);
        }

        if (harmony != null)
        {
            foreach (MethodBase m in harmony.GetPatchedMethods())
            {
                if (m != null && seen.Add(m))
                    result.Add(m);
            }
        }

        return result;
    }

    private static string SafeFullDescription(MethodBase method)
    {
        try
        {
            return method.FullDescription();
        }
        catch
        {
            return $"{method.DeclaringType?.FullName ?? "<unknown>"}::{method.Name}";
        }
    }
}
