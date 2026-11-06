namespace HAROptimizer.Patches;

using System.Collections.Generic;
using AlienRace;
using HarmonyLib;
using RimWorld;
using Verse;

/// <summary>
/// Stage 5: drop-in replacement for HAR's <c>CanEquipPostfix</c> (#210).
/// Uses <see cref="PatchHelper.ReplacePatch"/> for transactional replacement (Fix F2)
/// and avoids telemetry calls when <see cref="PerfTelemetry.Enabled"/> is false (Fix F6).
/// </summary>
public static class Opt_Restrictions
{
    private static readonly Dictionary<ThingDef, string> WearReasons  = new();
    private static readonly Dictionary<ThingDef, string> EquipReasons = new();

    public static void Patch(Harmony harmony)
    {
        var canEquip = AccessTools.Method(typeof(EquipmentUtility), nameof(EquipmentUtility.CanEquip),
                     new[] { typeof(Thing), typeof(Pawn), typeof(string).MakeByRefType(), typeof(bool) });
        var original = AccessTools.Method(typeof(HarmonyPatches), nameof(HarmonyPatches.CanEquipPostfix));
        var ours = AccessTools.Method(typeof(Opt_Restrictions), nameof(Postfix));

        PatchHelper.ReplacePatch(harmony, canEquip, original, ours, isPrefix: false,
            label: "EquipmentUtility.CanEquip (#210 CanEquipPostfix)");
    }

    public static void Postfix(ref bool __result, Thing thing, Pawn pawn, ref string cantReason)
    {
        if (!PerfTelemetry.Enabled)
        {
            if (!__result || thing == null || pawn == null)
                return;

            ThingDef r = pawn.def;
            if (thing.def.IsApparel && !RaceRestrictionSettings.CanWear(thing.def, r))
            {
                __result   = false;
                cantReason = WearReason(r);
                return;
            }
            if (thing.def.IsWeapon && !RaceRestrictionSettings.CanEquip(thing.def, r))
            {
                __result   = false;
                cantReason = EquipReason(r);
            }
            return;
        }

        long t0 = PerfTelemetry.Stamp();
        PerfTelemetry.Call(OptPatch.CanEquip);

        if (!__result || thing == null || pawn == null)
        {
            PerfTelemetry.Fast(OptPatch.CanEquip, t0);
            return;
        }

        ThingDef race = pawn.def;

        if (thing.def.IsApparel && !RaceRestrictionSettings.CanWear(thing.def, race))
        {
            __result    = false;
            cantReason  = WearReason(race);
            PerfTelemetry.Executed(OptPatch.CanEquip, t0);
            return;
        }

        if (thing.def.IsWeapon && !RaceRestrictionSettings.CanEquip(thing.def, race))
        {
            __result   = false;
            cantReason = EquipReason(race);
            PerfTelemetry.Executed(OptPatch.CanEquip, t0);
            return;
        }

        PerfTelemetry.Fast(OptPatch.CanEquip, t0);
    }

    private static string WearReason(ThingDef race)
    {
        if (!WearReasons.TryGetValue(race, out string s))
            WearReasons[race] = s = $"{race.LabelCap} can't wear this";
        return s;
    }

    private static string EquipReason(ThingDef race)
    {
        if (!EquipReasons.TryGetValue(race, out string s))
            EquipReasons[race] = s = $"{race.LabelCap} can't equip this";
        return s;
    }
}
