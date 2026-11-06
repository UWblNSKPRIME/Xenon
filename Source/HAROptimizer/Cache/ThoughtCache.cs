namespace HAROptimizer.Cache;

using System.Collections.Generic;
using AlienRace;
using RimWorld;
using Verse;

/// <summary>
/// Stage 7: precomputed, race-keyed facts about HAR's thought system so the hot-path thought
/// patches (<c>CanGetThought</c> #24 and the <c>ThoughtReplacement</c> / <c>TryGainMemory</c> /
/// <c>TryCreateThought</c> prefixes #25-32) can instantly no-op when provably unused, or check
/// direct pointer fields without HashSet lookups.
/// </summary>
public static class ThoughtCache
{
    private static readonly HashSet<ThingDef> racesWithReplacers = new();
    private static readonly HashSet<ThingDef> racesWithThoughtCustomization = new();
    private static readonly HashSet<ThoughtDef> restrictedThoughtDefs = new();

    public static bool Built { get; private set; }
    public static bool AnyThoughtRestrictionOrCustomization { get; private set; }
    public static bool AnyReplacers { get; private set; }

    public static void EnsureBuilt()
    {
        if (!Built && DefDatabase<ThingDef_AlienRace>.AllDefsListForReading.Count > 0)
            Build();
    }

    public static void Build()
    {
        Built = false;
        AnyThoughtRestrictionOrCustomization = false;
        AnyReplacers = false;
        racesWithReplacers.Clear();
        racesWithThoughtCustomization.Clear();
        restrictedThoughtDefs.Clear();

        foreach (ThingDef_AlienRace race in DefDatabase<ThingDef_AlienRace>.AllDefs)
        {
            ThoughtSettings ts = race.alienRace?.thoughtSettings;
            if (ts == null) continue;

            if (ts.replacerList != null && ts.replacerList.Count > 0)
                racesWithReplacers.Add(race);

            bool customized = ts.cannotReceiveThoughtsAtAll
                              || (ts.cannotReceiveThoughts   != null && ts.cannotReceiveThoughts.Count   > 0)
                              || (ts.canStillReceiveThoughts != null && ts.canStillReceiveThoughts.Count > 0)
                              || (ts.replacerList            != null && ts.replacerList.Count            > 0);
            if (customized)
                racesWithThoughtCustomization.Add(race);

            if (ts.restrictedThoughts != null)
                for (int i = 0; i < ts.restrictedThoughts.Count; i++)
                    if (ts.restrictedThoughts[i] != null)
                        restrictedThoughtDefs.Add(ts.restrictedThoughts[i]);
        }

        AnyReplacers = racesWithReplacers.Count > 0;
        AnyThoughtRestrictionOrCustomization = racesWithThoughtCustomization.Count > 0 || restrictedThoughtDefs.Count > 0;
        Built = true;
    }

    // ---- public query surface for the hot-path patches ----

    /// <summary>
    /// Fix F5: Direct O(1) field check on <c>race.alienRace.thoughtSettings.replacerList</c> instead of
    /// hashing <see cref="ThingDef"/> into a <see cref="HashSet{T}"/>. Fail-open when !Built.
    /// </summary>
    public static bool HasReplacers(ThingDef def)
    {
        if (def is not ThingDef_AlienRace race) return false;
        if (!Built) return true; // fail-open
        if (!AnyReplacers) return false;
        var list = race.alienRace?.thoughtSettings?.replacerList;
        return list != null && list.Count > 0;
    }

    /// <summary>True when this race's thoughtSettings can influence CanGetThought (#24). Fail-open when !Built.</summary>
    public static bool HasThoughtCustomization(ThingDef def)
    {
        if (def is not ThingDef_AlienRace) return false;
        if (!Built) return true; // fail-open
        return racesWithThoughtCustomization.Contains(def);
    }

    /// <summary>True when some race globally restricted this thought (blocks everyone else, #24). Fail-open when !Built.</summary>
    public static bool IsThoughtRestricted(ThoughtDef def)
    {
        if (def == null) return false;
        if (!Built) return true; // fail-open
        return restrictedThoughtDefs.Contains(def);
    }

    public static int ReplacerRaceCount() => racesWithReplacers.Count;
    public static int CustomizedRaceCount() => racesWithThoughtCustomization.Count;
    public static int RestrictedThoughtCount() => restrictedThoughtDefs.Count;

    public static void Invalidate()
    {
        Built = false;
        AnyThoughtRestrictionOrCustomization = false;
        AnyReplacers = false;
    }
}
