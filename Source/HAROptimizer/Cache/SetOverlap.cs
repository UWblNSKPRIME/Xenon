namespace HAROptimizer.Cache;

using System;
using System.Collections.Generic;

public static class SetOverlap
{
    // Match HashSet.Overlaps: membership is tested with the receiver's comparer.
    // Concrete enumeration avoids boxing HashSet<T>.Enumerator as IEnumerable<T>.
    public static bool Any(HashSet<Type> owned, HashSet<Type> requested)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(requested);
        if (owned.Count == 0 || requested.Count == 0) return false;
        foreach (Type type in requested)
            if (owned.Contains(type)) return true;
        return false;
    }
}
