namespace HAROptimizer.Cache;

using System.Runtime.CompilerServices;
using System.Threading;
using AlienRace;
using Verse;

/// <summary>
/// Cheap access to <see cref="AlienPartGenerator.AlienComp"/> for a pawn, plus a per-pawn
/// scratch state used by the need-graphic flip detection (Stage 3).
///
/// Backed by <see cref="ConditionalWeakTable{TKey,TValue}"/> keyed by <see cref="Pawn"/> so
/// destroyed/despawned raid pawns are never strongly rooted by the cache and can be collected by
/// the GC during play (fixes F6). Thread-safe and atomically replaceable on <see cref="Clear"/> (F7).
/// </summary>
public static class PawnCompCache
{
    private sealed class PawnEntry
    {
        public AlienPartGenerator.AlienComp Comp;
        public PawnNeedState NeedState;
    }

    private static ConditionalWeakTable<Pawn, PawnEntry> table = new();

    public static void Clear() =>
        Volatile.Write(ref table, new ConditionalWeakTable<Pawn, PawnEntry>());

    /// <summary>Fast path: returns false without touching the comp list for non-alien pawns.</summary>
    public static bool TryGetAlienCompFast(Pawn pawn, out AlienPartGenerator.AlienComp comp)
    {
        if (pawn?.def is not ThingDef_AlienRace)
        {
            comp = null;
            return false;
        }

        PawnEntry entry = Volatile.Read(ref table).GetOrCreateValue(pawn);
        comp = entry.Comp;
        if (comp != null)
            return true;

        comp = pawn.GetComp<AlienPartGenerator.AlienComp>();
        if (comp != null)
            entry.Comp = comp;
        return comp != null;
    }

    /// <summary>
    /// Per-pawn scratch: last satisfaction bit per race ConditionNeed, and last Mood band.
    /// Allocated once per pawn (sized to that race's ConditionNeed count); never in a hot loop.
    /// </summary>
    public static PawnNeedState GetOrCreateState(Pawn pawn, int needConditionCount)
    {
        PawnEntry entry = Volatile.Read(ref table).GetOrCreateValue(pawn);
        PawnNeedState s = entry.NeedState;
        if (s != null && s.Length == needConditionCount)
            return s;

        s = new PawnNeedState(needConditionCount);
        entry.NeedState = s;
        return s;
    }

    public sealed class PawnNeedState
    {
        public bool[] lastNeedSatisfied; // index-aligned with RaceConditionCache.GetNeedConditions(race)
        public bool[] needInitialized;
        public int    lastMoodBand = -1; // -1 = not computed yet

        public PawnNeedState(int needConditionCount)
        {
            lastNeedSatisfied = needConditionCount > 0 ? new bool[needConditionCount] : System.Array.Empty<bool>();
            needInitialized = needConditionCount > 0 ? new bool[needConditionCount] : System.Array.Empty<bool>();
        }

        public int Length => lastNeedSatisfied.Length;
    }
}
