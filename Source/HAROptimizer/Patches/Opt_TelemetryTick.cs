namespace HAROptimizer.Patches;

using HarmonyLib;
using Verse;

/// <summary>
/// Observes frame updates but emits windows based on actual simulation tick progress.
/// Paused frames do not advance the window; a new game resets the sampling baseline.
///
/// The whole postfix no-ops with a single inlined bool check when logging is disabled.
/// </summary>
[HarmonyPatch(typeof(Game), nameof(Game.UpdatePlay))]
public static class Opt_TelemetryTick
{
    private const int WindowTicks = 600;
    private static Game game;
    private static int startTick;
    private static int frames;

    public static void Patch(Harmony harmony)
    {
        var target = AccessTools.Method(typeof(Game), nameof(Game.UpdatePlay));
        if (target == null)
            return;
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(Opt_TelemetryTick), nameof(Postfix)));
    }

    public static void Postfix()
    {
        if (!PerfTelemetry.Enabled)
            return;
        var current = Current.Game;
        if (current?.tickManager == null) return;
        int now = current.tickManager.TicksGame;
        if (game != current || now < startTick)
        {
            game = current;
            startTick = now;
            frames = 0;
            PerfTelemetry.BeginMemoryWindow();
            return;
        }
        frames++;
        int elapsedTicks = now - startTick;
        if (elapsedTicks >= WindowTicks)
        {
            PerfTelemetry.DumpWindow(elapsedTicks, frames);
            startTick = now;
            frames = 0;
        }
    }
}
