using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using NiceHealthTab;
using UnityEngine;
using Verse;

namespace NiceHealthTabPatch
{
    /// <summary>
    /// Scales the doll head to the pawn's head-to-body proportion while the doll is drawn.
    /// Experimental; off by default.
    /// </summary>
    /// <remarks>
    /// Nice Health Tab draws every pawn with adult proportions: its kid doll has the head of an adult
    /// doll, while a vanilla child's head is far bigger next to its body, and races differ too (Miho's
    /// head is smaller). The pawn's proportion is its race head width over its chin-to-feet span
    /// (<see cref="AlienAddonOverlay.HeadRatio"/>); the doll is made for a vanilla adult, so the
    /// reference is the vanilla adult of the pawn's body type, or the average of the adult male and
    /// female for the kid doll and other body types. The head scale is the square root of the pawn's
    /// proportion over the reference: RimWorld's chibi pawns exaggerate the difference, and the full
    /// ratio would turn children into bobbleheads.
    ///
    /// The scale is applied around the doll chin (the bottom of the head texture's opaque pixels) to
    /// every doll part, gene part and prosthesis whose center lies above it, by moving and resizing
    /// their defs; part hit boxes come from the same values, so hovering follows. The head overlay
    /// rect, the outline (its rows above the chin rebuilt at runtime) and the top of the doll
    /// bounding box follow too, so Nice Health Tab fits and centers the whole doll in its rect.
    /// Everything is put back after the doll is drawn.
    /// </remarks>
    internal static class DollProportions
    {
        // Race head width over chin-to-feet span of vanilla adult humans, south portrait, measured on
        // RimWorld 1.7 with nht_probe.py (averaged over head types).
        private static readonly Dictionary<string, float> AdultRatios = new Dictionary<string, float>
        {
            { "Male", 0.949f }, { "Female", 0.781f }, { "Thin", 0.929f }, { "Hulk", 0.706f }, { "Fat", 0.805f },
        };
        private const float AverageAdultRatio = 0.865f;
        private const float MinScale = 0.7f, MaxScale = 1.6f;
        private const float ScaleStep = 0.02f;   // fewer outline copies
        private const byte OpaqueAlpha = 32;
        private const int MaxCachedOutlines = 16;

        private static readonly Type cacheType = AccessTools.TypeByName("NiceHealthTab.OverlayGeneCache");
        private static readonly Dictionary<Doll, Layout> layouts = new Dictionary<Doll, Layout>();
        private static readonly Dictionary<(Texture2D, float, float), Texture2D> outlines = new Dictionary<(Texture2D, float, float), Texture2D>();
        private static bool failed;

        private sealed class Layout
        {
            public float chin, top, centerX;   // doll units, y down
            public List<DollBodyPartDef> parts;
        }

        internal struct PartState
        {
            public DollBodyPartDef def;
            public Vector2 position, notifyPosition;
            public float width, height;
        }

        public sealed class Swap
        {
            public HumanlikeDoll doll;
            public Texture2D outline;
            public Rect boundingBox;
            public Vector2 headOverlayOffset, headOverlayScale;
            public readonly List<PartState> parts = new List<PartState>();
        }

        /// <summary>
        /// Scales the doll head for the pawn; returns what to restore, or null when nothing changed.
        /// </summary>
        public static Swap Apply(Pawn pawn, HumanlikeDoll doll)
        {
            if (failed)
                return null;
            Swap swap = null;
            try
            {
                float scale = ScaleFor(pawn);
                if (Mathf.Abs(scale - 1f) < ScaleStep / 2f)
                    return null;
                Layout layout = LayoutFor(doll);
                if (layout == null)
                    return null;
                swap = new Swap
                {
                    doll = doll,
                    outline = doll.outlineTex,
                    boundingBox = doll.BoundingBox,
                    headOverlayOffset = doll.headOverlayOffset,
                    headOverlayScale = doll.headOverlayScale,
                };
                var pivot = new Vector2(layout.centerX, layout.chin);
                foreach (DollBodyPartDef part in layout.parts)
                {
                    swap.parts.Add(new PartState
                    {
                        def = part, position = part.position, notifyPosition = part.notifyPosition,
                        width = part.width, height = part.height,
                    });
                    part.position = pivot + (part.position - pivot) * scale;
                    part.notifyPosition *= scale;
                    part.width *= scale;
                    part.height *= scale;
                }
                doll.headOverlayOffset *= scale;
                doll.headOverlayScale *= scale;
                Rect box = doll.BoundingBox;
                box.yMin += (layout.chin - (layout.chin - layout.top) * scale) - layout.top;
                doll.BoundingBox = box;
                doll.outlineTex = ScaledOutline(doll.outlineTex, layout, scale);
                return swap;
            }
            catch (Exception e)
            {
                failed = true;
                Restore(swap);
                Log.Error("[NiceHealthTabPatch] Doll proportions failed and are turned off until restart: " + e);
                return null;
            }
        }

        public static void Restore(Swap swap)
        {
            if (swap == null)
                return;
            foreach (PartState state in swap.parts)
            {
                state.def.position = state.position;
                state.def.notifyPosition = state.notifyPosition;
                state.def.width = state.width;
                state.def.height = state.height;
            }
            swap.doll.headOverlayOffset = swap.headOverlayOffset;
            swap.doll.headOverlayScale = swap.headOverlayScale;
            swap.doll.BoundingBox = swap.boundingBox;
            swap.doll.outlineTex = swap.outline;
        }

