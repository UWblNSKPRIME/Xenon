namespace HAROptimizer.Patches;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using AlienRace;
using HarmonyLib;
using HAROptimizer.Cache;
using Verse;

/// <summary>
/// Stage 2: replaces the allocation-per-frame LINQ check inside HAR's deferred addon
/// regeneration loop (<c>Enumerable.Intersect(...).Any()</c> -> <see cref="SetOverlap.Any"/>).
/// </summary>
public static class Opt_AlienCompUpdate
{
    private static readonly MethodInfo OverlapsMethod =
        AccessTools.Method(typeof(SetOverlap), nameof(SetOverlap.Any));

    private static bool applied;
    private static int replacementCount;

    public static void Patch(Harmony harmony)
    {
        applied = false;
        replacementCount = 0;

        var candidates = FindUpdateCandidates();
        if (candidates.Count == 0)
        {
            Log.Warning($"{PatchHelper.Tag} Stage2 AlienComp 'Update' local function not found, Overlaps optimization skipped.");
            PerfTelemetry.RecordReplacement("AlienComp.Update transpiler: 'Update' local function NOT found, skipped");
            return;
        }

        foreach (MethodBase target in candidates)
        {
            try
            {
                harmony.Patch(target, transpiler: new HarmonyMethod(typeof(Opt_AlienCompUpdate), nameof(Transpiler)));
                PatchHelper.RegisterDirectPatch(target);
            }
            catch (Exception e)
            {
                Log.Warning($"{PatchHelper.Tag} Stage2 could not transpile candidate '{target.Name}': {e.Message}");
            }
        }

        Log.Message(applied
            ? $"{PatchHelper.Tag} Stage2 applied: Intersect().Any() -> SetOverlap.Any() ({replacementCount} replacement(s), 0 allocations)."
            : $"{PatchHelper.Tag} <color=#F2D45C>Stage2 candidate(s) patched but Intersect().Any() pattern not found, no-op.</color>");

        PerfTelemetry.SetTranspilerCount(replacementCount);
        PerfTelemetry.RecordReplacement($"AlienComp.Update transpiler (Intersect().Any()->Overlaps()): " +
            (applied ? $"applied, {replacementCount} instruction(s) rewritten" : "pattern not found (no-op)"));
    }

    private static List<MethodBase> FindUpdateCandidates()
    {
        var result = new List<MethodBase>();
        Type alienCompType = typeof(AlienPartGenerator.AlienComp);

        foreach (Type t in EnumerateSelfAndNested(alienCompType))
        {
            foreach (MethodBase m in AccessTools.GetDeclaredMethods(t))
            {
                if (m is null || m.IsGenericMethodDefinition || m.ContainsGenericParameters)
                    continue;
                string name = m.Name;
                if (name.Contains("RegenerateAddonGraphicsWithCondition") && name.Contains("Update"))
                    result.Add(m);
            }
        }
        return result;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes    = new List<CodeInstruction>(instructions);
        var outCodes = new List<CodeInstruction>(codes.Count);
        int repl = 0;

        for (int i = 0; i < codes.Count; i++)
        {
            if (i < codes.Count - 1
                && codes[i].opcode == OpCodes.Call && IsIntersect2(codes[i].operand)
                && codes[i + 1].opcode == OpCodes.Call && IsAny0(codes[i + 1].operand))
            {
                var callvirt = new CodeInstruction(OpCodes.Call, OverlapsMethod);
                if (codes[i].labels is { Count: > 0 })
                    callvirt.labels.AddRange(codes[i].labels);
                callvirt.labels.AddRange(codes[i + 1].labels);
                callvirt.blocks.AddRange(codes[i].blocks);
                callvirt.blocks.AddRange(codes[i + 1].blocks);
                outCodes.Add(callvirt);
                i++;
                repl++;
                continue;
            }
            outCodes.Add(codes[i]);
        }

        if (repl > 0) applied = true;
        replacementCount += repl;
        return outCodes;
    }

    private static bool IsIntersect2(object operand) =>
        operand is MethodInfo mi
        && mi.DeclaringType == typeof(Enumerable)
        && mi.Name == nameof(Enumerable.Intersect)
        && mi.IsGenericMethod && mi.GetGenericArguments()[0] == typeof(Type)
        && mi.GetParameters().Length == 2;

    private static bool IsAny0(object operand) =>
        operand is MethodInfo mi
        && mi.DeclaringType == typeof(Enumerable)
        && mi.Name == nameof(Enumerable.Any)
        && mi.IsGenericMethod && mi.GetGenericArguments()[0] == typeof(Type)
        && mi.GetParameters().Length == 1;

    private static IEnumerable<Type> EnumerateSelfAndNested(Type root)
    {
        yield return root;
        foreach (Type n in root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (Type x in EnumerateSelfAndNested(n))
                yield return x;
    }
}
