using System;
using System.Collections.Generic;
using HarmonyLib;
using NiceHealthTab;
using RimWorld;
using UnityEngine;
using Verse;

namespace NiceHealthTabPatch
{
    public class NiceHealthTabPatchSettings : ModSettings
    {
        public bool alienHeadOverlay = true;
        public bool dollProportions;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref alienHeadOverlay, "alienHeadOverlay", true);
            Scribe_Values.Look(ref dollProportions, "dollProportions", false);
        }
    }

    /// <summary>
    /// Holds the settings of the Nice Health Tab patches; only loaded while Nice Health Tab is active.
    /// </summary>
    /// <remarks>
    /// The mod has no settings page of its own: the options are listed at the end of the Common tab
    /// of Nice Health Tab's settings, by replacing that tab's <see cref="Listing.End"/> call. They are
    /// saved as soon as they change, since closing that window only saves Nice Health Tab's settings.
    /// The child age range slider is cut down to its lower end, the age below which the vanilla health
    /// tab is used: its upper end picked the kid doll, which now follows the life stage
    /// (<see cref="KidDollByLifeStage"/>).
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
                else if (ins.operand is System.Reflection.MethodInfo m && m.DeclaringType == typeof(Widgets) && m.Name == nameof(Widgets.FloatRange)
                    && m.GetParameters().Length == 11)
                    yield return new CodeInstruction(ins) { opcode = System.Reflection.Emit.OpCodes.Call, operand = AccessTools.Method(typeof(NiceHealthTabPatchMod), nameof(DrawChildAgeMin)) };
                else
                    yield return ins;
            }
        }

        // Same parameters as the Widgets.FloatRange call it replaces; only the lower end stays.
        private static void DrawChildAgeMin(Rect rect, int id, ref FloatRange range, float min, float max, string labelKey,
            ToStringStyle valueStyle, float minimumRange, GameFont font, Color? color, float roundTo)
        {
            float age = range.min;
            Widgets.HorizontalSlider(rect, ref age, new FloatRange(min, max), $"Vanilla health tab below age {age:F0}", 1f);
            range.min = age;
        }

        private static void DrawOptionsAndEnd(Listing listing)
        {
            if (listing is Listing_Standard list)
            {
                list.Gap(12f);
                bool before = Settings.alienHeadOverlay;
                list.CheckboxLabeled("(Experimental) Draw alien race hair, ears and tail on the doll",
                    ref Settings.alienHeadOverlay,
                    "Draws the ears and tail that Humanoid Alien Races pawns wear as body addons (for example Miho, Ratkin), and their hair when the race does not use vanilla hair styles. Races with their own Nice Health Tab doll are left alone. Added by Nice Health Tab & Miho Fixes.");
                if (before != Settings.alienHeadOverlay)
                {
                    AlienAddonOverlay.ClearCache();
                    Settings.Write();
                }
                before = Settings.dollProportions;
                list.CheckboxLabeled("(Experimental) Scale the doll head to the pawn's proportions",
                    ref Settings.dollProportions,
                    "Makes the doll head bigger or smaller to follow the pawn's head-to-body proportion, such as a child's bigger head or Miho's smaller one. The difference is toned down, since RimWorld pawns exaggerate it. Added by Nice Health Tab & Miho Fixes.");
                if (before != Settings.dollProportions)
                    Settings.Write();
            }
            listing.End();
        }
    }
}
