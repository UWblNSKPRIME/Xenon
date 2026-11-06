namespace HAROptimizer;

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

/// <summary>
/// Which optimized patch a counter belongs to. Index order must match <see cref="PerfTelemetry.Label"/>.
/// </summary>
public enum OptPatch
{
    NeedLevel,       // Stage 3  - Need.CurLevel setter postfix (#166)
    AddHediff,       // Stage 4  - Pawn_HealthTracker.AddHediff postfix (#197)
    RemoveHediff,    // Stage 4  - Pawn_HealthTracker.RemoveHediff postfix (#198)
    HediffChanged,   // Stage 4  - Pawn_HealthTracker.Notify_HediffChanged postfix (#199)
    CanEquip,        // Stage 5  - EquipmentUtility.CanEquip postfix (#210)
    AlienCompUpdate, // Stage 2  - transpiler target (not per-call; kept for the startup table)
    AddonRegenGate,  // Stage 6  - prefix gate on both RegenerateAddonGraphicsWithCondition overloads
    ThoughtCanGet,   // Stage 7  - CanGetThought postfix (#24) fast path
    ThoughtReplace,  // Stage 7  - TryGainMemory/TryCreateThought/ThoughtReplacement prefixes (#25-32)
    WorkRestriction, // Stage 8  - D1/D2 generic JobOnThing/HasJobOnThing work-giver postfixes
    ReplacedBody,    // Stage 9  - fast-path prefix on HarmonyPatches.ReplacedBody (D3)
    RenderOffset,    // Stage 10 - BaseHeadOffsetAt helper/postfix (#80-81) race gate
    ApparelFood,     // Stage 12 - gated PawnCanWear (#157) / ApparelScoreGain (#23) / CanEverEat (#67)
}

/// <summary>
/// Self-contained, opt-in benchmark telemetry. Writes to <c>&lt;ModRoot&gt;/Xenon_Perf.log</c>
/// so profiling numbers can be collected without relying on the in-game renderer or AppData logs.
/// It is disabled by default; enable it from the mod settings or a benchmark marker file.
///
/// Every hot-path entry point is gated by a single <see cref="Enabled"/> bool: when logging is off
/// the only cost is a couple of inlined branches, so shipping it to players is harmless. When on,
/// we capture <see cref="Stopwatch.GetTimestamp()"/> per call and accumulate per-outcome counters
/// (fast-path skip / threshold skip / actual execution) for each patch.
/// </summary>
public static class PerfTelemetry
{
    private const int PatchCount = 13;

    // Master gate. Set by HAROptimizerMod from settings (or #if DEBUG). All public helpers no-op when false.
    public static bool Enabled;

    private static string logPath;
    private static readonly object ioLock = new object();

    private static readonly string[] Label =
    {
        "Opt_NeedLevel  (Need.CurLevel #166)",
        "Opt_HealthTracker.AddHediff (#197)",
        "Opt_HealthTracker.RemoveHediff (#198)",
        "Opt_HealthTracker.HediffChanged (#199)",
        "Opt_Restrictions.CanEquip (#210)",
        "Opt_AlienCompUpdate (transpiler)",
        "Opt_AddonRegenGate (regen prefix)",
        "Opt_Thoughts.CanGetThought (#24)",
        "Opt_Thoughts.Replace (#25-32)",
        "Opt_WorkRestrictions (D1/D2 JobOnThing)",
        "Opt_BodyAndHead.ReplacedBody (D3)",
        "Opt_RenderOffsets.BaseHeadOffsetAt (#80-81)",
        "Opt_ApparelAndFood (#23/#67/#157)",
    };

    // ---- cumulative counters ----
    private static readonly long[] total  = new long[PatchCount];
    private static readonly long[] fast   = new long[PatchCount];
    private static readonly long[] thresh = new long[PatchCount];
    private static readonly long[] exec   = new long[PatchCount];
    private static readonly long[] ticks  = new long[PatchCount];

    // ---- window snapshots (value at last dump) ----
    private static readonly long[] sTotal  = new long[PatchCount];
    private static readonly long[] sFast   = new long[PatchCount];
    private static readonly long[] sThresh = new long[PatchCount];
    private static readonly long[] sExec   = new long[PatchCount];
    private static readonly long[] sTicks  = new long[PatchCount];

    // ---- startup facts ----
    public static int TotalAlienRaces;
    public static int MoodSensitive;
    public static int NeedSensitive;
    public static int HediffSensitive;
    public static string HediffRaceNames = "none";
    public static int TranspilerReplacements;

