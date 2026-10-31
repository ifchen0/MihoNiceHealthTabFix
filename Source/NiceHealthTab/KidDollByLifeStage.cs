using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NiceHealthTab;
using RimWorld;
using Verse;

namespace NiceHealthTabPatch
{
    /// <summary>
    /// Gives the kid doll to pawns that are not adults yet, by life stage instead of by age.
    /// </summary>
    /// <remarks>
    /// Nice Health Tab picks its kid doll for any pawn younger than the top of its kid age range
    /// (15 by default). Vanilla teenagers of 13 and 14 already have adult bodies, and races that grow
    /// up sooner (Weazel is an adult at 8) got the kid doll with an adult body. Where Nice Health Tab
    /// reads the biological age for that choice, it now gets 0 for a child or baby life stage and
    /// int.MaxValue for an adult one, so the kid doll follows the life stage. The kid doll option and
    /// xenotypes that ignore age keep working as before.
    /// </remarks>
    [StaticConstructorOnStartup]
    public static class KidDollByLifeStage
    {
        private static readonly MethodInfo ageGetter = AccessTools.PropertyGetter(typeof(Pawn_AgeTracker), nameof(Pawn_AgeTracker.AgeBiologicalYears));

        static KidDollByLifeStage()
        {
            var harmony = new Harmony("ifchen0.nicehealthtabpatch");
            var transpiler = new HarmonyMethod(typeof(KidDollByLifeStage), nameof(Transpiler));
            foreach (MethodBase method in new MethodBase[]
            {
                AccessTools.Method(typeof(DollRenderContext), "GetMainDoll"),
                AccessTools.Method(typeof(DollDrawer), nameof(DollDrawer.DrawAllDolls)),
            })
            {
                try
                {
                    if (method == null)
                        throw new MissingMethodException("kid doll choice");
                    harmony.Patch(method, transpiler: transpiler);
                }
                catch (Exception e)
                {
                    Log.Warning("[NiceHealthTabPatch] Nice Health Tab kid doll choice not found; kid doll fix skipped there. " + e.Message);
                }
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction code in instructions)
            {
                if (code.Calls(ageGetter))
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(KidDollByLifeStage), nameof(KidDollAge))).WithLabels(code.labels);
                else
                    yield return code;
            }
        }

        private static int KidDollAge(Pawn_AgeTracker ageTracker) =>
            ageTracker.CurLifeStage != null && !ageTracker.CurLifeStage.developmentalStage.Adult() ? 0 : int.MaxValue;
    }
}
