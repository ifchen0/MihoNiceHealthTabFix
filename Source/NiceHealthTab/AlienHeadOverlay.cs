using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NiceHealthTab;
using RimWorld;
using UnityEngine;
using Verse;

namespace NiceHealthTabPatch
{
    /// <summary>
    /// Draws the hair-colored head addons of Humanoid Alien Races pawns (hair, ears) on the doll head.
    /// Experimental; can be turned off in the mod settings.
    /// </summary>
    /// <remarks>
    /// Nice Health Tab only draws the hair and beard style icons, so a race that wears its hair and
    /// ears as HAR body addons (Miho) shows a bald doll. Only races whose HAR style settings turn
    /// vanilla hair styles off are handled, so a pawn wearing a vanilla style (bald included) is
    /// left to Nice Health Tab. The head addons with the "hair" color
    /// channel are taken from the pawn's render tree with their south-facing portrait size, offset
    /// and layer, composited into one cached texture and drawn with Nice Health Tab's hair material.
    /// The overlay rect was made for vanilla hair, so the race head (the head graphic plus the "skin"
    /// head addons) is fitted to a vanilla head by the width and center of their opaque bounds.
    /// Addon conditions (posture, drafted, hiding apparel) are ignored and addons sharing a texture
    /// are drawn once: Miho repeats each hair addon for standing and lying, and the doll shows hair
    /// under headgear like Nice Health Tab does.
    /// </remarks>
    [StaticConstructorOnStartup]
    public static class AlienHeadOverlay
    {
        private const string AddonPropsTypeName = "AlienRace.AlienPawnRenderNodeProperties_BodyAddon";
        // Vanilla hair and head meshes are this wide; the overlay rect covers one of them.
        private const float VanillaMeshSize = 1.5f;
        // The composite covers twice the overlay rect so long hair is not cut off.
        private const float CanvasScale = 2f;
        private const int CanvasPixels = 512;
        private const byte OpaqueAlpha = 32;

        private static readonly FieldInfo addonField;
        private static readonly PropertyInfo colorChannelProperty;
        private static readonly Type addonPropsType;
        private static readonly Action<DollRenderContext, Rect, Texture2D, Material, Color> drawHairClipped;
        private static readonly FieldInfo hairMaterialField;
        private static readonly FieldInfo outlineMaterialField;
        private static Material hairMaterial;
        private static Material outlineMaterial;
        private static bool failed;

        private sealed class Layer
        {
            public Texture2D tex;
            public Rect rect;   // world units around the head center, y up
            public float layer;
        }

        private sealed class CachedOverlay
        {
            public int key;
            public Texture2D tex;
        }

        private static readonly Dictionary<Pawn, CachedOverlay> overlays = new Dictionary<Pawn, CachedOverlay>();
        private static readonly Dictionary<Texture, Color32[]> pixelCache = new Dictionary<Texture, Color32[]>();
        private static readonly List<PawnRenderNode> hairNodes = new List<PawnRenderNode>();
        private static readonly Dictionary<ThingDef, bool> noVanillaHairRaces = new Dictionary<ThingDef, bool>();
        private static Game cacheGame;
        private static Rect? vanillaHeadBounds;

        static AlienHeadOverlay()
        {
            addonPropsType = GenTypes.GetTypeInAnyAssembly(AddonPropsTypeName);
            if (addonPropsType == null)
                return;
            try
            {
                addonField = AccessTools.Field(addonPropsType, "addon");
                colorChannelProperty = AccessTools.Property(addonField.FieldType, "ColorChannel");
                drawHairClipped = AccessTools.MethodDelegate<Action<DollRenderContext, Rect, Texture2D, Material, Color>>(
                    AccessTools.Method(typeof(DollPartRenderer), "DrawHairClipped"));
                Type assets = AccessTools.TypeByName("NiceHealthTab.Assets");
                hairMaterialField = AccessTools.Field(assets, "HairMaterial");
                outlineMaterialField = AccessTools.Field(assets, "OutlineMaterial");
                new Harmony("ifchen0.nicehealthtabpatch").Patch(AccessTools.Method(typeof(DollPartRenderer), "DrawHeadOverlay"),
                    postfix: new HarmonyMethod(typeof(AlienHeadOverlay), nameof(DrawHeadOverlayPostfix)));
            }
            catch (Exception e)
            {
                failed = true;
                Log.Warning("[NiceHealthTabPatch] Alien race head addons or Nice Health Tab head overlay not found; alien hair overlay skipped. " + e.Message);
            }
        }

