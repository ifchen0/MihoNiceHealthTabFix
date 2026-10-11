using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace NiceHealthTabPatch
{
    /// <summary>
    /// Draws inactive buttons around Nice Health Tab's body doll with their hover color at all times.
    /// </summary>
    /// <remarks>
    /// Nice Health Tab draws inactive icon buttons at 25% alpha until hovered, which is hard to see on its
    /// dark background. Every load of Assets.IconInactiveColor in the button color setup is replaced
    /// with Assets.IconInactiveHoverColor. Active (accent colored) buttons are unchanged.
    /// </remarks>
    [StaticConstructorOnStartup]
    public static class ButtonColorPatch
    {
        private static FieldInfo inactiveColor;
        private static FieldInfo inactiveHoverColor;

        static ButtonColorPatch()
        {
            var assets = AccessTools.TypeByName("NiceHealthTab.Assets");
            var dollWidget = AccessTools.TypeByName("NiceHealthTab.DollWidget");
            inactiveColor = AccessTools.Field(assets, "IconInactiveColor");
            inactiveHoverColor = AccessTools.Field(assets, "IconInactiveHoverColor");
            if (dollWidget == null || inactiveColor == null || inactiveHoverColor == null)
            {
                Log.Warning("[NiceHealthTabPatch] Nice Health Tab button colors not found; button patch skipped.");
                return;
            }

            var harmony = new Harmony("ifchen0.nicehealthtabpatch");
            var transpiler = new HarmonyMethod(typeof(ButtonColorPatch), nameof(Transpiler));
            var targets = AccessTools.GetDeclaredMethods(dollWidget)
                .Where(m => m.Name == "ApplyButtonColors" || m.Name == "DrawVanillaToggleButton");
            foreach (MethodInfo method in targets)
                harmony.Patch(method, transpiler: transpiler);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.LoadsField(inactiveColor))
                    ins.operand = inactiveHoverColor;
                yield return ins;
            }
        }
    }
}
