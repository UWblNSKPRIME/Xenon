namespace HAROptimizer.Cache;

using System.Collections.Generic;
using AlienRace;
using RimWorld;
using Verse;

/// <summary>
/// Stage 9: which alien races actually change their body across life stages.
///
/// HAR's <c>BodyReferenceTranspiler</c> (D3) rewrites EVERY <c>def.race.body</c> field load across the
/// whole game assembly into a call to <c>HarmonyPatches.ReplacedBody(pawn)</c>, which for an alien reads
///   <c>(pawn.ageTracker?.CurLifeStageRace as LifeStageAgeAlien)?.body ?? pawn.RaceProps.body</c>.
/// For a race whose <c>lifeStageAges</c> never set <c>LifeStageAgeAlien.body</c> (and for every human)
/// that whole age-tracker lookup provably resolves to <c>pawn.RaceProps.body</c>. We precompute the
/// small set of races that DO override a body with age so the hot path can short-circuit everyone else.
/// </summary>
public static class BodyOverrideCache
{
    private static readonly HashSet<ThingDef> racesWithBodyOverride = new();

    public static bool Built { get; private set; }
    public static bool AnyOverride { get; private set; }

    public static void EnsureBuilt()
    {
        if (!Built && DefDatabase<ThingDef_AlienRace>.AllDefsListForReading.Count > 0)
            Build();
    }

    public static void Build()
    {
        Built = false;
        AnyOverride = false;
        racesWithBodyOverride.Clear();

        foreach (ThingDef_AlienRace race in DefDatabase<ThingDef_AlienRace>.AllDefs)
        {
            List<LifeStageAge> stages = race.race?.lifeStageAges;
            if (stages == null) continue;

            for (int i = 0; i < stages.Count; i++)
                if (stages[i] is LifeStageAgeAlien lsa && lsa.body != null)
                {
                    racesWithBodyOverride.Add(race);
                    break;
                }
        }

        AnyOverride = racesWithBodyOverride.Count > 0;
        Built = true;
    }

    /// <summary>
    /// True only for races that actually swap body by life stage; everyone else is a fast path.
    /// Fix F4: O(1) bool + type check avoids HashSet lookup for Humans and when AnyOverride is false.
    /// </summary>
    public static bool RaceOverridesBody(ThingDef def)
    {
        if (!Built) return true; // fail-open when cache not ready
        if (!AnyOverride || def is not ThingDef_AlienRace) return false;
        return racesWithBodyOverride.Contains(def);
    }

    public static int OverrideRaceCount() => racesWithBodyOverride.Count;

    public static void Invalidate()
    {
        Built = false;
        AnyOverride = false;
    }
}