        public static void ClearCache()
        {
            foreach (CachedOverlay overlay in overlays.Values)
            {
                if (overlay.tex != null)
                    UnityEngine.Object.Destroy(overlay.tex);
            }
            overlays.Clear();
            pixelCache.Clear();
        }

        private static void DrawHeadOverlayPostfix(DollRenderContext ctx, Pawn pawn, Rect overlayRect, DollBodyPartDef part, DollPartStatus status)
        {
            if (failed || pawn == null || !NiceHealthTabPatchMod.Settings.alienHeadOverlay || !NiceHediffTabSettings.ShowHair)
                return;
            try
            {
                Texture2D tex = OverlayFor(pawn);
                if (tex == null)
                    return;
                // Nice Health Tab's materials are made in its own startup constructor, so read them on first use.
                hairMaterial = hairMaterial ?? (Material)hairMaterialField.GetValue(null);
                outlineMaterial = outlineMaterial ?? (Material)outlineMaterialField.GetValue(null);
                Rect rect = new Rect(0f, 0f, overlayRect.width * CanvasScale, overlayRect.height * CanvasScale) { center = overlayRect.center };
                if (status != DollPartStatus.Missing)
                {
                    drawHairClipped(ctx, rect, tex, hairMaterial, part.PartColor);
                }
                else
                {
                    outlineMaterial.color = Color.white;
                    Graphics.DrawTexture(rect, tex, outlineMaterial);
                }
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error("[NiceHealthTabPatch] Alien hair overlay failed and is turned off until restart: " + e);
            }
        }

        /// <summary>
        /// Returns the cached composite of the pawn's hair addons, rebuilt when any addon graphic changes;
        /// null when the pawn has none.
        /// </summary>
        private static Texture2D OverlayFor(Pawn pawn)
        {
            if (cacheGame != Current.Game)
            {
                ClearCache();
                cacheGame = Current.Game;
            }
            if (!HasNoVanillaHair(pawn.def))
                return null;
            PawnRenderTree tree = pawn.Drawer?.renderer?.renderTree;
            if (tree == null || !tree.Resolved || !tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Head, out PawnRenderNode head))
                return null;

            hairNodes.Clear();
            CollectAddons(head, pawn, "hair", hairNodes);
            if (hairNodes.Count == 0)
                return null;
            int key = 17;
            unchecked
            {
                foreach (PawnRenderNode node in hairNodes)
                    key = key * 31 + node.GraphicFor(pawn).GetHashCode();
                key = key * 31 + (pawn.story?.headType?.GetHashCode() ?? 0);
                key = key * 31 + (pawn.story?.bodyType?.GetHashCode() ?? 0);
            }
            if (overlays.TryGetValue(pawn, out CachedOverlay cached) && cached.key == key)
                return cached.tex;

            if (cached?.tex != null)
                UnityEngine.Object.Destroy(cached.tex);
            Texture2D tex = Compose(pawn, head, hairNodes);
            overlays[pawn] = new CachedOverlay { key = key, tex = tex };
            return tex;
        }

        /// <summary>
        /// Whether the race is a HAR race with <c>hasStyle</c> off for <see cref="HairDef"/>.
        /// </summary>
        private static bool HasNoVanillaHair(ThingDef race)
        {
            if (noVanillaHairRaces.TryGetValue(race, out bool result))
                return result;
            object alienRace = AccessTools.Field(race.GetType(), "alienRace")?.GetValue(race);
            object styleSettings = alienRace == null ? null : AccessTools.Field(alienRace.GetType(), "styleSettings")?.GetValue(alienRace);
            object hairStyle = styleSettings is System.Collections.IDictionary dict && dict.Contains(typeof(HairDef)) ? dict[typeof(HairDef)] : null;
            result = hairStyle != null && AccessTools.Field(hairStyle.GetType(), "hasStyle")?.GetValue(hairStyle) is false;
            noVanillaHairRaces[race] = result;
            return result;
        }

        private static void CollectAddons(PawnRenderNode node, Pawn pawn, string colorChannel, List<PawnRenderNode> result)
        {
            if (node.children == null)
                return;
            foreach (PawnRenderNode child in node.children)
            {
                if (addonPropsType.IsInstanceOfType(child.Props)
                    && colorChannelProperty.GetValue(addonField.GetValue(child.Props)) as string == colorChannel
                    && child.GraphicFor(pawn) != null)
                    result.Add(child);
                CollectAddons(child, pawn, colorChannel, result);
            }
        }

