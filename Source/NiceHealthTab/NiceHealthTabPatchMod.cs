using UnityEngine;
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
    /// Settings for the Nice Health Tab patches; only loaded while Nice Health Tab is active.
    /// </summary>
    public class NiceHealthTabPatchMod : Mod
    {
        public static NiceHealthTabPatchSettings Settings;

        public NiceHealthTabPatchMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<NiceHealthTabPatchSettings>();
        }

        public override string SettingsCategory() => Content.Name;

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);
            bool before = Settings.alienHeadOverlay;
            list.CheckboxLabeled("(Experimental) Draw alien race hair and ears on the health doll",
                ref Settings.alienHeadOverlay,
                "Draws the hair and ears that Humanoid Alien Races pawns wear as body addons (for example Miho) on the head of the health tab doll.");
            if (before != Settings.alienHeadOverlay)
                AlienHeadOverlay.ClearCache();
            list.End();
        }
    }
}