    // ---- A/B benchmark facts (set by HAROptimizerMod / StartupInit) ----
    // "OPTIMIZED (patch active)" or "BASELINE (patch disabled)". Printed on every dump so the
    // two runs are unmistakable in one log file.
    public static string Mode = "OPTIMIZED (patch active)";
    // Shared, mode-independent probe: how many times HAR's expensive
    // AlienComp.RegenerateAddonGraphicsWithCondition(...) was actually invoked (this is the work
    // the optimization removes). Counted by Opt_ABProbe on BOTH baseline and optimized runs.
    private static long regenCalls;
    private static long regenTicks;
    private static long sRegenCalls;
    private static long sRegenTicks;
    private static long regenAllocatedBytes;
    private static long sRegenAllocatedBytes;
    // ---- memory (A/B): cumulative managed bytes allocated by the process (GC.GetTotalAllocatedBytes) ----
    private static long allocBaseline;                 // value at last dump -> window delta
    private static long windowStartTimestamp;
    private static readonly int[] collectionBaseline = new int[3];
    private static TimeSpan pauseBaseline;
    private static readonly StringBuilder memLines = new StringBuilder();
    private static readonly StringBuilder modeLines = new StringBuilder();
    private static readonly StringBuilder patchLines = new StringBuilder();

    private static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;
    private static long dumpAtTicks;

    /// <summary>Sets the target file (called from the Mod constructor with the mod's root dir).</summary>
    public static void Init(string modRootDir)
    {
        logPath = Path.Combine(modRootDir, "Xenon_Perf.log");
        if (!Enabled) return;
        try
        {
            File.WriteAllText(logPath,
                "# Xenon performance telemetry (benchmark logging ENABLED)\n" +
                "# windows are 600 game ticks (~10s at 1x)\n\n");
        }
        catch (Exception e) { LogNonBlocking("init", e); }
    }

