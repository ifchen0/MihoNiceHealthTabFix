using System.Collections.Generic;
using Verse;

namespace NiceHealthTabPatch
{
    /// <summary>
    /// Removes duplicate surgery recipes from Miho race defs so Nice Health Tab's operations list lays out correctly.
    /// </summary>
    /// <remarks>
    /// Miho enables HAR humanRecipeImport, which adds the race to the recipeUsers of Human surgery recipes,
    /// and also lists the same recipes in its own recipes. Vanilla ThingDef.AllRecipes joins both sources
    /// without deduplication, so every surgery appears twice. Nice Health Tab keys its animated operations
    /// list by recipe and body part, and duplicate keys make the same row slot get pulled to two positions.
    /// The cached list is deduplicated in place, keeping the first occurrence so order is preserved.
    /// </remarks>
    [StaticConstructorOnStartup]
    public static class MihoRecipeDedup
    {
        private const string MihoPackageId = "miho.fortifiedoutremer";

        static MihoRecipeDedup()
        {
            ModContentPack miho = LoadedModManager.RunningModsListForReading
                .Find(m => m.PackageIdPlayerFacing.EqualsIgnoreCase(MihoPackageId));
            if (miho == null)
                return;

            var seen = new HashSet<RecipeDef>();
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.modContentPack != miho || def.race == null)
                    continue;
                List<RecipeDef> all = def.AllRecipes;
                seen.Clear();
                int removed = all.RemoveAll(r => !seen.Add(r));
                if (removed > 0)
                {
                    Log.Message($"[NiceHealthTabPatch] Removed {removed} duplicate recipes from {def.defName}.");
                }
            }
        }
    }
}
