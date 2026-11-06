namespace HAROptimizer.Cache;

using System;
using System.Collections.Generic;
using AlienRace;
using RimWorld;
using Verse;

/// <summary>
/// Stage 8: precomputed lookups that turn HAR's two dynamic work-giver postfixes
/// (<c>GenericHasJobOnThingPostfix</c> D2 and <c>GenericJobOnThingPostfix</c> D1) from an
/// O(races x workGivers) LINQ scan (with a closure, every work query) into two O(1) set probes.
///
/// HAR's original expression, for a work-giver instance and a pawn:
///   allowed = pawnRaceListsThisGiverClass(pawn, giverClass)
///             || !AnyOtherRaceListsThisGiverDef(pawn.def, giverDef)
/// The right-hand term enumerates <c>ThingDef_AlienRace.AllDefs</c> and, for each race, its whole
/// <c>workGiverList</c>, on every JobOnThing/HasJobOnThing call during work search. We precompute:
///   • raceGiverClasses  : race -> set of giverClass Types it lists.
///   • defClaimedByRaces : WorkGiverDef -> set of races that list it.
/// so both terms become dictionary/set probes with zero allocation and zero closure.
/// </summary>
public static class RestrictionCache
{
    private static readonly Dictionary<ThingDef, HashSet<Type>> raceGiverClasses = new();
    private static readonly Dictionary<WorkGiverDef, HashSet<ThingDef>> defClaimedByRaces = new();
    private static readonly HashSet<Type> allGiverClasses = new();

    // Stage 12: apparel / food restriction precomputation.
    // HAR's #157 PawnCanWear and #23 ApparelScoreGain always call RaceRestrictionSettings.CanWear,
    // and #67 CanEverEat always calls CanEat. We prove when the result is trivially "allowed":
    //   CanWear(apparel, race) == true  for every apparel  iff
    //     the global apparelRestricted set is empty AND this race neither opts into
    //     onlyUseRaceRestrictedApparel nor has a non-empty blackApparelList.
    //   CanEat(food, race)  == true  for every food  iff
    //     global foodRestricted empty AND race has no onlyEatRaceRestrictedFood/blackFoodList
    //     AND race defines no chemicalSettings (CanUseChemical would otherwise matter).
    private static readonly HashSet<ThingDef> racesAffectingApparel = new();
    private static readonly HashSet<ThingDef> racesAffectingFood = new();
    private static bool apparelGlobalSetEmpty;
    private static bool foodGlobalSetEmpty;
    private static bool apparelAlwaysAllowed;
    private static bool foodAlwaysAllowed;

    public static bool Built { get; private set; }

    public static void EnsureBuilt()
    {
        if (!Built && DefDatabase<ThingDef_AlienRace>.AllDefsListForReading.Count > 0)
            Build();
    }

    public static void Build()
    {
        raceGiverClasses.Clear();
        defClaimedByRaces.Clear();
        allGiverClasses.Clear();
        racesAffectingApparel.Clear();
        racesAffectingFood.Clear();

        foreach (ThingDef_AlienRace race in DefDatabase<ThingDef_AlienRace>.AllDefs)
        {
            // ---- Stage 12: apparel / food restriction contributors ----
            RaceRestrictionSettings rr = race.alienRace?.raceRestriction;
            if (rr != null)
            {
                if (rr.onlyUseRaceRestrictedApparel || (rr.blackApparelList?.Count > 0))
                    racesAffectingApparel.Add(race);
                if (rr.onlyEatRaceRestrictedFood || (rr.blackFoodList?.Count > 0)
                    || (race.alienRace?.generalSettings?.chemicalSettings?.Count > 0))
                    racesAffectingFood.Add(race);
            }

            List<WorkGiverDef> list = race.alienRace?.raceRestriction?.workGiverList;
            if (list == null) continue;

            for (int i = 0; i < list.Count; i++)
            {
                WorkGiverDef wgd = list[i];
                if (wgd == null || wgd.giverClass == null) continue;

                if (!raceGiverClasses.TryGetValue(race, out var classes))
                    raceGiverClasses[race] = classes = new HashSet<Type>();
                classes.Add(wgd.giverClass);

                if (!defClaimedByRaces.TryGetValue(wgd, out var racers))
                    defClaimedByRaces[wgd] = racers = new HashSet<ThingDef>();
                racers.Add(race);

                allGiverClasses.Add(wgd.giverClass);
            }
        }

        Built = true;

        // Derived flags (read from HAR's populated global sets, so must run AFTER the loop).
        apparelGlobalSetEmpty = RaceRestrictionSettings.apparelRestricted.Count == 0;
        foodGlobalSetEmpty    = RaceRestrictionSettings.foodRestricted.Count == 0;
        apparelAlwaysAllowed  = apparelGlobalSetEmpty && racesAffectingApparel.Count == 0;
        foodAlwaysAllowed     = foodGlobalSetEmpty    && racesAffectingFood.Count == 0;
    }

    // ---- hot-path probes ----

    /// <summary>Does this race list a work-giver of exactly <paramref name="giverClass"/>? (cond1)</summary>
    public static bool RaceListsGiverClass(ThingDef race, Type giverClass) =>
        race != null && giverClass != null
        && raceGiverClasses.TryGetValue(race, out var set) && set.Contains(giverClass);

    /// <summary>Is <paramref name="def"/> listed by any race OTHER than <paramref name="pawnRace"/>? (cond2)</summary>
    public static bool AnyOtherRaceListsDef(ThingDef pawnRace, WorkGiverDef def)
    {
        if (def == null || !defClaimedByRaces.TryGetValue(def, out var set) || set.Count == 0)
            return false;
        if (set.Count > 1)
            return true; // at least one entry differs from pawnRace (pawnRace appears at most once)
        // exactly one claimant: it is "other" only if it isn't the pawn's own race
        foreach (ThingDef r in set)
            return r != pawnRace;
        return false;
    }

    // ---- dynamic-patch install surface (called once at SCCS / def-load time) ----

    /// <summary>All distinct work-giver classes any race lists. These are the D1/D2 patch targets.</summary>
    public static IEnumerable<Type> GiverClasses() => allGiverClasses;

    // ---- Stage 12: apparel / food trivially-allowed probes (hot path) ----

    /// <summary>
    /// True when, for a pawn of <paramref name="race"/>, <c>RaceRestrictionSettings.CanWear</c> is
    /// provably <c>true</c> for every apparel, so Xenon skips HAR's #157/#23 postfix.
    /// Conservative: returns <c>false</c> (i.e. run HAR) when the cache isn't built or the pawn is unknown.
    /// </summary>
    public static bool ApparelTriviallyAllowed(ThingDef race)
    {
        if (!Built || race == null) return false;
        if (apparelAlwaysAllowed) return true;
        return apparelGlobalSetEmpty && !racesAffectingApparel.Contains(race);
    }

    /// <summary>
    /// True when <c>RaceRestrictionSettings.CanEat</c> is provably <c>true</c> for every food and every
    /// race, so Xenon skips HAR's #67 postfix. Global-only (the postfix has no cheap pawn handle).
    /// </summary>
    public static bool FoodTriviallyAllowed => Built && foodAlwaysAllowed;

    public static int RaceCount() => raceGiverClasses.Count;
    public static int GiverClassCount() => allGiverClasses.Count;

    public static void Invalidate() => Built = false;
}