        private static Texture2D Compose(Pawn pawn, PawnRenderNode head, List<PawnRenderNode> nodes)
        {
            PawnDrawParms parms = PawnDrawParms.DefaultFor(pawn);
            parms.facing = Rot4.South;
            parms.posture = PawnPosture.Standing;
            parms.flags |= PawnRenderFlags.Portrait;
            parms.flipHead = false;

            Rect? raceHead = RaceHeadBounds(pawn, head, parms, out float? faceLayer);
            var layers = new List<Layer>();
            var seen = new HashSet<Texture2D>();
            foreach (PawnRenderNode node in nodes)
            {
                Layer layer = LayerFor(node, pawn, parms);
                // Hair drawn behind the face (Miho's back hair) would cover the doll head.
                if (layer != null && !(layer.layer < faceLayer) && seen.Add(layer.tex))
                    layers.Add(layer);
            }
            if (layers.Count == 0)
                return null;
            layers.Sort((a, b) => a.layer.CompareTo(b.layer));

            // Fit the race head onto the vanilla head the overlay rect was made for.
            float scale = 1f;
            Vector2 shift = Vector2.zero;
            Rect? vanillaHead = VanillaHeadBounds();
            if (raceHead.HasValue && vanillaHead.HasValue && raceHead.Value.width > 0.01f)
            {
                scale = Mathf.Clamp(vanillaHead.Value.width / raceHead.Value.width, 0.5f, 3f);
                shift = vanillaHead.Value.center - raceHead.Value.center * scale;
            }

            var canvas = new Color[CanvasPixels * CanvasPixels];
            float canvasWorld = VanillaMeshSize * CanvasScale;
            foreach (Layer layer in layers)
            {
                Rect r = layer.rect;
                Rect fitted = Rect.MinMaxRect(r.xMin * scale + shift.x, r.yMin * scale + shift.y, r.xMax * scale + shift.x, r.yMax * scale + shift.y);
                Rect pixels = Rect.MinMaxRect(
                    (fitted.xMin / canvasWorld + 0.5f) * CanvasPixels, (fitted.yMin / canvasWorld + 0.5f) * CanvasPixels,
                    (fitted.xMax / canvasWorld + 0.5f) * CanvasPixels, (fitted.yMax / canvasWorld + 0.5f) * CanvasPixels);
                DrawLayer(canvas, Pixels(layer.tex), layer.tex.width, layer.tex.height, pixels);
            }

            var tex = new Texture2D(CanvasPixels, CanvasPixels, TextureFormat.RGBA32, true)
            {
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixels(canvas);
            tex.Apply(true, true);
            return tex;
        }

        /// <summary>
        /// The node's south texture and where the render tree draws it, relative to the head.
        /// </summary>
        private static Layer LayerFor(PawnRenderNode node, Pawn pawn, PawnDrawParms parms)
        {
            Texture2D tex = node.GraphicFor(pawn)?.MatSouth?.mainTexture as Texture2D;
            Mesh mesh = node.GetMesh(parms);
            if (tex == null || mesh == null || node.Worker == null)
                return null;
            Vector3 offset = node.Worker.OffsetFor(node, parms, out _);
            Vector3 scale = node.Worker.ScaleFor(node, parms);
            Vector3 size = mesh.bounds.size;
            float w = size.x * scale.x, h = size.z * scale.z;
            return new Layer
            {
                tex = tex,
                rect = new Rect(offset.x - w / 2f, offset.z - h / 2f, w, h),
                layer = node.Worker.LayerFor(node, parms) + offset.y,
            };
        }

        /// <summary>
        /// Opaque bounds of the race head in world units around the head center: the head graphic
        /// (blank for Miho) and the head addons with the "skin" color channel (Miho's face).
        /// <paramref name="faceLayer"/> is the top layer of those addons, null when there are none.
        /// </summary>
        private static Rect? RaceHeadBounds(Pawn pawn, PawnRenderNode head, PawnDrawParms parms, out float? faceLayer)
        {
            var nodes = new List<PawnRenderNode> { head };
            CollectAddons(head, pawn, "skin", nodes);
            Rect? bounds = null;
            faceLayer = null;
            foreach (PawnRenderNode node in nodes)
            {
                Layer layer = LayerFor(node, pawn, parms);
                if (layer == null)
                    continue;
                if (node == head)
                    layer.rect.center = Vector2.zero;
                else
                    faceLayer = Mathf.Max(faceLayer ?? layer.layer, layer.layer);
                Rect? opaque = OpaqueBounds(layer.tex, layer.rect);
                if (opaque.HasValue)
                    bounds = bounds.HasValue ? Union(bounds.Value, opaque.Value) : opaque.Value;
            }
            return bounds;
        }

        private static Rect? VanillaHeadBounds()
        {
            if (vanillaHeadBounds.HasValue)
                return vanillaHeadBounds;
            string path = DefDatabase<HeadTypeDef>.GetNamedSilentFail("Male_AverageNormal")?.graphicPath;
            Texture2D tex = path == null ? null : ContentFinder<Texture2D>.Get(path + "_south", false);
            if (tex == null)
                return null;
            float half = VanillaMeshSize / 2f;
            vanillaHeadBounds = OpaqueBounds(tex, new Rect(-half, -half, VanillaMeshSize, VanillaMeshSize));
            return vanillaHeadBounds;
        }

        /// <summary>
        /// The opaque part of a texture drawn in <paramref name="rect"/>, or null when it is fully transparent.
        /// </summary>
        private static Rect? OpaqueBounds(Texture2D tex, Rect rect)
        {
            Color32[] pixels = Pixels(tex);
            int w = tex.width, h = tex.height;
            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (pixels[y * w + x].a < OpaqueAlpha)
                        continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (maxX < 0)
                return null;
            return Rect.MinMaxRect(
                rect.xMin + rect.width * minX / w, rect.yMin + rect.height * minY / h,
                rect.xMin + rect.width * (maxX + 1) / w, rect.yMin + rect.height * (maxY + 1) / h);
        }

        /// <summary>
        /// Alpha-blends a texture over the canvas into the given canvas pixel rect, with bilinear sampling.
        /// </summary>
        private static void DrawLayer(Color[] canvas, Color32[] src, int srcW, int srcH, Rect dest)
        {
            int x0 = Mathf.Max(0, Mathf.FloorToInt(dest.xMin)), x1 = Mathf.Min(CanvasPixels, Mathf.CeilToInt(dest.xMax));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(dest.yMin)), y1 = Mathf.Min(CanvasPixels, Mathf.CeilToInt(dest.yMax));
            for (int y = y0; y < y1; y++)
            {
                float v = (y + 0.5f - dest.yMin) / dest.height;
                if (v < 0f || v > 1f)
                    continue;
                for (int x = x0; x < x1; x++)
                {
                    float u = (x + 0.5f - dest.xMin) / dest.width;
                    if (u < 0f || u > 1f)
                        continue;
                    Color s = Sample(src, srcW, srcH, u, v);
                    if (s.a <= 0f)
                        continue;
                    ref Color d = ref canvas[y * CanvasPixels + x];
                    float a = s.a + d.a * (1f - s.a);
                    Color blended = (s * s.a + d * d.a * (1f - s.a)) / a;
                    blended.a = a;
                    d = blended;
                }
            }
        }

        private static Color Sample(Color32[] src, int w, int h, float u, float v)
        {
            float fx = Mathf.Clamp(u * w - 0.5f, 0f, w - 1), fy = Mathf.Clamp(v * h - 0.5f, 0f, h - 1);
            int ix = (int)fx, iy = (int)fy;
            int ix1 = Mathf.Min(ix + 1, w - 1), iy1 = Mathf.Min(iy + 1, h - 1);
            float tx = fx - ix, ty = fy - iy;
            Color bottom = Color.Lerp(src[iy * w + ix], src[iy * w + ix1], tx);
            Color top = Color.Lerp(src[iy1 * w + ix], src[iy1 * w + ix1], tx);
            return Color.Lerp(bottom, top, ty);
        }

        private static Color32[] Pixels(Texture2D tex)
        {
            if (!pixelCache.TryGetValue(tex, out Color32[] pixels))
            {
                pixels = TexturePixels.Read(tex);
                pixelCache[tex] = pixels;
            }
            return pixels;
        }

        private static Rect Union(Rect a, Rect b) =>
            Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));
    }
}
