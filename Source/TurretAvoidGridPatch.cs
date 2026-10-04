using System.Collections.Generic;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace NiceHealthTabPatch
{
    /// <summary>
    /// Silences "Not enough squares to get to radius ..." when the enemy avoid grid is built around a very long range turret.
    /// </summary>
    /// <remarks>
    /// AvoidGrid.PrintAvoidGridAroundTurret asks GenRadial.NumCellsInRadius for the turret range plus 4. Miho's missile turret
    /// has range 200, beyond the radial pattern (radius about 80), so every rebuild of the avoid grid logs an error per turret.
    /// Vanilla then returns the whole pattern anyway; the replacement returns the same value without logging.
    /// </remarks>
    [StaticConstructorOnStartup]
    public static class TurretAvoidGridPatch
    {
        static TurretAvoidGridPatch()
        {
            var target = AccessTools.Method(typeof(AvoidGrid), "PrintAvoidGridAroundTurret");
            if (target == null)
            {
                Log.Warning("[NiceHealthTabPatch] AvoidGrid.PrintAvoidGridAroundTurret not found; turret avoid grid patch skipped.");
                return;
            }
            new Harmony("ifchen0.nicehealthtabpatch").Patch(target, transpiler: new HarmonyMethod(typeof(TurretAvoidGridPatch), nameof(Transpiler)));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = AccessTools.Method(typeof(GenRadial), nameof(GenRadial.NumCellsInRadius));
            var replacement = AccessTools.Method(typeof(TurretAvoidGridPatch), nameof(NumCellsInRadiusQuiet));
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.Calls(original))
                    ins.operand = replacement;
                yield return ins;
            }
        }

        public static int NumCellsInRadiusQuiet(float radius)
        {
            return radius >= GenRadial.MaxRadialPatternRadius ? GenRadial.RadialPattern.Length : GenRadial.NumCellsInRadius(radius);
        }
    }
}
