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
    /// Draws the hair-colored addons of Humanoid Alien Races pawns on the doll: hair and ears on the
    /// head, and the tail behind the body. Experimental; one switch in Nice Health Tab's settings.
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
    /// Addon conditions (posture, drafted, hiding apparel) are ignored and of the addons sharing a
    /// base path only the first is drawn: Miho repeats each hair addon for standing and lying (the
    /// two copies can roll different variants), and the doll shows hair under headgear like Nice
    /// Health Tab does.
    ///
    /// Tails are the hair-colored addons on the body. They are drawn before the doll, so the body and
    /// its outline cover the root, in the healthy part color (a race on the human body has no tail
    /// part). Their conditions are checked, since Miho swaps tails when drafted. The race body is
    /// fitted to the doll by the chin-to-feet span: the race chin is the bottom of the head bounds
    /// above, the doll chin the bottom of the vanilla head in the overlay rect, and the feet are the
    /// bottoms of the body graphic and of the doll outline. The tail size is not taken from the race,
    /// though: Miho's chibi tail is nearly as long as its body, and a child's even longer, which looks
    /// huge on the realistic doll. Tails are scaled around the center of the race body so their
    /// opaque height is <see cref="TailLength"/> of the doll's chin-to-feet span.
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
        private static readonly FieldInfo addonPathField;
        private static readonly PropertyInfo colorChannelProperty;
        private static readonly MethodInfo canDrawAddonMethod;
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
            public Texture2D tex;   // null when the pawn has nothing to draw
            public int lastUsedFrame;
        }

        // Composites keyed by appearance, so pawns that look alike share one; the least recently
        // used is dropped beyond MaxCachedOverlays.
        private const int MaxCachedOverlays = 32;
        private static readonly Dictionary<string, CachedOverlay> overlays = new Dictionary<string, CachedOverlay>();
        private static readonly System.Text.StringBuilder keyBuilder = new System.Text.StringBuilder();
        private sealed class TailInfo
        {
            public List<Layer> layers;   // rects in world units around the body center, y up
            public float chin, feet, centerX;
            public Vector2 bodyCenter;
            public float tailHeight;     // opaque height of the tail layers
        }

        private const int MaxCachedTails = 64;
        private const float TailLength = 0.5f;
        private static readonly Dictionary<string, TailInfo> tails = new Dictionary<string, TailInfo>();
        private static readonly Dictionary<Texture, float> outlineBottoms = new Dictionary<Texture, float>();
        private static readonly List<PawnRenderNode> tailNodes = new List<PawnRenderNode>();
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
                addonPathField = AccessTools.Field(addonField.FieldType, "path");
                colorChannelProperty = AccessTools.Property(addonField.FieldType, "ColorChannel");
                canDrawAddonMethod = AccessTools.Method(addonField.FieldType, "CanDrawAddon", new[] { typeof(Pawn) });
                drawHairClipped = AccessTools.MethodDelegate<Action<DollRenderContext, Rect, Texture2D, Material, Color>>(
                    AccessTools.Method(typeof(DollPartRenderer), "DrawHairClipped"));
                Type assets = AccessTools.TypeByName("NiceHealthTab.Assets");
                hairMaterialField = AccessTools.Field(assets, "HairMaterial");
                outlineMaterialField = AccessTools.Field(assets, "OutlineMaterial");
                var harmony = new Harmony("ifchen0.nicehealthtabpatch");
                harmony.Patch(AccessTools.Method(typeof(DollPartRenderer), "DrawHeadOverlay"),
                    postfix: new HarmonyMethod(typeof(AlienHeadOverlay), nameof(DrawHeadOverlayPostfix)));
                harmony.Patch(AccessTools.Method(typeof(DollRenderer), nameof(DollRenderer.DrawDoll)),
                    prefix: new HarmonyMethod(typeof(AlienHeadOverlay), nameof(DrawDollPrefix)));
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
            tails.Clear();
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

        private static void DrawDollPrefix(DollRenderContext ctx, Doll doll, Rect rect)
        {
            Pawn pawn = ctx?.Pawn;
            if (failed || pawn == null || !NiceHealthTabPatchMod.Settings.alienHeadOverlay
                || !(doll is HumanlikeDoll humanlike) || humanlike.headPart == null || doll.outlineTex == null)
                return;
            try
            {
                TailInfo info = TailFor(pawn);
                if (info == null)
                    return;
                Rect dollRect = doll.Fit(rect, out _);
                Rect overlayRect = humanlike.GetHeadOverlayRect(dollRect);
                Rect? vanillaHead = VanillaHeadBounds();
                if (!vanillaHead.HasValue || overlayRect.width <= 0f)
                    return;
                float chinY = overlayRect.center.y - vanillaHead.Value.yMin * overlayRect.width / VanillaMeshSize;
                Rect outlineRect = doll.GetOutlineRect(dollRect);
                float feetY = outlineRect.yMin + OutlineBottom(doll.outlineTex) * outlineRect.height;
                float scale = (feetY - chinY) / (info.chin - info.feet);
                if (scale <= 0f || float.IsNaN(scale))
                    return;
                hairMaterial = hairMaterial ?? (Material)hairMaterialField.GetValue(null);
                foreach (Layer layer in info.layers)
                {
                    float shrink = TailLength * (info.chin - info.feet) / info.tailHeight;
                    Rect r = new Rect(info.bodyCenter + (layer.rect.position - info.bodyCenter) * shrink, layer.rect.size * shrink);
                    var screen = new Rect(overlayRect.center.x + (r.xMin - info.centerX) * scale, chinY + (info.chin - r.yMax) * scale,
                        r.width * scale, r.height * scale);
                    drawHairClipped(ctx, screen, layer.tex, hairMaterial, Color.white);
                }
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error("[NiceHealthTabPatch] Alien tail overlay failed and the alien overlays are turned off until restart: " + e);
            }
        }

        /// <summary>
        /// The pawn's drawable tail addons and the race body measurements, cached by appearance; null
        /// when there is no tail.
        /// </summary>
        private static TailInfo TailFor(Pawn pawn)
        {
            if (cacheGame != Current.Game)
            {
                ClearCache();
                cacheGame = Current.Game;
            }
            if (!HasNoVanillaHair(pawn.def))
                return null;
            PawnRenderTree tree = pawn.Drawer?.renderer?.renderTree;
            if (tree == null || !tree.Resolved
                || !tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Body, out PawnRenderNode body)
                || !tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Head, out PawnRenderNode head))
                return null;

            tailNodes.Clear();
            foreach (PawnRenderNode child in body.children ?? Array.Empty<PawnRenderNode>())
            {
                if (child == head || !addonPropsType.IsInstanceOfType(child.Props))
                    continue;
                object addon = addonField.GetValue(child.Props);
                if (colorChannelProperty.GetValue(addon) as string == "hair" && child.GraphicFor(pawn) != null
                    && (bool)canDrawAddonMethod.Invoke(addon, new object[] { pawn }))
                    tailNodes.Add(child);
            }
            if (tailNodes.Count == 0)
                return null;
            keyBuilder.Clear().Append(pawn.def.defName);
            foreach (PawnRenderNode node in tailNodes)
                keyBuilder.Append('|').Append(node.GraphicFor(pawn).path);
            keyBuilder.Append('|').Append(pawn.story?.headType?.defName)
                .Append('|').Append(pawn.story?.bodyType?.defName)
                .Append('|').Append((int)pawn.gender)
                .Append('|').Append(HumanlikeMeshPoolUtility.HumanlikeHeadWidthForPawn(pawn).ToString("F3"));
            string key = keyBuilder.ToString();
            if (tails.TryGetValue(key, out TailInfo info))
                return info;

            info = MeasureTail(pawn, body, head, tailNodes);
            pixelCache.Clear();
            if (tails.Count >= MaxCachedTails)
                tails.Clear();
            tails[key] = info;
            return info;
        }

        private static TailInfo MeasureTail(Pawn pawn, PawnRenderNode body, PawnRenderNode head, List<PawnRenderNode> nodes)
        {
            PawnDrawParms parms = PawnDrawParms.DefaultFor(pawn);
            parms.facing = Rot4.South;
            parms.posture = PawnPosture.Standing;
            parms.flags |= PawnRenderFlags.Portrait;
            parms.flipHead = false;

            Layer bodyLayer = LayerFor(body, pawn, parms);
            Layer headLayer = LayerFor(head, pawn, parms);
            Rect? raceHead = RaceHeadBounds(pawn, head, parms, out _);
            if (bodyLayer == null || headLayer == null || !raceHead.HasValue)
                return null;
            bodyLayer.rect.center = Vector2.zero;
            Rect? bodyBounds = OpaqueBounds(bodyLayer.tex, bodyLayer.rect);
            if (!bodyBounds.HasValue)
                return null;
            Vector2 headCenter = headLayer.rect.center;
            var info = new TailInfo
            {
                layers = new List<Layer>(),
                chin = headCenter.y + raceHead.Value.yMin,
                feet = bodyBounds.Value.yMin,
                centerX = headCenter.x,
                bodyCenter = bodyBounds.Value.center,
            };
            if (info.chin - info.feet <= 0.01f)
                return null;
            Rect? tailBounds = null;
            foreach (PawnRenderNode node in nodes)
            {
                Layer layer = LayerFor(node, pawn, parms);
                Rect? opaque = layer == null ? null : OpaqueBounds(layer.tex, layer.rect);
                if (!opaque.HasValue)
                    continue;
                info.layers.Add(layer);
                tailBounds = tailBounds.HasValue ? Union(tailBounds.Value, opaque.Value) : opaque.Value;
            }
            if (!tailBounds.HasValue || tailBounds.Value.height <= 0.01f)
                return null;
            info.tailHeight = tailBounds.Value.height;
            info.layers.Sort((a, b) => a.layer.CompareTo(b.layer));
            return info;
        }

        /// <summary>
        /// The bottom of the doll outline's opaque pixels as a fraction from the top of its rect.
        /// </summary>
        private static float OutlineBottom(Texture2D outline)
        {
            if (!outlineBottoms.TryGetValue(outline, out float bottom))
            {
                Rect? bounds = OpaqueBounds(outline, new Rect(0f, 0f, 1f, 1f));
                pixelCache.Remove(outline);
                bottom = bounds.HasValue ? 1f - bounds.Value.yMin : 1f;
                outlineBottoms[outline] = bottom;
            }
            return bottom;
        }

        /// <summary>
        /// Returns the cached composite of the pawn's hair addons, shared by pawns of the same
        /// appearance; null when the pawn has none.
        /// </summary>
        /// <remarks>
        /// The key holds everything the composite depends on: the race, the addon graphics, head and
        /// body type, gender (HAR has female offsets) and head width (children).
        /// </remarks>
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
            keyBuilder.Clear().Append(pawn.def.defName);
            foreach (PawnRenderNode node in hairNodes)
                keyBuilder.Append('|').Append(node.GraphicFor(pawn).path);
            keyBuilder.Append('|').Append(pawn.story?.headType?.defName)
                .Append('|').Append(pawn.story?.bodyType?.defName)
                .Append('|').Append((int)pawn.gender)
                .Append('|').Append(HumanlikeMeshPoolUtility.HumanlikeHeadWidthForPawn(pawn).ToString("F3"));
            string key = keyBuilder.ToString();

            if (!overlays.TryGetValue(key, out CachedOverlay cached))
            {
                cached = new CachedOverlay { tex = Compose(pawn, head, hairNodes) };
                // Source pixels are only needed while composing; keeping them would grow with every style seen.
                pixelCache.Clear();
                overlays[key] = cached;
                if (overlays.Count > MaxCachedOverlays)
                    EvictLeastRecentlyUsed(key);
            }
            cached.lastUsedFrame = Time.frameCount;
            return cached.tex;
        }

        private static void EvictLeastRecentlyUsed(string keep)
        {
            string oldest = null;
            int oldestFrame = int.MaxValue;
            foreach (KeyValuePair<string, CachedOverlay> entry in overlays)
            {
                if (entry.Key != keep && entry.Value.lastUsedFrame < oldestFrame)
                {
                    oldest = entry.Key;
                    oldestFrame = entry.Value.lastUsedFrame;
                }
            }
            if (oldest == null)
                return;
            if (overlays[oldest].tex != null)
                UnityEngine.Object.Destroy(overlays[oldest].tex);
            overlays.Remove(oldest);
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
            var seen = new HashSet<string>();
            foreach (PawnRenderNode node in nodes)
            {
                Layer layer = LayerFor(node, pawn, parms);
                // Hair drawn behind the face (Miho's back hair) would cover the doll head.
                if (layer != null && !(layer.layer < faceLayer) && seen.Add(addonPathField.GetValue(addonField.GetValue(node.Props)) as string ?? layer.tex.name))
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
        /// <remarks>
        /// Only rows and columns with at least 1% of the texture size in opaque pixels count, so stray
        /// specks do not stretch the bounds (Miho's Head2 face has four near the left edge, which
        /// halved the fitted hair).
        /// </remarks>
        private static Rect? OpaqueBounds(Texture2D tex, Rect rect)
        {
            Color32[] pixels = Pixels(tex);
            int w = tex.width, h = tex.height;
            var columns = new int[w];
            var rows = new int[h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (pixels[y * w + x].a < OpaqueAlpha)
                        continue;
                    columns[x]++;
                    rows[y]++;
                }
            }
            if (!FindSpan(columns, Mathf.Max(2, h / 100), out int minX, out int maxX)
                || !FindSpan(rows, Mathf.Max(2, w / 100), out int minY, out int maxY))
                return null;
            return Rect.MinMaxRect(
                rect.xMin + rect.width * minX / w, rect.yMin + rect.height * minY / h,
                rect.xMin + rect.width * (maxX + 1) / w, rect.yMin + rect.height * (maxY + 1) / h);
        }

        private static bool FindSpan(int[] counts, int minCount, out int first, out int last)
        {
            first = Array.FindIndex(counts, c => c >= minCount);
            last = Array.FindLastIndex(counts, c => c >= minCount);
            return first >= 0;
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
