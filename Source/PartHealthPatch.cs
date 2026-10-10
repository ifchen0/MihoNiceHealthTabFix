using System;
using System.Collections.Generic;
using HarmonyLib;
using NiceHealthTab;
using Verse;

namespace NiceHealthTabPatch
{
    /// <summary>
    /// Makes Nice Health Tab agree with the vanilla part health when it would treat a part as destroyed.
    /// </summary>
    /// <remarks>
    /// Nice Health Tab computes part health with its own copy of HediffSet.GetPartHealth, so mods that
    /// patch the vanilla method are ignored. Just A Flesh Wound keeps a part at 1 HP (still healable)
    /// after it takes lethal damage, but Nice Health Tab sees 0 and draws the part as missing. When Nice
    /// Health Tab gets 0 for a part without a missing-part hediff, the vanilla value is used instead.
    /// Parts it already sees as damaged but alive are left alone.
    /// </remarks>
    [StaticConstructorOnStartup]
    public static class PartHealthPatch
    {
        static PartHealthPatch()
        {
            try
            {
                var harmony = new Harmony("ifchen0.nicehealthtabpatch");
                harmony.Patch(AccessTools.Method(typeof(HediffCache), nameof(HediffCache.GetPartHealth)),
                    postfix: new HarmonyMethod(typeof(PartHealthPatch), nameof(GetPartHealthPostfix)));
            }
            catch (Exception e)
            {
                Log.Warning("[NiceHealthTabPatch] Nice Health Tab part health not found; part health patch skipped. " + e.Message);
            }
        }

        private static void GetPartHealthPostfix(Pawn pawn, List<Hediff> AllPartHediffs, BodyPartRecord part, ref float __result)
        {
            if (__result > 0f || part == null || pawn?.health?.hediffSet == null)
                return;
            if (AllPartHediffs != null)
            {
                foreach (Hediff hediff in AllPartHediffs)
                {
                    if (hediff is Hediff_MissingPart)
                        return;
                }
            }
            __result = pawn.health.hediffSet.GetPartHealth(part);
        }
    }
}
