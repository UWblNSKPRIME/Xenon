namespace HAROptimizer.Cache;

using System;
using System.Collections.Generic;
using AlienRace.ExtendedGraphics;
using RimWorld;

// Indices refer to the original condition list, keeping each pawn's state layout stable.
public sealed class NeedConditionIndex
{
    private readonly Dictionary<NeedDef, int[]> byNeed = new();
    public NeedConditionIndex(List<ConditionNeed> conditions)
    {
        var groups = new Dictionary<NeedDef, List<int>>();
        for (int i = 0; i < conditions.Count; i++)
        {
            NeedDef def = conditions[i].need;
            if (def == null) continue;
            if (!groups.TryGetValue(def, out var indices)) groups[def] = indices = new();
            indices.Add(i);
        }
        foreach (var group in groups) byNeed.Add(group.Key, group.Value.ToArray());
    }
    public int[] For(NeedDef need) => need != null && byNeed.TryGetValue(need, out var indices)
        ? indices : Array.Empty<int>();
}
