# Xenon

Xenon is a performance add-on for Humanoid Alien Races on RimWorld 1.7.

HAR hooks into pawn needs, hediffs, work givers, thoughts, apparel, food, and rendering. Most of those hooks run for every pawn on the map, humans included, even when the pawn's race does not use the feature being checked. Xenon loads after HAR, builds lookup tables from the loaded race defs in about 20 to 40 ms, and swaps 24 of HAR's Harmony patches for versions that check those tables first. When a race actually uses a feature, Xenon calls HAR's original method.

## Why HAR slows down colonies

The worst offender is `Need.CurLevel`. Every time hunger, rest, or mood ticks on any pawn, HAR allocates a `HashSet<Type>` and queues an addon graphics rebuild. Most alien races do not tie body addon graphics to need levels at all. On a test save with 10 Miho, 10 MoeLotl, and 10 Milira pawns, none of the eight loaded races had need or mood graphic conditions, yet HAR queued 1,512 to 1,698 addon rebuilds every 600 ticks. Over a ten-minute run that added up to 345,382 wasted rebuild requests. Milira alone has 50 body addons and 249 condition triggers, so checking them over and over burns frame time and feeds the garbage collector.

With Xenon active, those 1,500+ rebuild requests per 600 ticks drop to zero.

## What Xenon changes

**Need and mood checks.** `Opt_NeedLevel` checks `if (race is not ThingDef_AlienRace)` first, so human pawns return in a couple of instructions instead of allocating a hash set. For alien races, `RaceConditionCache` and `NeedConditionIndex` group conditions by `NeedDef`. Races without need conditions exit right away. Races that do use them only check the thresholds for the specific need that changed.

**Hediff changes.** `Opt_HealthTracker` replaces HAR's postfixes on `AddHediff`, `RemoveHediff`, and `Notify_HediffChanged`. A new scar or blood loss tick only triggers an addon rebuild if the pawn's race has a `ConditionHediff` for that `HediffDef` or uses `ConditionHediffSeverity`. Unspawned pawns also exit early.

**Set overlap in `AlienComp`.** `Opt_AlienCompUpdate` transpiles `AlienComp`'s update loop to call `SetOverlap.Any` instead of `Enumerable.Intersect().Any()`. That cuts 312 bytes of allocations per check down to 0 bytes and runs 5.7x faster in microbenchmarks.

**Work giver restrictions.** HAR's `GenericJobOnThingPostfix` and `GenericHasJobOnThingPostfix` scan all alien races and their `workGiverList` with LINQ on every work query. `Opt_WorkRestrictions` deduplicates the target `MethodInfo` entries so inherited methods are patched once, then answers each check with two hash lookups from `RestrictionCache`.

**Thoughts, apparel, and food.** `Opt_Thoughts` skips `ThoughtReplacer` prefixes for races without replacers and delegates `CanGetThought` to HAR's memoized dictionary when restrictions exist. `Opt_ApparelAndFood` skips `PawnCanWear`, `ApparelScoreGain`, and `WillEat` checks when no race restricts apparel or food.

**Body overrides and head offsets.** `Opt_BodyAndHead` skips the life-stage lookup in `ReplacedBody` when a race does not override its body def. `Opt_RenderOffsets` checks actual `headOffset` and `headOffsetDirectional` values at startup, so alien races with zero offsets skip the per-frame field chain in `BaseHeadOffsetAt`.

## Microbenchmark numbers

Measured on .NET 10 Release across 200,000 iterations and 5 repeats:

| Path | Original HAR | Xenon | Speedup | Allocations |
| :--- | :---: | :---: | :---: | :---: |
| `AlienComp` set overlap hit | 762.56 ns | 133.43 ns | 5.71x | 312 B to 0 B |
| `Need.CurLevel` on human pawn | 290.84 ns | 7.04 ns | 41.32x | 176 B to 0 B |
| `Need.CurLevel` scan, 101 conditions to 10 | 217.23 ns | 91.15 ns | 2.38x | 0 B to 0 B |
| `AddHediff` irrelevant scar on alien pawn | 203.03 ns | 6.25 ns | 32.47x | 176 B to 0 B |
| `WorkGiver` check across 12 races | 353.18 ns | 62.11 ns | 5.69x | 0 B to 0 B |
| `ReplacedBody` on alien without override | 67.61 ns | 26.27 ns | 2.57x | 0 B to 0 B |
| `BaseHeadOffsetAt` on alien with zero offset | 86.07 ns | 19.08 ns | 4.51x | 0 B to 0 B |

## Installation and save compatibility

1. Subscribe to or install Humanoid Alien Races.
2. Place Xenon in `RimWorld/Mods/` and enable it after HAR.
3. You can add or remove Xenon mid-save. It adds no defs and stores nothing in save files.

## Telemetry and A/B testing

By default Xenon runs with telemetry off, so hot paths do not call `Stopwatch.GetTimestamp()` or write to disk.

To record a profile on your own save, open Mod Settings, select Xenon, and turn on `Enable Benchmark Logging`. Xenon writes a snapshot to `Xenon_Perf.log` in the mod folder every 600 ticks. If you also turn on `A/B Baseline mode` and restart, Xenon leaves HAR's original patches untouched while keeping the regen counter active, so you can compare the two runs side by side.

## Building from source

```powershell
dotnet build Source/HAROptimizer/HAROptimizer.csproj -c Release -p:RimWorldDir="D:\Steam\steamapps\common\RimWorld\"
```

The build targets `net10.0` and writes `1.7/Assemblies/HAROptimizer.dll`.

## Author

Created by wevsy.
