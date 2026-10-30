using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NiceHealthTab;
using Verse;

namespace NiceHealthTabPatch
{
    /// <summary>
    /// Brings back Nice Health Tab's gene doll layers (Yttakin furry tail), which its RimWorld 1.7
    /// build leaves out.
    /// </summary>
    /// <remarks>
    /// Nice Health Tab draws a gene part when the pawn's render tree has a node of a gene listed in
    /// OverlayGeneCache.GeneHooks. Its 1.7 assembly no longer stores the genes in GeneHooks in
    /// DollGenePart.PrepareBodyPart, and DollRenderer.DrawDoll keeps its gene layer local at null
    /// instead of reading OverlayGeneCache.DollsLayers, so no gene part is ever drawn. The
    /// DollsLayers lookup is put back after the null assignment, and only once that worked are the
    /// missing GeneHooks entries added. Both steps leave the 1.6 assembly, or a fixed release, alone.
    /// Replacement gene parts (the pigskin nose) are not registered: 1.7 dropped the code that swaps
    /// them in, and an unregistered nose gene still reaches the doll head through Nice Health Tab's
    /// gene overlay fallback, which a registered one would skip.
    /// </remarks>
    [StaticConstructorOnStartup]
    public static class GeneHooksFix
    {
        private static readonly Type cacheType = AccessTools.TypeByName("NiceHealthTab.OverlayGeneCache");
        private static FieldInfo dollsLayersField;
        private static bool layerLookupRestored;

        static GeneHooksFix()
        {
            if (!ModsConfig.BiotechActive)
                return;
            var hooks = AccessTools.Field(cacheType, "GeneHooks")?.GetValue(null) as Dictionary<GeneDef, DollGenePart>;
            dollsLayersField = AccessTools.Field(cacheType, "DollsLayers");
            if (hooks == null || dollsLayersField == null)
            {
                Log.Warning("[NiceHealthTabPatch] Nice Health Tab gene part map not found; gene part fix skipped.");
                return;
            }
            var missing = new List<(GeneDef, DollGenePart)>();
            foreach (DollGenePart part in DefDatabase<DollBodyPartDef>.AllDefs.OfType<DollGenePart>())
            {
                GeneDef gene = part.geneDefName.NullOrEmpty() ? null : DefDatabase<GeneDef>.GetNamedSilentFail(part.geneDefName);
                if (gene != null && !part.replace && !hooks.ContainsKey(gene))
                    missing.Add((gene, part));
            }
            if (missing.Count == 0)
                return;
            try
            {
                new Harmony("ifchen0.nicehealthtabpatch").Patch(AccessTools.Method(typeof(DollRenderer), nameof(DollRenderer.DrawDoll)),
                    transpiler: new HarmonyMethod(typeof(GeneHooksFix), nameof(DrawDollTranspiler)));
            }
            catch (Exception e)
            {
                Log.Warning("[NiceHealthTabPatch] Nice Health Tab doll drawing could not be patched; gene part fix skipped. " + e.Message);
                return;
            }
            if (!layerLookupRestored)
            {
                Log.Warning("[NiceHealthTabPatch] Nice Health Tab gene layer lookup not found; gene part fix skipped.");
                return;
            }
            foreach ((GeneDef gene, DollGenePart part) in missing)
                hooks[gene] = part;
            Log.Message($"[NiceHealthTabPatch] Restored Nice Health Tab gene layers and registered {missing.Count} missing gene parts.");
        }

        private static IEnumerable<CodeInstruction> DrawDollTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            List<CodeInstruction> codes = instructions.ToList();
            if (codes.Any(c => c.LoadsField(dollsLayersField)))
            {
                layerLookupRestored = true;
                return codes;
            }
            Type containerType = dollsLayersField.FieldType.GetGenericArguments()[1];
            IList<LocalVariableInfo> locals = original.GetMethodBody()?.LocalVariables;
            for (int i = 0; i + 1 < codes.Count; i++)
            {
                if (codes[i].opcode != OpCodes.Ldnull || !codes[i + 1].IsStloc() || locals == null)
                    continue;
                // The 1.7 build stores into stloc.2, which carries no LocalBuilder operand.
                int index = codes[i + 1].LocalIndex();
                if (index < 0 || index >= locals.Count || locals[index].LocalType != containerType)
                    continue;
                codes.InsertRange(i + 2, new[]
                {
                    new CodeInstruction(OpCodes.Ldsfld, dollsLayersField),
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(Doll), nameof(Doll.Type))),
                    new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(dollsLayersField.FieldType, "Item")),
                    new CodeInstruction(codes[i + 1].opcode, codes[i + 1].operand),
                });
                layerLookupRestored = true;
                break;
            }
            return codes;
        }
    }
}
