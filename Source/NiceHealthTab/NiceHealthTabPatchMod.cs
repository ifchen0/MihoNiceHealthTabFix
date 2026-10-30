using System;
using System.Collections.Generic;
using HarmonyLib;
using NiceHealthTab;
using Verse;

namespace NiceHealthTabPatch
{
    public class NiceHealthTabPatchSettings : ModSettings
    {
        public bool alienHeadOverlay = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref alienHeadOverlay, "alienHeadOverlay", true);
        }
    }

    /// <summary>
    /// Holds the settings of the Nice Health Tab patches; only loaded while Nice Health Tab is active.
    /// </summary>
    /// <remarks>
    /// The mod has no settings page of its own: the options are listed at the end of the Common tab
    /// of Nice Health Tab's settings, by replacing that tab's <see cref="Listing.End"/> call. They are
    /// saved as soon as they change, since closing that window only saves Nice Health Tab's settings.
    /// </remarks>
    [StaticConstructorOnStartup]
    public class NiceHealthTabPatchMod : Mod
    {
        public static NiceHealthTabPatchSettings Settings;

        public NiceHealthTabPatchMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<NiceHealthTabPatchSettings>();
        }

        static NiceHealthTabPatchMod()
        {
            try
            {
                new Harmony("ifchen0.nicehealthtabpatch").Patch(AccessTools.Method(typeof(NiceHediffTabSettings), "DrawCommonTab"),
                    transpiler: new HarmonyMethod(typeof(NiceHealthTabPatchMod), nameof(CommonTabTranspiler)));
            }
            catch (Exception e)
            {
                Log.Warning("[NiceHealthTabPatch] Nice Health Tab settings page not found; patch options not shown. " + e.Message);
            }
        }

        private static IEnumerable<CodeInstruction> CommonTabTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var end = AccessTools.Method(typeof(Listing), nameof(Listing.End));
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.Calls(end))
                    yield return new CodeInstruction(ins) { opcode = System.Reflection.Emit.OpCodes.Call, operand = AccessTools.Method(typeof(NiceHealthTabPatchMod), nameof(DrawOptionsAndEnd)) };
                else
                    yield return ins;
            }
        }

        private static void DrawOptionsAndEnd(Listing listing)
        {
            if (listing is Listing_Standard list)
            {
                list.Gap(12f);
                bool before = Settings.alienHeadOverlay;
                list.CheckboxLabeled("(Experimental) Draw alien race hair, ears and tail on the doll",
                    ref Settings.alienHeadOverlay,
                    "Draws the hair, ears and tail of Humanoid Alien Races pawns whose race does not use vanilla hair styles (for example Miho). Added by Nice Health Tab & Miho Fixes.");
                if (before != Settings.alienHeadOverlay)
                {
                    AlienHeadOverlay.ClearCache();
                    Settings.Write();
                }
            }
            listing.End();
        }
    }
}
