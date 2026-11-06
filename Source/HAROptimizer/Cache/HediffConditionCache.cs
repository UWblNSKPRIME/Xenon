namespace HAROptimizer.Cache;

using System.Collections.Generic;
using AlienRace;
using AlienRace.ExtendedGraphics;
using RimWorld;
using Verse;

/// <summary>
/// Precomputes, per alien race, which <see cref="HediffDef"/>s its addons actually reference via
/// <see cref="ConditionHediff"/>, and whether any <see cref="ConditionHediffSeverity"/> exists.
/// Used to gate HAR's AddHediff / RemoveHediff / Notify_HediffChanged postfixes so a scar, flu or
/// pain tick on a hediff no addon cares about stops triggering a full addon regeneration.
///
/// Same rule as <see cref="RaceConditionCache"/>: never reads the lazily-filled <c>conditionTypes</c>;
/// walks <c>conditions</c> + <c>extendedGraphics</c> recursively and unwraps logic operators.
/// </summary>
public static class HediffConditionCache
{
    private static readonly HashSet<ThingDef> sensitiveRaces = new();
    private static readonly Dictionary<ThingDef, HashSet<HediffDef>> raceHediffDefs = new();
    private static readonly Dictionary<ThingDef, bool> raceHasSeverity = new();

    public static bool Built { get; private set; }

    // See RaceConditionCache.EnsureBuilt: lazy, ordering-safe build on first real use.
    public static void EnsureBuilt()
    {
        if (!Built && DefDatabase<ThingDef_AlienRace>.AllDefsListForReading.Count > 0)
            Build();
    }

    public static void Build()
    {
        Built = false;
        sensitiveRaces.Clear();
        raceHediffDefs.Clear();
        raceHasSeverity.Clear();

        var universal = CollectUniversalAddons();
        foreach (ThingDef_AlienRace race in DefDatabase<ThingDef_AlienRace>.AllDefs)
            ScanRace(race, race.alienRace.generalSettings.alienPartGenerator.bodyAddons, universal);

        Built = true;
    }

    private static List<AlienPartGenerator.BodyAddon> CollectUniversalAddons()
    {
        var list = new List<AlienPartGenerator.BodyAddon>();
        foreach (RaceSettings rs in DefDatabase<RaceSettings>.AllDefsListForReading)
            if (rs.universalBodyAddons != null)
                list.AddRange(rs.universalBodyAddons);
        return list;
    }

    public static void ScanRace(ThingDef race, List<AlienPartGenerator.BodyAddon> own,
                                List<AlienPartGenerator.BodyAddon> universal)
    {
        var  defs        = new HashSet<HediffDef>();
        bool hasSeverity = false;

        if (own != null)
            for (int i = 0; i < own.Count; i++)
                ScanAddon(own[i], defs, ref hasSeverity);
        if (universal != null)
            for (int i = 0; i < universal.Count; i++)
                ScanAddon(universal[i], defs, ref hasSeverity);

        // Fix F1: a race may have ConditionHediffSeverity (which reads data.hediff or null from
        // ResolveData) even if defs.Count == 0, so record the race whenever defs.Count > 0 OR hasSeverity.
        if (defs.Count == 0 && !hasSeverity)
            return;

        sensitiveRaces.Add(race);
        if (defs.Count > 0)
            raceHediffDefs[race] = defs;
        raceHasSeverity[race] = hasSeverity;
    }

    private static void ScanAddon(AlienPartGenerator.BodyAddon addon, HashSet<HediffDef> defs, ref bool hasSeverity)
    {
        if (addon == null) return;
        ScanConditions(addon.conditions, defs, ref hasSeverity);
        ScanGraphics(addon.extendedGraphics, defs, ref hasSeverity);
    }

    private static void ScanConditions(List<Condition> conditions, HashSet<HediffDef> defs, ref bool hasSeverity)
    {
        if (conditions == null) return;
        for (int i = 0; i < conditions.Count; i++)
            ScanCondition(conditions[i], defs, ref hasSeverity);
    }

    private static void ScanCondition(Condition c, HashSet<HediffDef> defs, ref bool hasSeverity)
    {
        switch (c)
        {
            case null:
                return;
            case ConditionHediff ch:
                if (ch.hediff != null)
                    defs.Add(ch.hediff);
                break;
            case ConditionHediffSeverity:
                hasSeverity = true;
                break;
            case ConditionLogicCollection clc:
                ScanConditions(clc.conditions, defs, ref hasSeverity);
                break;
            case ConditionLogicSingle cls:
                ScanCondition(cls.condition, defs, ref hasSeverity);
                break;
        }
    }

    private static void ScanGraphics(List<AbstractExtendedGraphic> graphics, HashSet<HediffDef> defs, ref bool hasSeverity)
    {
        if (graphics == null) return;
        for (int i = 0; i < graphics.Count; i++)
        {
            AbstractExtendedGraphic g = graphics[i];
            if (g is null) continue;
            if (g is AlienPartGenerator.ExtendedConditionGraphic cg)
                ScanConditions(cg.conditions, defs, ref hasSeverity);
            ScanGraphics(g.extendedGraphics, defs, ref hasSeverity);
        }
    }

    // ---- query surface ----

    // Fail-open when !Built (returns true so the caller falls through to HAR's regen).
    public static bool IsHediffRelevant(ThingDef race, HediffDef hediff)
    {
        if (race is not ThingDef_AlienRace || hediff == null)
            return false;
        if (!Built || RaceConditionCache.HasCustomGraphicsForRace(race))
            return true;
        return raceHediffDefs.TryGetValue(race, out var set) && set.Contains(hediff);
    }

    public static bool IsSeverityRelevant(ThingDef race, HediffDef hediff)
    {
        if (race is not ThingDef_AlienRace)
            return false;
        if (!Built || RaceConditionCache.HasCustomGraphicsForRace(race))
            return true;
        if (!raceHasSeverity.TryGetValue(race, out var s) || !s)
            return false;
        if (!raceHediffDefs.TryGetValue(race, out var set) || set.Count == 0)
            return true;
        return hediff == null || set.Contains(hediff);
    }

    public static int SensitiveRaceCount() => sensitiveRaces.Count;

    public static string SensitiveRaceNames()
    {
        var names = new List<string>();
        foreach (ThingDef r in sensitiveRaces) names.Add(r.defName);
        names.Sort();
        return names.Count > 0 ? string.Join(", ", names) : "none";
    }

    public static void Invalidate() => Built = false;
}