        private static float ScaleFor(Pawn pawn)
        {
            float? ratio = AlienAddonOverlay.HeadRatio(pawn);
            if (!ratio.HasValue)
                return 1f;
            string bodyType = pawn.story?.bodyType?.defName;
            bool adult = pawn.ageTracker?.CurLifeStage?.developmentalStage.Adult() ?? true;
            float reference = adult && bodyType != null && AdultRatios.TryGetValue(bodyType, out float r) ? r : AverageAdultRatio;
            float scale = Mathf.Clamp(Mathf.Sqrt(ratio.Value / reference), MinScale, MaxScale);
            return Mathf.Round(scale / ScaleStep) * ScaleStep;
        }

        private static Layout LayoutFor(HumanlikeDoll doll)
        {
            if (layouts.TryGetValue(doll, out Layout layout))
                return layout;
            layout = MakeLayout(doll);
            layouts[doll] = layout;
            return layout;
        }

        private static Layout MakeLayout(HumanlikeDoll doll)
        {
            DollBodyPartDef head = doll.headPart;
            if (head?.tex == null || head.UsesAtlas)
                return null;
            Color32[] pixels = TexturePixels.Read(head.tex);
            int w = head.tex.width, h = head.tex.height;
            int bottom = -1, top = -1;   // texture rows, bottom first
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (pixels[y * w + x].a < OpaqueAlpha)
                        continue;
                    if (bottom < 0)
                        bottom = y;
                    top = y;
                    break;
                }
            }
            if (bottom < 0)
                return null;
            Vector2 size = head.GetSourcePixelSize();
            float pxH = Mathf.Abs(head.height) * size.y / h;
            float rectTop = head.position.y - Mathf.Abs(head.height) * size.y / 2f;
            var layout = new Layout
            {
                top = rectTop + (h - 1 - top) * pxH,
                chin = rectTop + (h - bottom) * pxH,
                centerX = head.position.x,
                parts = new List<DollBodyPartDef>(),
            };

            var seen = new HashSet<DollBodyPartDef>();
            var names = new HashSet<string>();
            foreach (DollBodyPart part in doll.Parts)
            {
                names.Add(part.defName);
                if (part.position.y < layout.chin && seen.Add(part))
                    layout.parts.Add(part);
            }
            foreach (DollPartProsthesis part in DefDatabase<DollPartProsthesis>.AllDefs)
            {
                if (names.Contains(part.dollPartName) && part.position.y < layout.chin && seen.Add(part))
                    layout.parts.Add(part);
            }
            if (AccessTools.Field(cacheType, "DollsLayers")?.GetValue(null) is IDictionary dollsLayers
                && dollsLayers.Contains(doll.Type()))
            {
                object container = dollsLayers[doll.Type()];
                foreach (string list in new[] { "BehindLayer", "FrontLayer" })
                {
                    if (!(Traverse.Create(container).Field(list).GetValue() is IEnumerable genes))
                        continue;
                    foreach (DollBodyPartDef part in genes)
                    {
                        if (part.position.y < layout.chin && seen.Add(part))
                            layout.parts.Add(part);
                    }
                }
            }
            return layout;
        }

        /// <summary>
        /// The outline with its rows above the doll chin scaled around the head center; padded at
        /// both ends when the head grows past the top, so the texture stays centered on the doll.
        /// </summary>
        private static Texture2D ScaledOutline(Texture2D tex, Layout layout, float scale)
        {
            var key = (tex, scale, layout.chin);
            if (outlines.TryGetValue(key, out Texture2D result))
                return result;
            int w = tex.width, h = tex.height;
            Color32[] src = TexturePixels.Read(tex);
            // The outline is centered on the doll origin, one texture pixel per doll unit; rows from the top.
            float chin = layout.chin + h / 2f, cx = layout.centerX + w / 2f;
            float newTop = chin - (chin - (layout.top + h / 2f)) * scale;
            int pad = Mathf.Max(0, Mathf.CeilToInt(-newTop) + 2);
            int height = h + pad * 2;
            var dst = new Color32[w * height];
            for (int row = 0; row < height; row++)
            {
                int y = row - pad;   // row in the source, from the top
                for (int x = 0; x < w; x++)
                {
                    int sx = x, sy = y;
                    if (y + 0.5f < chin)
                    {
                        sx = Mathf.FloorToInt(cx + (x + 0.5f - cx) / scale);
                        sy = Mathf.FloorToInt(chin - (chin - (y + 0.5f)) / scale);
                    }
                    if (sx >= 0 && sx < w && sy >= 0 && sy < h)
                        dst[(height - 1 - row) * w + x] = src[(h - 1 - sy) * w + sx];
                }
            }
            if (outlines.Count >= MaxCachedOutlines)
            {
                foreach (Texture2D old in outlines.Values)
                    UnityEngine.Object.Destroy(old);
                outlines.Clear();
            }
            bool mips = tex.mipmapCount > 1;
            result = new Texture2D(w, height, TextureFormat.RGBA32, mips)
            {
                filterMode = tex.filterMode,
                wrapMode = TextureWrapMode.Clamp,
                name = tex.name + "_scaled",
            };
            result.SetPixels32(dst);
            result.Apply(mips, true);
            outlines[key] = result;
            return result;
        }
    }
}
