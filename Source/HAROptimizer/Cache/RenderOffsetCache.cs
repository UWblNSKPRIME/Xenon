namespace HAROptimizer.Cache;

using System.Collections.Generic;
using AlienRace;
using RimWorld;
using UnityEngine;
using Verse;

/// <summary>
/// Stage 10: which alien races actually have a non-zero 2D head offset (#81) or non-zero
/// directional head offset (#80) on any <see cref="LifeStageAgeAlien"/> life stage.
///
/// Fix F5: Inspects actual resolved offset values so alien races with default (zero) head offsets
/// on <see cref="LifeStageAgeAlien"/> are also short-circuited without entering HAR's
/// per-frame life-stage lookup and directional chain.
/// </summary>
public static class RenderOffsetCache
{
    private static readonly HashSet<ThingDef> racesWithAlienLifeStage = new();
    private static readonly HashSet<ThingDef> racesWith2DOffset = new();
    private static readonly HashSet<ThingDef> racesWithDirectionalOffset = new();

    public static bool Built { get; private set; }
    public static bool Any2DOffset { get; private set; }
    public static bool AnyDirectionalOffset { get; private set; }

    public static void EnsureBuilt()
    {
        if (!Built && DefDatabase<ThingDef_AlienRace>.AllDefsListForReading.Count > 0)
            Build();
    }

    public static void Build()
    {
        Built = false;
        Any2DOffset = false;
        AnyDirectionalOffset = false;
        racesWithAlienLifeStage.Clear();
        racesWith2DOffset.Clear();
        racesWithDirectionalOffset.Clear();

        foreach (ThingDef_AlienRace race in DefDatabase<ThingDef_AlienRace>.AllDefs)
            ScanRace(race);

        Any2DOffset = racesWith2DOffset.Count > 0;
        AnyDirectionalOffset = racesWithDirectionalOffset.Count > 0;
        Built = true;
    }

    public static void ScanRace(ThingDef_AlienRace race)
    {
        List<LifeStageAge> stages = race?.race?.lifeStageAges;
        if (stages == null) return;

        AlienPartGenerator apg = race.alienRace?.generalSettings?.alienPartGenerator;
        bool hasStage = false;
        bool has2D = false;
        bool hasDir = false;

        for (int i = 0; i < stages.Count; i++)
        {
            if (stages[i] is not LifeStageAgeAlien lsa)
                continue;

            hasStage = true;

            // Mirror ThingDef_AlienRace.ResolveReferences (lines 35-36, 98-102):
            // headFemaleOffset defaults to negativeInfinity until ResolveReferences copies headOffset into it.
            Vector2 male2D = lsa.headOffset != Vector2.zero
                ? lsa.headOffset
                : (apg != null ? apg.headOffset : Vector2.zero);
            Vector2 female2D = !lsa.headFemaleOffset.Equals(Vector2.negativeInfinity)
                ? lsa.headFemaleOffset
                : (apg != null && !apg.headFemaleOffset.Equals(Vector2.negativeInfinity) ? apg.headFemaleOffset : male2D);

            if (male2D != Vector2.zero || female2D != Vector2.zero)
                has2D = true;

            if (HasNonZeroDirectional(lsa.headOffsetDirectional ?? apg?.headOffsetDirectional)
                || HasNonZeroDirectional(lsa.headFemaleOffsetDirectional ?? apg?.headFemaleOffsetDirectional))
                hasDir = true;
        }

        if (hasStage)
            racesWithAlienLifeStage.Add(race);
        if (has2D)
            racesWith2DOffset.Add(race);
        if (hasDir)
            racesWithDirectionalOffset.Add(race);
    }

    public static bool HasNonZeroDirectional(AlienPartGenerator.DirectionalOffset dir)
    {
        if (dir == null) return false;
        return HasNonZeroRotation(dir.south)
            || HasNonZeroRotation(dir.north)
            || HasNonZeroRotation(dir.east)
            || HasNonZeroRotation(dir.west);
    }

    private static bool HasNonZeroRotation(AlienPartGenerator.RotationOffset rot)
    {
        if (rot == null) return false;
        if (rot.layerOffset != 0f || rot.offset != Vector2.zero)
            return true;
        if (rot.bodyTypes != null && rot.bodyTypes.Count > 0)
            return true;
        if (rot.portraitBodyTypes != null && rot.portraitBodyTypes.Count > 0)
            return true;
        if (rot.headTypes != null && rot.headTypes.Count > 0)
            return true;
        if (rot.portraitHeadTypes != null && rot.portraitHeadTypes.Count > 0)
            return true;
        return false;
    }

    public static bool RaceMayShiftHead2D(ThingDef def)
    {
        if (!Built) return true; // fail-open
        if (!Any2DOffset || def is not ThingDef_AlienRace) return false;
        return racesWith2DOffset.Contains(def);
    }

    public static bool RaceMayShiftHeadDirectional(ThingDef def)
    {
        if (!Built) return true; // fail-open
        if (!AnyDirectionalOffset || def is not ThingDef_AlienRace) return false;
        return racesWithDirectionalOffset.Contains(def);
    }

    public static bool RaceMayShiftHead(ThingDef def) =>
        RaceMayShiftHead2D(def) || RaceMayShiftHeadDirectional(def);

    public static int AlienStageRaceCount() => racesWithAlienLifeStage.Count;

    public static void Invalidate()
    {
        Built = false;
        Any2DOffset = false;
        AnyDirectionalOffset = false;
    }
}
