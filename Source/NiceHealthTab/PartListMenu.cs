using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MilkyWay.Widgets;
using NiceHealthTab;
using UnityEngine;
using Verse;

namespace NiceHealthTabPatch
{
    /// <summary>
    /// Right-clicking a part in Nice Health Tab's part list opens the same surgery menu as the doll.
    /// </summary>
    /// <remarks>
    /// The list beside the doll groups hediffs into one block per body part, but only the doll opens
    /// surgery menus. A right click on a block opens Nice Health Tab's menu for that part (whole-body
    /// surgeries for the whole-body block).
    /// </remarks>
    [StaticConstructorOnStartup]
    public static class PartListMenu
    {
        private static FieldInfo blockPawn;
        private static FieldInfo blockPart;

        private static Thing thingForMedBills;

        static PartListMenu()
        {
            try
            {
                Type partBlock = AccessTools.Inner(typeof(BodyPartTableWidget), "PartBlock");
                blockPawn = AccessTools.Field(partBlock, "Pawn");
                blockPart = AccessTools.Field(partBlock, "Part");
                if (blockPawn == null || blockPart == null)
                    throw new MissingFieldException("BodyPartTableWidget part block fields");

                var harmony = new Harmony("ifchen0.nicehealthtabpatch");
                harmony.Patch(AccessTools.Method(typeof(NewTabViewRenderer), nameof(NewTabViewRenderer.DrawWindow)),
                    prefix: new HarmonyMethod(typeof(PartListMenu), nameof(DrawWindowPrefix)));
                harmony.Patch(AccessTools.Method(partBlock, "Draw"),
                    postfix: new HarmonyMethod(typeof(PartListMenu), nameof(PartBlockDrawPostfix)));
            }
            catch (Exception e)
            {
                Log.Warning("[NiceHealthTabPatch] Nice Health Tab part list not found; part list menu skipped. " + e.Message);
            }
        }

        private static void DrawWindowPrefix(bool allowOperations, Thing thingForMedBills)
        {
            PartListMenu.thingForMedBills = allowOperations ? thingForMedBills : null;
        }

        private static void PartBlockDrawPostfix(Widget __instance)
        {
            if (Event.current.type != EventType.MouseDown || Event.current.button != 1 || thingForMedBills == null)
                return;
            if (!__instance.Visible || !Mouse.IsOver(__instance.Geometry))
                return;
            if (!(blockPawn.GetValue(__instance) is Pawn pawn))
                return;
            List<FloatMenuOption> options = MissingChildPartMarker.PartSurgeryOptions(
                pawn, blockPart.GetValue(__instance) as BodyPartRecord, thingForMedBills);
            if (options == null || options.Count == 0)
                return;
            Find.WindowStack.Add(new FloatMenu(options));
            Event.current.Use();
        }
    }
}