    /// <summary>Capture a timestamp only when logging is on (avoids the Stopwatch cost otherwise).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Stamp() => Enabled ? Stopwatch.GetTimestamp() : 0L;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Call(OptPatch p)
    {
        if (!Enabled) return;
        total[(int)p]++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fast(OptPatch p, long t0)
    {
        if (!Enabled) return;
        int i = (int)p;
        fast[i]++;
        ticks[i] += Stopwatch.GetTimestamp() - t0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Thresh(OptPatch p, long t0)
    {
        if (!Enabled) return;
        int i = (int)p;
        thresh[i]++;
        ticks[i] += Stopwatch.GetTimestamp() - t0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Executed(OptPatch p, long t0)
    {
        if (!Enabled) return;
        int i = (int)p;
        exec[i]++;
        ticks[i] += Stopwatch.GetTimestamp() - t0;
    }

    /// <summary>Records an unpatch/replace outcome for the startup report (called at StaticConstructorOnStartup).</summary>
    public static void RecordReplacement(string line)
    {
        if (!Enabled) return;
        patchLines.AppendLine("  - " + line);
    }

    public static void SetTranspilerCount(int replacements) => TranspilerReplacements = replacements;

    /// <summary>Records that the optimization stages are off and the ORIGINAL HAR patch is left in place.</summary>
    public static void SetBaselineMode(string note)
    {
        Mode = "BASELINE (HAROptimizer disabled)";
        if (!Enabled) return;
        modeLines.Clear();
        modeLines.AppendLine("  NOTE: " + note);
        memLines.Clear();
        memLines.AppendLine("      MODE: BASELINE, HAR original patches active (expected: higher allocation rate)");
    }

    /// <summary>Current cumulative managed-heap allocation in bytes (0 when disabled). Cheap, called once per dump.</summary>
    public static long CurAllocBytes() => Enabled ? GC.GetTotalAllocatedBytes(precise: false) : 0L;

    /// <summary>Resets the memory window baseline to the current cumulative value (call at each dump).</summary>
    public static void MarkMemWindow(long curAllocBytes) => allocBaseline = curAllocBytes;

    // No forced collection: sampling must not change normal game/GC behavior.
    public static void BeginMemoryWindow()
    {
        if (!Enabled) return;
        allocBaseline = CurAllocBytes();
        for (int i = 0; i < 3; i++) collectionBaseline[i] = GC.CollectionCount(i);
        pauseBaseline = GC.GetTotalPauseDuration();
        windowStartTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// One-line live session summary for the mod settings screen: total hot-path calls intercepted,
    /// how many were skipped by the fast gates, the cut-off rate, and the mode-independent regen count.
    /// Only meaningful while <see cref="Enabled"/>; returns a hint otherwise.
    /// </summary>
    public static string LiveSummary()
    {
        if (!Enabled) return "(telemetry disabled, enable Benchmark Logging to collect data)";
        long tot = 0, skip = 0, ex = 0;
        for (int i = 0; i < PatchCount; i++)
        {
            tot  += total[i];
            skip += fast[i] + thresh[i];
            ex   += exec[i];
        }
        double cut = tot > 0 ? (double)skip / tot * 100.0 : 0.0;
        return $"Session: {tot:N0} calls intercepted | {skip:N0} skipped ({cut:0.#}%) | " +
               $"{ex:N0} ran full HAR logic | regen requests: {regenCalls:N0} | mode: {Mode}";
    }

    /// <summary>Entry into HAR's expensive addon-regeneration request (prefix of the static overload).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long RegenEnter() => Enabled ? Stopwatch.GetTimestamp() : 0L;

    /// <summary>Exit from that request; accumulates the mode-independent work counter.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RegenExit(long t0, long allocatedBytes)
    {
        if (!Enabled) return;
        regenCalls++;
        regenAllocatedBytes += allocatedBytes;
        regenTicks += Stopwatch.GetTimestamp() - t0;
    }

    /// <summary>One-time startup block: patch results + def/sensitive counts + transpiler status.</summary>
    public static void LogStartup()
    {
        if (!Enabled) return;
        Array.Clear(total); Array.Clear(fast); Array.Clear(thresh); Array.Clear(exec); Array.Clear(ticks);
        Array.Clear(sTotal); Array.Clear(sFast); Array.Clear(sThresh); Array.Clear(sExec); Array.Clear(sTicks);
        regenCalls = regenTicks = sRegenCalls = sRegenTicks = dumpAtTicks = 0;
        regenAllocatedBytes = sRegenAllocatedBytes = 0;
        BeginMemoryWindow();
        long cur = CurAllocBytes();
        allocBaseline = cur; // memory delta measured from startup onward
        var sb = new StringBuilder();
        sb.AppendLine("=== [" + Now() + "] Xenon startup report ===");
        sb.AppendLine("Patches unpatched from HAR and replaced:");
        if (patchLines.Length == 0) sb.AppendLine("  - (none recorded)");
        else sb.Append(patchLines);
        sb.AppendLine($"ThingDef_AlienRace total in game : {TotalAlienRaces}");
        sb.AppendLine($"Sensitive races  Mood={MoodSensitive}  Need={NeedSensitive}  Hediff={HediffSensitive}");
        sb.AppendLine($"Hediff-sensitive race defNames   : {HediffRaceNames}");
        sb.AppendLine($"Transpiler Intersect().Any() -> Overlaps() replacements : {TranspilerReplacements}");
        sb.AppendLine();
        AppendSnapshot(sb, header: "=== [" + Now() + "] baseline (cumulative at startup) \u2014 MODE: " + Mode + " ===", window: false, curAllocBytes: cur);
        Write(sb.ToString());
    }

    /// <summary>Called by <see cref="Patches.Opt_TelemetryTick"/> every 600 ticks. Emits the per-window table then resets the window.</summary>
    public static void DumpWindow(int elapsedTicks, int frames)
    {
        if (!Enabled) return;
        dumpAtTicks += elapsedTicks;
        double seconds = (Stopwatch.GetTimestamp() - windowStartTimestamp) / (double)Stopwatch.Frequency;
        long cur = CurAllocBytes();
        var sb = new StringBuilder();
        AppendSnapshot(sb, header: $"=== [{Now()}] {dumpAtTicks:N0} actual ticks (window={elapsedTicks}, frames={frames}, seconds={seconds:F3}), MODE: {Mode} ===", window: true, curAllocBytes: cur);
        double bytes = cur - allocBaseline;
        sb.AppendLine(FormattableString.Invariant($"      allocation rate    : {bytes / Math.Max(seconds, 0.000001) / 1048576.0:F3} MB/s; {bytes / elapsedTicks:F1} bytes/tick; {bytes / Math.Max(frames, 1):F1} bytes/observed-frame"));
        sb.AppendLine(FormattableString.Invariant($"      managed heap now   : {GC.GetTotalMemory(false) / 1048576.0:F3} MB (includes uncollected objects; not retained size)"));
        var info = GC.GetGCMemoryInfo();
        sb.AppendLine(FormattableString.Invariant($"      last GC heap       : {info.HeapSizeBytes / 1048576.0:F3} MB; fragmented={info.FragmentedBytes / 1048576.0:F3} MB; committed={info.TotalCommittedBytes / 1048576.0:F3} MB"));
        sb.AppendLine($"      GC collections     : gen0={GC.CollectionCount(0) - collectionBaseline[0]}, gen1={GC.CollectionCount(1) - collectionBaseline[1]}, gen2={GC.CollectionCount(2) - collectionBaseline[2]}");
        sb.AppendLine(FormattableString.Invariant($"      GC pause time      : {(GC.GetTotalPauseDuration() - pauseBaseline).TotalMilliseconds:F3} ms"));
        sb.AppendLine("      Scope: process-wide managed allocation; not attribution to HAR. Logging allocation is excluded between windows; logging time may still affect later frames.");
        sb.AppendLine();
        Write(sb.ToString());
        BeginMemoryWindow(); // exclude our string formatting and file write from the next allocation window
    }

    private static void AppendSnapshot(StringBuilder sb, string header, bool window, long curAllocBytes)
    {
        sb.AppendLine(header);
        for (int i = 0; i < PatchCount; i++)
        {
            long tT = window ? total[i]  - sTotal[i]  : total[i];
            long tF = window ? fast[i]   - sFast[i]   : fast[i];
            long tX = window ? thresh[i] - sThresh[i] : thresh[i];
            long tE = window ? exec[i]   - sExec[i]   : exec[i];
            long tK = window ? ticks[i]  - sTicks[i]  : ticks[i];

            if (window)
            {
                sTotal[i] = total[i]; sFast[i] = fast[i]; sThresh[i] = thresh[i];
                sExec[i] = exec[i]; sTicks[i] = ticks[i];
            }

            double ms    = tK * TickToMs;
            double avgMs = tT > 0 ? ms / tT : 0.0;
            double cutPct = tT > 0 ? (double)(tF + tX) / tT * 100.0 : 0.0;

            sb.AppendLine($"Patch: {Label[i]}");
            sb.AppendLine($"  Total Calls        : {tT:N0}");
            sb.AppendLine($"  Fast-Path Skips    : {tF:N0}");
            sb.AppendLine($"  Threshold Skips    : {tX:N0}");
            sb.AppendLine($"  Actual Executions  : {tE:N0}");
            sb.AppendLine($"  Cut-off rate       : {cutPct.ToString("0.00", CultureInfo.InvariantCulture)}%");
            sb.AppendLine($"  Time               : {ms.ToString("0.####", CultureInfo.InvariantCulture)} ms (avg {avgMs.ToString("0.#####", CultureInfo.InvariantCulture)} ms/call)");
            sb.AppendLine($"  [cumulative        : calls={total[i]:N0}, exec={exec[i]:N0}]");
        }

        // Shared A/B metric: HAR's expensive addon-regeneration REQUESTS actually made. Present in
        // BOTH modes (our optimized patches and the original HAR patch call the same method), so the
        // baseline vs optimized delta is exactly the heavy work the optimization eliminates.
        long wCalls = window ? regenCalls - sRegenCalls : regenCalls;
        long wTicks = window ? regenTicks - sRegenTicks : regenTicks;
        long wAllocated = window ? regenAllocatedBytes - sRegenAllocatedBytes : regenAllocatedBytes;
        if (window) sRegenAllocatedBytes = regenAllocatedBytes;
        if (window) { sRegenCalls = regenCalls; sRegenTicks = regenTicks; }
        double wMs = wTicks * TickToMs;
        sb.AppendLine($">>> HAR regen requests (RegenerateAddonGraphicsWithCondition), MODE: {Mode}");
        sb.AppendLine($"      window calls       : {wCalls:N0}   [{regenCalls:N0} cumulative]");
        sb.AppendLine($"      synchronous bytes  : {wAllocated:N0} (current thread; includes surrounding patches; excludes deferred graphics)");
        sb.AppendLine($"      window enqueue time: {wMs.ToString("0.####", CultureInfo.InvariantCulture)} ms (avg {(wCalls > 0 ? wMs / wCalls : 0.0).ToString("0.#####", CultureInfo.InvariantCulture)} ms/call)");

        // Memory (A/B): managed bytes allocated during this window (cumulative GC counter delta).
        // Directly shows the allocations the LINQ/Intersect path caused vs the 0-alloc Overlaps path.
        long winAlloc = curAllocBytes - allocBaseline;
        double mb = winAlloc / 1048576.0;
        string unit = winAlloc >= 1048576 ? $"{mb.ToString("0.00", CultureInfo.InvariantCulture)} MB"
                                          : $"{(winAlloc / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} KB";
        sb.AppendLine(">>> Memory (cumulative managed allocations)");
        sb.AppendLine($"      window allocated   : {unit}");
        sb.AppendLine($"      [process total     : {(curAllocBytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture)} MB]");
        if (memLines.Length > 0) sb.Append(memLines);
        if (modeLines.Length > 0) sb.Append(modeLines);
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static void Write(string text)
    {
        if (logPath == null) return;
        try { lock (ioLock) File.AppendAllText(logPath, text); }
        catch (Exception e) { LogNonBlocking("write", e); }
    }

    private static void LogNonBlocking(string where, Exception e)
    {
        try { Verse.Log.Warning($"<color=#5BC0FF>[Xenon]</color> {where} failed: {e.Message}"); } catch { }
    }
}
