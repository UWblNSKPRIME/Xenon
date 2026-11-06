namespace HAROptimizer.Cache;

using System;
using System.Collections.Generic;
using AlienRace;
using AlienRace.ExtendedGraphics;
using RimWorld;
using Verse;

/// <summary>
/// Precomputes, per alien race, whether it actually owns any addon gated by
/// <see cref="ConditionMood"/> / <see cref="ConditionNeed"/>, and collects the concrete
/// <see cref="ConditionNeed"/> instances so a hot-path patch can react only on real flips.
///
/// IMPORTANT (bug #6): HAR only fills <c>BodyAddon.conditionTypes</c> lazily inside
/// <c>DefaultGraphicsLoader.LoadAllGraphics</c>, which is invoked from <c>LoadGraphicsHook</c>
/// on <c>Application.onBeforeRender</c>, long after StaticConstructorOnStartup. So this
/// cache NEVER reads <c>conditionTypes</c>; it walks the XML-deserialized <c>conditions</c>
/// lists and the <c>extendedGraphics</c> tree directly (those are populated during def load).
/// </summary>
public static class RaceConditionCache
{
    private static readonly HashSet<ThingDef> sensitiveRaces = new();
    private static readonly Dictionary<ThingDef, bool> raceHasMood = new();
    private static readonly Dictionary<ThingDef, List<ConditionNeed>> raceNeedConditions = new();
    private static readonly Dictionary<ThingDef, HashSet<NeedDef>> raceRelevantNeeds = new();
    // Stage 6: the FULL set of leaf Condition.GetType() values a race owns across its own addons +
    // the universal addons. This matches what HAR lazily fills into each addon's conditionTypes.
    // A regen request whose condition types don't intersect this set can never change a graphic.
    private static readonly Dictionary<ThingDef, HashSet<Type>> raceConditionTypes = new();
    private static readonly Dictionary<ThingDef, NeedConditionIndex> needIndices = new();
    private static readonly HashSet<ThingDef> customGraphics = new();

    public static bool Built { get; private set; }

    public static void EnsureBuilt()
    {
        if (!Built && DefDatabase<ThingDef_AlienRace>.AllDefsListForReading.Count > 0)
            Build();
    }

    public static void Build()
    {
        Built = false;
        needIndices.Clear();
        customGraphics.Clear();
        sensitiveRaces.Clear();
        raceHasMood.Clear();
        raceNeedConditions.Clear();
        raceRelevantNeeds.Clear();
        raceConditionTypes.Clear();

        // Universal addons apply to every race, so collect them once and feed into every scan.
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

    public static void ScanRace(ThingDef race, List<AlienPartGenerator.BodyAddon> ownAddons,
                                List<AlienPartGenerator.BodyAddon> universalAddons)
    {
        bool hasMood = false;
        var  needs   = new List<ConditionNeed>();
        var  relevant = new HashSet<NeedDef>();
        var  types    = new HashSet<Type>();

        if (ownAddons != null)
            for (int i = 0; i < ownAddons.Count; i++)
            {
                ScanAddon(ownAddons[i], ref hasMood, needs, relevant);
                CollectAddonTypes(ownAddons[i], types);
            }

        if (universalAddons != null)
            for (int i = 0; i < universalAddons.Count; i++)
            {
                ScanAddon(universalAddons[i], ref hasMood, needs, relevant);
                CollectAddonTypes(universalAddons[i], types);
            }

        if (hasMood)
            relevant.Add(AlienDefOf.Mood);

        // Stage 6 gate data: recorded for EVERY race (even non-mood/need), keyed by ThingDef.
        raceConditionTypes[race] = types;
        needIndices[race] = new NeedConditionIndex(needs);
        foreach (Type type in types)
            if (type.Assembly != typeof(Condition).Assembly) customGraphics.Add(race);
        if (HasCustomGraphics(ownAddons) || HasCustomGraphics(universalAddons)) customGraphics.Add(race);

        // Nothing to react to -> this race is not sensitive -> the postfix can early-out.
        if (!hasMood && needs.Count == 0)
            return;

        sensitiveRaces.Add(race);
        raceHasMood[race]  = hasMood;
        raceNeedConditions[race] = needs;
        raceRelevantNeeds[race]  = relevant;
    }

    private static void ScanAddon(AlienPartGenerator.BodyAddon addon, ref bool hasMood,
                                  List<ConditionNeed> needs, HashSet<NeedDef> relevant)
    {
        if (addon == null) return;
        ScanConditions(addon.conditions, ref hasMood, needs, relevant);
        ScanGraphics(addon.extendedGraphics, ref hasMood, needs, relevant);
    }

    private static void ScanConditions(List<Condition> conditions, ref bool hasMood,
                                       List<ConditionNeed> needs, HashSet<NeedDef> relevant)
    {
        if (conditions == null) return;
        for (int i = 0; i < conditions.Count; i++)
            ScanCondition(conditions[i], ref hasMood, needs, relevant);
    }

    private static void ScanCondition(Condition c, ref bool hasMood,
                                      List<ConditionNeed> needs, HashSet<NeedDef> relevant)
    {
        switch (c)
        {
            case null:
                return;
            case ConditionMood:
                hasMood = true;              // do NOT return; keep scanning siblings via caller loop
                break;
            case ConditionNeed cn:
                needs.Add(cn);
                if (cn.need != null)
                    relevant.Add(cn.need);
                break;
            case ConditionLogicCollection clc:
                ScanConditions(clc.conditions, ref hasMood, needs, relevant);
                break;
            case ConditionLogicSingle cls:
                if (cls.condition != null)
                    ScanCondition(cls.condition, ref hasMood, needs, relevant);
                break;
        }
    }

    private static void ScanGraphics(List<AbstractExtendedGraphic> graphics, ref bool hasMood,
                                     List<ConditionNeed> needs, HashSet<NeedDef> relevant)
    {
        if (graphics == null) return;
        for (int i = 0; i < graphics.Count; i++)
        {
            AbstractExtendedGraphic g = graphics[i];
            if (g is null) continue;
            if (g is AlienPartGenerator.ExtendedConditionGraphic cg)
                ScanConditions(cg.conditions, ref hasMood, needs, relevant);
            ScanGraphics(g.extendedGraphics, ref hasMood, needs, relevant);
        }
    }

    private static void CollectAddonTypes(AlienPartGenerator.BodyAddon addon, HashSet<Type> into)
    {
        if (addon == null) return;
        CollectConditionTypes(addon.conditions, into);
        CollectGraphicTypes(addon.extendedGraphics, into);
    }

    private static void CollectConditionTypes(List<Condition> conditions, HashSet<Type> into)
    {
        if (conditions == null) return;
        for (int i = 0; i < conditions.Count; i++)
        {
            Condition c = conditions[i];
            switch (c)
            {
                case null:
                    continue;
                case ConditionLogicCollection clc:
                    CollectConditionTypes(clc.conditions, into);
                    break;
                case ConditionLogicSingle cls:
                    if (cls.condition != null)
                        CollectSingleConditionType(cls.condition, into);
                    break;
                default:
                    into.Add(c.GetType());
                    break;
            }
        }
    }

    private static void CollectGraphicTypes(List<AbstractExtendedGraphic> graphics, HashSet<Type> into)
    {
        if (graphics == null) return;
        for (int i = 0; i < graphics.Count; i++)
        {
            AbstractExtendedGraphic g = graphics[i];
            if (g is null) continue;
            if (g is AlienPartGenerator.ExtendedConditionGraphic cg)
                CollectConditionTypes(cg.conditions, into);
            CollectGraphicTypes(g.extendedGraphics, into);
        }
    }

    private static void CollectSingleConditionType(Condition condition, HashSet<Type> into)
    {
        if (condition is ConditionLogicCollection collection) CollectConditionTypes(collection.conditions, into);
        else if (condition is ConditionLogicSingle single)
        {
            if (single.condition != null) CollectSingleConditionType(single.condition, into);
        }
        else into.Add(condition.GetType());
    }

    // ---- public query surface for the hot-path patches ----

    public static bool IsRaceSensitive(ThingDef def) =>
        Built && def != null && sensitiveRaces.Contains(def);

    /// <summary>
    /// Stage 6 gate: does this race own at least one addon gated on any of <paramref name="requested"/>
    /// condition types? Returns true (conservatively allow) when the race is unknown or cache is not built.
    /// Fix F7: if the race IS known and has 0 conditions (<c>owned.Count == 0</c>) and no custom graphics,
    /// returns <c>false</c> (provably no condition overlap).
    /// </summary>
    public static bool RaceOwnsAnyType(ThingDef def, HashSet<Type> requested)
    {
        if (requested == null || requested.Count == 0)
            return true;
        if (!Built || def == null || !raceConditionTypes.TryGetValue(def, out var owned))
            return true; // cache not ready or unknown race -> don't gate (safe default)
        if (customGraphics.Contains(def))
            return true;
        if (owned.Count == 0)
            return false; // known race with 0 addon conditions -> provably no overlap
        return SetOverlap.Any(owned, requested);
    }

    public static bool HasCustomGraphicsForRace(ThingDef race) =>
        race != null && customGraphics.Contains(race);

    private static bool HasCustomGraphics<T>(List<T> graphics) where T : AbstractExtendedGraphic
    {
        if (graphics == null) return false;
        for (int i = 0; i < graphics.Count; i++)
        {
            var graphic = graphics[i];
            if (graphic == null) continue;
            if (graphic.GetType().Assembly != typeof(Condition).Assembly || HasCustomGraphics(graphic.extendedGraphics)) return true;
            if (graphic is AlienPartGenerator.BodyAddon addon && HasCustomConditions(addon.conditions)) return true;
            if (graphic is AlienPartGenerator.ExtendedConditionGraphic conditional && HasCustomConditions(conditional.conditions)) return true;
        }
        return false;
    }

    private static bool HasCustomConditions(List<Condition> conditions)
    {
        if (conditions == null) return false;
        for (int i = 0; i < conditions.Count; i++)
            if (HasCustomCondition(conditions[i])) return true;
        return false;
    }

    private static bool HasCustomCondition(Condition condition)
    {
        if (condition == null) return false;
        if (condition.GetType().Assembly != typeof(Condition).Assembly) return true;
        if (condition is ConditionLogicCollection collection) return HasCustomConditions(collection.conditions);
        return condition is ConditionLogicSingle single && HasCustomCondition(single.condition);
    }

    public static bool CanOptimizeNeeds(ThingDef race) => Built && race != null
        && needIndices.ContainsKey(race) && !customGraphics.Contains(race);

    public static int[] NeedIndices(ThingDef race, NeedDef need) => needIndices[race].For(need);

    public static bool IsNeedRelevant(ThingDef def, NeedDef need) =>
        def != null && need != null &&
        raceRelevantNeeds.TryGetValue(def, out var set) && set.Contains(need);

    public static bool RaceHasMood(ThingDef def) =>
        def != null && raceHasMood.TryGetValue(def, out var b) && b;

    public static List<ConditionNeed> GetNeedConditions(ThingDef def) =>
        def != null && raceNeedConditions.TryGetValue(def, out var l) ? l : null;

    public static int SensitiveRaceCount() => sensitiveRaces.Count;

    public static int MoodSensitiveCount()
    {
        int n = 0;
        foreach (bool has in raceHasMood.Values)
            if (has) n++;
        return n;
    }

    public static int NeedSensitiveCount()
    {
        int n = 0;
        foreach (List<ConditionNeed> l in raceNeedConditions.Values)
            if (l != null && l.Count > 0) n++;
        return n;
    }

    public static void Invalidate() => Built = false;
}
