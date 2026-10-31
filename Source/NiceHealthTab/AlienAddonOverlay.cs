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
    /// Draws Humanoid Alien Races body addons on the doll: hair and ears on the head, and the tail
    /// behind the body. Experimental; one switch in Nice Health Tab's settings.
    /// </summary>
    /// <remarks>
    /// Nice Health Tab only draws the hair and beard style icons, so races that wear hair, ears or a
    /// tail as HAR body addons (Miho, Ratkin) lose them on the doll. Pawns whose xenotype has its own
    /// Nice Health Tab doll are skipped, since that doll already draws them.
    ///
    /// Head: the race head is the head graphic, or when that is blank (Miho) the "skin" head addons
    /// covering the head center (the face). The candidates are the head addons plus the drawable body
    /// addons lying wholly above the chin (Weazel's ears are body addons). Those whose opaque pixels lie mostly
    /// off the race head's opaque pixels extend its outline (ears, horns) and are drawn for any race;
    /// pixels rather than bounds, since ears on a round head sit in the corners of its bounds; nothing tells an ear from
    /// other headgear, so the rule is purely the outline. Addons with the "hair" color channel are
    /// hair and are drawn only for races whose HAR style settings turn vanilla hair styles off, so a
    /// pawn wearing a vanilla style (bald included) keeps Nice Health Tab's hair. Addons layered behind the face
    /// (Miho's back hair) are left out. The addons are taken from the pawn's render tree with their
    /// south-facing portrait size, offset and layer, composited into one cached texture and drawn with
    /// Nice Health Tab's hair material. The overlay rect was made for vanilla hair: when Nice Health
    /// Tab draws the pawn's vanilla hair there, the addons follow the same scale (the race hair mesh
    /// mapped onto the overlay rect), so ears keep their size next to that hair (Ratkin's small face
    /// would otherwise blow them up); otherwise the race head is fitted to a vanilla head by the
    /// width and center of their opaque bounds. Addon conditions
    /// (posture, drafted, hiding apparel) are ignored and of the addons sharing a base path only the
    /// first is drawn: Miho repeats each addon for standing and lying (the two copies can roll
    /// different variants), and the doll shows hair under headgear like Nice Health Tab does. When
    /// the pawn gets such outline addons, the doll head and outline are swapped for copies without
    /// the human ears (<see cref="EarlessHead"/>) while the doll is drawn, so it does not show four ears.
    ///
    /// Tail: the body addons that lie mostly outside the body graphic and reach below the chin (a raised
    /// tail can have its center above the chin, but not its bottom). They are
    /// drawn before the doll, so the body and its outline cover the root, in the healthy part color
    /// (a race on the human body has no tail part). Their conditions are checked as if the pawn were
    /// not drafted: Miho swaps to a raised tail when drafted, which sits behind the body and looks like
    /// a cape on the doll. The race body is fitted to the doll by the chin-to-feet span: the race chin
    /// is the bottom of the race head, the doll chin the bottom of the vanilla head in the overlay
    /// rect, and the feet are the bottoms of the body graphic and of the doll outline. That span only
    /// sets the scale and the horizontal place. Neither the tail size nor its height is taken from the
    /// race, since the race art is drawn for its own (often chibi) body: Miho's tail is nearly as long
    /// as its body, a child's even longer, and a child's short legs put its hips near the doll's knees.
    /// Tails are scaled around the center of the race body so their opaque height is
    /// <see cref="TailLength"/> of the doll's chin-to-feet span, and their opaque top is put at the
    /// doll's hips (<see cref="HipY"/>), so every tail on a doll takes the same height band. Guessing the root from the art (where the body
    /// covers the tail) moved tails up or down by how much of each tail the race's legs hide.
    /// </remarks>
    [StaticConstructorOnStartup]
    public static class AlienAddonOverlay
    {
        private const string AddonPropsTypeName = "AlienRace.AlienPawnRenderNodeProperties_BodyAddon";
        // Vanilla hair and head meshes are this wide; the overlay rect covers one of them.
        private const float VanillaMeshSize = 1.5f;
        // The composite covers twice the overlay rect so long hair is not cut off.
        private const float CanvasScale = 2f;
        private const int CanvasPixels = 512;
        private const byte OpaqueAlpha = 32;
        // Share of an addon's opaque bounds outside the head or body for it to count as an ear or tail.
        private const float OutsideShare = 0.5f;

        private static readonly FieldInfo addonField;
        private static readonly FieldInfo addonPathField;
        private static readonly PropertyInfo colorChannelProperty;
        private static readonly MethodInfo canDrawAddonMethod;
        private static readonly object[] canDrawArgs = new object[1];
        private static readonly AccessTools.FieldRef<Pawn_DraftController, bool> draftedField =
            AccessTools.FieldRefAccess<Pawn_DraftController, bool>("draftedInt");
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
            public bool ears;       // addons sticking out of the head outline are drawn
            public int lastUsedFrame;
        }

        // Composites keyed by appearance, so pawns that look alike share one; the least recently
        // used is dropped beyond MaxCachedOverlays.
        private const int MaxCachedOverlays = 32;
        private static readonly Dictionary<string, CachedOverlay> overlays = new Dictionary<string, CachedOverlay>();
        private static readonly System.Text.StringBuilder keyBuilder = new System.Text.StringBuilder();
        private sealed class TailInfo
        {
            public Texture2D tex;        // grayscale composite of the tail layers
            public Rect texRect;         // world units around the body center, y up
            public float chin, feet, centerX;
            public Vector2 bodyCenter;
            public float tailHeight;     // opaque height of the tail layers
            public float tailTop;        // opaque top of the tail layers
        }

        private const int MaxCachedTails = 64;
        private const float TailLength = 0.5f;
        private static readonly Dictionary<string, TailInfo> tails = new Dictionary<string, TailInfo>();
        private static readonly Dictionary<Texture, float> outlineBottoms = new Dictionary<Texture, float>();
        private static readonly Dictionary<Texture, float> opaqueTops = new Dictionary<Texture, float>();
        private static readonly Dictionary<Texture, float> opaqueBottoms = new Dictionary<Texture, float>();
        private static readonly List<PawnRenderNode> tailNodes = new List<PawnRenderNode>();
        private static readonly List<PawnRenderNode> faceKeyNodes = new List<PawnRenderNode>();
        private static readonly Dictionary<Texture, Color32[]> pixelCache = new Dictionary<Texture, Color32[]>();
        private static readonly List<PawnRenderNode> headAddonNodes = new List<PawnRenderNode>();
        private static readonly List<PawnRenderNode> bodyAddonNodes = new List<PawnRenderNode>();
        private static readonly Dictionary<ThingDef, bool> noVanillaHairRaces = new Dictionary<ThingDef, bool>();
        private static Game cacheGame;
        // OverlayFor and TailFor run several times per frame (every IMGUI event, prefix and postfix),
        // so the last pawn's results are kept for the rest of the frame.
        private static Pawn overlayMemoPawn, tailMemoPawn;
        private static int overlayMemoFrame = -1, tailMemoFrame = -1;
        private static CachedOverlay overlayMemo;
        private static TailInfo tailMemo;
        private static Rect? vanillaHeadBounds;

        static AlienAddonOverlay()
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
                    postfix: new HarmonyMethod(typeof(AlienAddonOverlay), nameof(DrawHeadOverlayPostfix)));
                harmony.Patch(AccessTools.Method(typeof(DollRenderer), nameof(DollRenderer.DrawDoll)),
                    prefix: new HarmonyMethod(typeof(AlienAddonOverlay), nameof(DrawDollPrefix)),
                    finalizer: new HarmonyMethod(typeof(AlienAddonOverlay), nameof(DrawDollFinalizer)));
            }
            catch (Exception e)
            {
                failed = true;
                Log.Warning("[NiceHealthTabPatch] Alien race addons or Nice Health Tab doll drawing not found; alien addon overlay skipped. " + e.Message);
            }
        }

        public static void ClearCache()
        {
            foreach (CachedOverlay overlay in overlays.Values)
                DestroyTexture(overlay.tex);
            overlays.Clear();
            ClearTails();
            pixelCache.Clear();
            overlayMemoPawn = tailMemoPawn = null;
        }

        private static void ClearTails()
        {
            foreach (TailInfo tail in tails.Values)
                DestroyTexture(tail?.tex);
            tails.Clear();
        }

        private static void DestroyTexture(Texture2D tex)
        {
            if (tex != null)
                UnityEngine.Object.Destroy(tex);
        }

        private static void ResetIfNewGame()
        {
            if (cacheGame == Current.Game)
                return;
            ClearCache();
            cacheGame = Current.Game;
        }

        /// <summary>
        /// The facing and pose every addon is measured in: south, standing, portrait.
        /// </summary>
        private static PawnDrawParms SouthPortraitParms(Pawn pawn)
        {
            PawnDrawParms parms = PawnDrawParms.DefaultFor(pawn);
            parms.facing = Rot4.South;
            parms.posture = PawnPosture.Standing;
            parms.flags |= PawnRenderFlags.Portrait;
            parms.flipHead = false;
            return parms;
        }

        private static void AppendGraphicPaths(List<PawnRenderNode> nodes, Pawn pawn)
        {
            foreach (PawnRenderNode node in nodes)
                keyBuilder.Append('|').Append(node.GraphicFor(pawn).path);
        }

        /// <summary>
        /// The pawn-dependent part of the cache keys: head and body type, gender (HAR has female
        /// offsets) and head width (children).
        /// </summary>
        private static void AppendAppearance(Pawn pawn)
        {
            keyBuilder.Append('|').Append(pawn.story?.headType?.defName)
                .Append('|').Append(pawn.story?.bodyType?.defName)
                .Append('|').Append((int)pawn.gender)
                .Append('|').Append(HumanlikeMeshPoolUtility.HumanlikeHeadWidthForPawn(pawn).ToString("F3"));
        }

        private static void DrawHeadOverlayPostfix(DollRenderContext ctx, Pawn pawn, Rect overlayRect, DollBodyPartDef part, DollPartStatus status)
        {
            if (failed || pawn == null || !NiceHealthTabPatchMod.Settings.alienHeadOverlay || !NiceHediffTabSettings.ShowHair
                || HasOwnDoll(ctx.MainDoll, pawn))
                return;
            try
            {
                Texture2D tex = OverlayFor(pawn)?.tex;
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
                Log.Error("[NiceHealthTabPatch] Alien addon overlay failed and is turned off until restart: " + e);
            }
        }

        /// <summary>
        /// The doll head and outline swapped for the earless copies while the doll is drawn.
        /// </summary>
        private struct HeadSwap
        {
            public DollBodyPartDef part;
            public Texture2D head;
            public Doll doll;
            public Texture2D outline;
        }

        private static void DrawDollPrefix(DollRenderContext ctx, Doll doll, Rect rect, out HeadSwap __state)
        {
            __state = default;
            Pawn pawn = ctx?.Pawn;
            if (failed || pawn == null || !NiceHealthTabPatchMod.Settings.alienHeadOverlay
                || !(doll is HumanlikeDoll humanlike) || humanlike.headPart == null || doll.outlineTex == null
                || HasOwnDoll(doll, pawn))
                return;
            try
            {
                SwapEarlessHead(pawn, humanlike, ref __state);
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
                float shrink = TailLength * (info.chin - info.feet) / info.tailHeight;
                Rect r = new Rect(info.bodyCenter + (info.texRect.position - info.bodyCenter) * shrink, info.texRect.size * shrink);
                float top = chinY + (info.chin - r.yMax) * scale;
                float? hipY = HipY(doll, dollRect);
                if (hipY.HasValue)
                {
                    float tailTop = info.bodyCenter.y + (info.tailTop - info.bodyCenter.y) * shrink;
                    top = hipY.Value - (r.yMax - tailTop) * scale;
                }
                var screen = new Rect(overlayRect.center.x + (r.xMin - info.centerX) * scale, top, r.width * scale, r.height * scale);
                drawHairClipped(ctx, screen, info.tex, hairMaterial, Color.white);
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error("[NiceHealthTabPatch] Alien tail overlay failed and the alien overlays are turned off until restart: " + e);
            }
        }

        private static void SwapEarlessHead(Pawn pawn, HumanlikeDoll doll, ref HeadSwap swap)
        {
            DollBodyPartDef part = doll.headPart;
            if (!NiceHediffTabSettings.ShowHair || part.UsesAtlas || part.tex == null)
                return;
            CachedOverlay overlay = OverlayFor(pawn);
            if (overlay?.tex == null || !overlay.ears)
                return;
            Texture2D head = EarlessHead.Head(part.tex);
            if (head == null)
                return;
            Texture2D outline = EarlessHead.Outline(doll.outlineTex, part);
            swap = new HeadSwap { part = part, head = part.tex, doll = doll, outline = doll.outlineTex };
            part.tex = head;
            if (outline != null)
                doll.outlineTex = outline;
        }

        private static void DrawDollFinalizer(HeadSwap __state)
        {
            if (__state.part != null)
                __state.part.tex = __state.head;
            if (__state.doll != null)
                __state.doll.outlineTex = __state.outline;
        }

        /// <summary>
        /// Whether Nice Health Tab picked a doll made for the pawn's xenotype rather than its default one.
        /// </summary>
        private static bool HasOwnDoll(Doll doll, Pawn pawn)
        {
            string xenotype = pawn.genes?.Xenotype?.defName;
            return doll != null && xenotype != null && doll.xenotype == xenotype;
        }

        /// <summary>
        /// The pawn's drawable tail addons and the race body measurements, cached by appearance; null
        /// when there is no tail.
        /// </summary>
        private static TailInfo TailFor(Pawn pawn)
        {
            if (pawn != tailMemoPawn || Time.frameCount != tailMemoFrame)
            {
                tailMemo = TailForUncached(pawn);
                tailMemoPawn = pawn;
                tailMemoFrame = Time.frameCount;
            }
            return tailMemo;
        }

        private static TailInfo TailForUncached(Pawn pawn)
        {
            ResetIfNewGame();
            PawnRenderTree tree = pawn.Drawer?.renderer?.renderTree;
            if (tree == null || !tree.Resolved
                || !tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Body, out PawnRenderNode body)
                || !tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Head, out PawnRenderNode head))
                return null;

            tailNodes.Clear();
            CollectDrawableBodyAddons(body, head, pawn, tailNodes);
            if (tailNodes.Count == 0)
                return null;
            keyBuilder.Clear().Append(pawn.def.defName);
            AppendGraphicPaths(tailNodes, pawn);
            // The chin comes from the face addons when the head graphic is blank (Miho).
            faceKeyNodes.Clear();
            CollectAddons(head, pawn, "skin", faceKeyNodes);
            keyBuilder.Append("|face");
            AppendGraphicPaths(faceKeyNodes, pawn);
            AppendAppearance(pawn);
            string key = keyBuilder.ToString();
            if (tails.TryGetValue(key, out TailInfo info))
                return info;

            info = MeasureTail(pawn, body, head, tailNodes);
            pixelCache.Clear();
            if (tails.Count >= MaxCachedTails)
                ClearTails();
            tails[key] = info;
            return info;
        }

        private static TailInfo MeasureTail(Pawn pawn, PawnRenderNode body, PawnRenderNode head, List<PawnRenderNode> nodes)
        {
            PawnDrawParms parms = SouthPortraitParms(pawn);

            Layer bodyLayer = LayerFor(body, pawn, parms);
            Layer headLayer = LayerFor(head, pawn, parms);
            Rect? raceHead = RaceHeadBounds(pawn, head, parms, out _, out _, out _);
            if (bodyLayer == null || headLayer == null || !raceHead.HasValue)
                return null;
            bodyLayer.rect.center = Vector2.zero;
            Rect? bodyBounds = OpaqueBounds(bodyLayer.tex, bodyLayer.rect);
            if (!bodyBounds.HasValue)
                return null;
            Vector2 headCenter = headLayer.rect.center;
            var info = new TailInfo
            {
                chin = headCenter.y + raceHead.Value.yMin,
                feet = bodyBounds.Value.yMin,
                centerX = headCenter.x,
                bodyCenter = bodyBounds.Value.center,
            };
            if (info.chin - info.feet <= 0.01f)
                return null;
            var layers = new List<Layer>();
            Rect? tailBounds = null;
            Rect? texRect = null;
            foreach (PawnRenderNode node in nodes)
            {
                Layer layer = LayerFor(node, pawn, parms);
                Rect? opaque = layer == null ? null : OpaqueBounds(layer.tex, layer.rect);
                // Addons wholly above the chin belong to the head (Weazel's ears, Miho's lying faces).
                bool tail = opaque.HasValue && opaque.Value.yMin < info.chin && OutsideAreaShare(opaque.Value, bodyBounds.Value) >= OutsideShare;
                if (!tail)
                    continue;
                layers.Add(layer);
                tailBounds = tailBounds.HasValue ? Union(tailBounds.Value, opaque.Value) : opaque.Value;
                texRect = texRect.HasValue ? Union(texRect.Value, layer.rect) : layer.rect;
            }
            if (!tailBounds.HasValue || tailBounds.Value.height <= 0.01f)
                return null;
            info.tailHeight = tailBounds.Value.height;
            info.tailTop = tailBounds.Value.yMax;
            info.texRect = texRect.Value;
            layers.Sort((a, b) => a.layer.CompareTo(b.layer));
            info.tex = Composite(layers, r => r, info.texRect);
            return info;
        }

        /// <summary>
        /// The doll's hips on screen: halfway between the top of its leg parts and its crotch (the
        /// bottom of its torso part); null without either.
        /// </summary>
        /// <remarks>
        /// Nice Health Tab's leg parts start at the hip joint, hidden under the torso, so their top
        /// alone is too high for a tail and the crotch too low.
        /// </remarks>
        private static float? HipY(Doll doll, Rect dollRect)
        {
            List<BodyPartRecord> humanParts = BodyDefOf.Human.AllParts;
            float? legTop = null, crotch = null;
            foreach (DollBodyPart part in doll.Parts)
            {
                if (part.bodyPartId < 0 || part.bodyPartId >= humanParts.Count || part.UsesAtlas || part.tex == null)
                    continue;
                BodyPartDef def = humanParts[part.bodyPartId].def;
                if (def != BodyPartDefOf.Leg && def != BodyPartDefOf.Torso)
                    continue;
                Rect rect = part.GetRect(doll.BoundingBox, dollRect, Vector2.zero);
                if (def == BodyPartDefOf.Torso)
                {
                    crotch = rect.yMin + OpaqueBottom(part.tex) * rect.height;
                }
                else
                {
                    float top = rect.yMin + OpaqueTop(part.tex) * rect.height;
                    legTop = legTop.HasValue ? Mathf.Min(legTop.Value, top) : top;
                }
            }
            return legTop.HasValue && crotch.HasValue ? (legTop.Value + crotch.Value) / 2f : (float?)null;
        }

        /// <summary>
        /// The top of a texture's opaque pixels as a fraction from the top of its rect.
        /// </summary>
        private static float OpaqueTop(Texture2D tex)
        {
            if (!opaqueTops.TryGetValue(tex, out float top))
            {
                Rect? bounds = OpaqueBounds(tex, new Rect(0f, 0f, 1f, 1f));
                pixelCache.Remove(tex);
                top = bounds.HasValue ? 1f - bounds.Value.yMax : 0f;
                opaqueTops[tex] = top;
            }
            return top;
        }

        /// <summary>
        /// The bottom of a texture's opaque pixels as a fraction from the top of its rect.
        /// </summary>
        private static float OpaqueBottom(Texture2D tex)
        {
            if (!opaqueBottoms.TryGetValue(tex, out float bottom))
            {
                Rect? bounds = OpaqueBounds(tex, new Rect(0f, 0f, 1f, 1f));
                pixelCache.Remove(tex);
                bottom = bounds.HasValue ? 1f - bounds.Value.yMin : 1f;
                opaqueBottoms[tex] = bottom;
            }
            return bottom;
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
        /// Returns the cached composite of the pawn's head addons, shared by pawns of the same
        /// appearance; null when the pawn has none.
        /// </summary>
        /// <remarks>
        /// The key holds everything the composite depends on: the race, the addon graphics, the hair
        /// style and <see cref="AppendAppearance"/>.
        /// </remarks>
        private static CachedOverlay OverlayFor(Pawn pawn)
        {
            if (pawn != overlayMemoPawn || Time.frameCount != overlayMemoFrame)
            {
                overlayMemo = OverlayForUncached(pawn);
                overlayMemoPawn = pawn;
                overlayMemoFrame = Time.frameCount;
            }
            if (overlayMemo != null)
                overlayMemo.lastUsedFrame = Time.frameCount;
            return overlayMemo;
        }

        private static CachedOverlay OverlayForUncached(Pawn pawn)
        {
            ResetIfNewGame();
            PawnRenderTree tree = pawn.Drawer?.renderer?.renderTree;
            if (tree == null || !tree.Resolved || !tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Head, out PawnRenderNode head))
                return null;

            headAddonNodes.Clear();
            CollectAddons(head, pawn, null, headAddonNodes);
            bodyAddonNodes.Clear();
            if (tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Body, out PawnRenderNode body))
                CollectDrawableBodyAddons(body, head, pawn, bodyAddonNodes);
            if (headAddonNodes.Count == 0 && bodyAddonNodes.Count == 0)
                return null;
            keyBuilder.Clear().Append(pawn.def.defName);
            AppendGraphicPaths(headAddonNodes, pawn);
            keyBuilder.Append("|body");
            AppendGraphicPaths(bodyAddonNodes, pawn);
            keyBuilder.Append('|').Append(pawn.story?.hairDef?.defName);
            AppendAppearance(pawn);
            string key = keyBuilder.ToString();

            if (!overlays.TryGetValue(key, out CachedOverlay cached))
            {
                cached = new CachedOverlay { tex = Compose(pawn, head, headAddonNodes, bodyAddonNodes, out bool ears), ears = ears };
                // Source pixels are only needed while composing; keeping them would grow with every style seen.
                pixelCache.Clear();
                overlays[key] = cached;
                if (overlays.Count > MaxCachedOverlays)
                    EvictLeastRecentlyUsed(key);
            }
            return cached;
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
            DestroyTexture(overlays[oldest].tex);
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

        /// <summary>
        /// Collects the body's own HAR addons (not the head's) that have a graphic and pass their
        /// conditions as if the pawn were not drafted.
        /// </summary>
        private static void CollectDrawableBodyAddons(PawnRenderNode body, PawnRenderNode head, Pawn pawn, List<PawnRenderNode> result)
        {
            // The field rather than the Drafted setter, which would end the pawn's jobs.
            Pawn_DraftController drafter = pawn.drafter;
            bool drafted = drafter != null && draftedField(drafter);
            if (drafted)
                draftedField(drafter) = false;
            try
            {
                foreach (PawnRenderNode child in body.children ?? Array.Empty<PawnRenderNode>())
                {
                    if (child == head || !addonPropsType.IsInstanceOfType(child.Props) || child.GraphicFor(pawn) == null)
                        continue;
                    canDrawArgs[0] = pawn;
                    if ((bool)canDrawAddonMethod.Invoke(addonField.GetValue(child.Props), canDrawArgs))
                        result.Add(child);
                }
            }
            finally
            {
                if (drafted)
                    draftedField(drafter) = true;
            }
        }

        /// <summary>
        /// Collects the HAR addons below <paramref name="node"/> with a graphic, of one color channel or any when null.
        /// </summary>
        private static void CollectAddons(PawnRenderNode node, Pawn pawn, string colorChannel, List<PawnRenderNode> result)
        {
            if (node.children == null)
                return;
            foreach (PawnRenderNode child in node.children)
            {
                if (addonPropsType.IsInstanceOfType(child.Props)
                    && (colorChannel == null || ColorChannel(child) == colorChannel)
                    && child.GraphicFor(pawn) != null)
                    result.Add(child);
                CollectAddons(child, pawn, colorChannel, result);
            }
        }

        /// <summary>
        /// Composites the pawn's drawn head addons; <paramref name="ears"/> tells whether any of them
        /// sticks out of the head outline.
        /// </summary>
        private static Texture2D Compose(Pawn pawn, PawnRenderNode head, List<PawnRenderNode> nodes, List<PawnRenderNode> bodyNodes, out bool ears)
        {
            ears = false;
            PawnDrawParms parms = SouthPortraitParms(pawn);

            Rect? raceHead = RaceHeadBounds(pawn, head, parms, out float? faceLayer, out List<PawnRenderNode> faceNodes, out List<Layer> headLayers);
            if (!raceHead.HasValue)
                return null;
            bool drawHair = HasNoVanillaHair(pawn.def);
            var layers = new List<Layer>();
            var seen = new HashSet<string>();
            foreach (PawnRenderNode node in nodes)
            {
                if (faceNodes.Contains(node))
                    continue;
                Layer layer = LayerFor(node, pawn, parms);
                // Addons behind the face (Miho's back hair) would cover the doll head.
                if (layer == null || layer.layer < faceLayer)
                    continue;
                Rect? opaque = OpaqueBounds(layer.tex, layer.rect);
                if (!opaque.HasValue)
                    continue;
                bool ear = OutsidePixelShare(layer, headLayers) >= OutsideShare;
                bool hair = drawHair && ColorChannel(node) == "hair";
                if ((ear || hair) && seen.Add(AddonPath(node) ?? layer.tex.name))
                {
                    layers.Add(layer);
                    ears |= ear;
                }
            }
            layers.Sort((a, b) => a.layer.CompareTo(b.layer));

            // Body addons placed at the head; their layers are in the body's frame, so they go behind.
            Vector3 headOffset = head.Worker.OffsetFor(head, parms, out _);
            var bodyLayers = new List<Layer>();
            foreach (PawnRenderNode node in bodyNodes)
            {
                Layer layer = LayerFor(node, pawn, parms);
                if (layer == null)
                    continue;
                layer.rect.position -= new Vector2(headOffset.x, headOffset.z);
                Rect? opaque = OpaqueBounds(layer.tex, layer.rect);
                // Only addons wholly above the chin; one reaching below it is a tail.
                if (!opaque.HasValue || opaque.Value.yMin <= raceHead.Value.yMin)
                    continue;
                if (OutsidePixelShare(layer, headLayers) >= OutsideShare && seen.Add(AddonPath(node) ?? layer.tex.name))
                {
                    bodyLayers.Add(layer);
                    ears = true;
                }
            }
            bodyLayers.Sort((a, b) => a.layer.CompareTo(b.layer));
            layers.InsertRange(0, bodyLayers);
            if (layers.Count == 0)
                return null;

            // Follow the vanilla hair Nice Health Tab draws, or fit the race head onto the vanilla head.
            float scale = 1f;
            Vector2 shift = Vector2.zero;
            Rect? vanillaHead = VanillaHeadBounds();
            Rect? hairMesh = drawHair ? null : HairMeshRect(head, parms);
            if (hairMesh.HasValue && hairMesh.Value.width > 0.01f)
            {
                scale = VanillaMeshSize / hairMesh.Value.width;
                shift = -hairMesh.Value.center * scale;
            }
            else if (vanillaHead.HasValue && raceHead.Value.width > 0.01f)
            {
                scale = Mathf.Clamp(vanillaHead.Value.width / raceHead.Value.width, 0.5f, 3f);
                shift = vanillaHead.Value.center - raceHead.Value.center * scale;
            }

            float canvasWorld = VanillaMeshSize * CanvasScale;
            return Composite(layers,
                r => Rect.MinMaxRect(r.xMin * scale + shift.x, r.yMin * scale + shift.y, r.xMax * scale + shift.x, r.yMax * scale + shift.y),
                new Rect(-canvasWorld / 2f, -canvasWorld / 2f, canvasWorld, canvasWorld));
        }

        /// <summary>
        /// Alpha-blends the layers, in order and in grayscale, into a texture covering
        /// <paramref name="canvasWorld"/>; <paramref name="place"/> maps a layer rect into that space.
        /// </summary>
        /// <remarks>
        /// Nice Health Tab's hair material tints the texture with the part color, which only works on
        /// gray art like vanilla hair; some races' ears and tails are colored (Ratkin, Weazel).
        /// </remarks>
        private static Texture2D Composite(List<Layer> layers, Func<Rect, Rect> place, Rect canvasWorld)
        {
            int w = CanvasPixels, h = CanvasPixels;
            if (canvasWorld.width > canvasWorld.height)
                h = Mathf.Max(1, Mathf.RoundToInt(CanvasPixels * canvasWorld.height / canvasWorld.width));
            else
                w = Mathf.Max(1, Mathf.RoundToInt(CanvasPixels * canvasWorld.width / canvasWorld.height));
            var canvas = new Color[w * h];
            foreach (Layer layer in layers)
            {
                Rect r = place(layer.rect);
                Rect pixels = Rect.MinMaxRect(
                    (r.xMin - canvasWorld.xMin) / canvasWorld.width * w, (r.yMin - canvasWorld.yMin) / canvasWorld.height * h,
                    (r.xMax - canvasWorld.xMin) / canvasWorld.width * w, (r.yMax - canvasWorld.yMin) / canvasWorld.height * h);
                DrawLayer(canvas, w, h, Pixels(layer.tex), layer.tex.width, layer.tex.height, pixels);
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true)
            {
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixels(canvas);
            tex.Apply(true, true);
            return tex;
        }

        private static string AddonPath(PawnRenderNode node) =>
            addonPathField.GetValue(addonField.GetValue(node.Props)) as string;

        /// <summary>
        /// Where the render tree draws the pawn's vanilla hair, relative to the head; null without a hair node.
        /// </summary>
        private static Rect? HairMeshRect(PawnRenderNode node, PawnDrawParms parms)
        {
            foreach (PawnRenderNode child in node.children ?? Array.Empty<PawnRenderNode>())
            {
                if (child is PawnRenderNode_Hair && child.Worker != null)
                {
                    Mesh mesh = child.GetMesh(parms);
                    if (mesh == null)
                        return null;
                    Vector3 offset = child.Worker.OffsetFor(child, parms, out _);
                    Vector3 scale = child.Worker.ScaleFor(child, parms);
                    float w = mesh.bounds.size.x * scale.x, h = mesh.bounds.size.z * scale.z;
                    return new Rect(offset.x - w / 2f, offset.z - h / 2f, w, h);
                }
                Rect? found = HairMeshRect(child, parms);
                if (found.HasValue)
                    return found;
            }
            return null;
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
        /// Opaque bounds of the race head in world units around the head center: the head graphic, or
        /// when that is blank (Miho) the "skin" head addons covering the head center (the face).
        /// <paramref name="faceLayer"/> is the top layer of those face addons, null when there are none.
        /// </summary>
        private static Rect? RaceHeadBounds(Pawn pawn, PawnRenderNode head, PawnDrawParms parms,
            out float? faceLayer, out List<PawnRenderNode> faceNodes, out List<Layer> headLayers)
        {
            faceLayer = null;
            faceNodes = new List<PawnRenderNode>();
            headLayers = new List<Layer>();
            Layer headLayer = LayerFor(head, pawn, parms);
            if (headLayer != null)
            {
                headLayer.rect.center = Vector2.zero;
                Rect? headBounds = OpaqueBounds(headLayer.tex, headLayer.rect);
                if (headBounds.HasValue)
                {
                    headLayers.Add(headLayer);
                    return headBounds;
                }
            }
            var skinNodes = new List<PawnRenderNode>();
            CollectAddons(head, pawn, "skin", skinNodes);
            Rect? bounds = null;
            foreach (PawnRenderNode node in skinNodes)
            {
                Layer layer = LayerFor(node, pawn, parms);
                Rect? opaque = layer == null ? null : OpaqueBounds(layer.tex, layer.rect);
                if (!opaque.HasValue || !opaque.Value.Contains(Vector2.zero))
                    continue;
                faceNodes.Add(node);
                headLayers.Add(layer);
                faceLayer = Mathf.Max(faceLayer ?? layer.layer, layer.layer);
                bounds = bounds.HasValue ? Union(bounds.Value, opaque.Value) : opaque.Value;
            }
            return bounds;
        }

        /// <summary>
        /// Share of the layer's opaque pixels that do not cover an opaque pixel of any head layer.
        /// </summary>
        private static float OutsidePixelShare(Layer layer, List<Layer> headLayers)
        {
            Color32[] pixels = Pixels(layer.tex);
            var headPixels = new Color32[headLayers.Count][];
            for (int i = 0; i < headLayers.Count; i++)
                headPixels[i] = Pixels(headLayers[i].tex);
            int w = layer.tex.width, h = layer.tex.height;
            int step = Mathf.Max(1, w / 128);
            int total = 0, outside = 0;
            for (int y = 0; y < h; y += step)
            {
                for (int x = 0; x < w; x += step)
                {
                    if (pixels[y * w + x].a < OpaqueAlpha)
                        continue;
                    total++;
                    var point = new Vector2(layer.rect.xMin + (x + 0.5f) / w * layer.rect.width, layer.rect.yMin + (y + 0.5f) / h * layer.rect.height);
                    bool covered = false;
                    for (int i = 0; i < headLayers.Count && !covered; i++)
                        covered = IsOpaqueAt(headLayers[i], headPixels[i], point);
                    if (!covered)
                        outside++;
                }
            }
            return total == 0 ? 0f : (float)outside / total;
        }

        private static bool IsOpaqueAt(Layer layer, Color32[] pixels, Vector2 point)
        {
            if (!layer.rect.Contains(point))
                return false;
            int w = layer.tex.width, h = layer.tex.height;
            int x = Mathf.Clamp((int)((point.x - layer.rect.xMin) / layer.rect.width * w), 0, w - 1);
            int y = Mathf.Clamp((int)((point.y - layer.rect.yMin) / layer.rect.height * h), 0, h - 1);
            return pixels[y * w + x].a >= OpaqueAlpha;
        }

        /// <summary>
        /// Share of <paramref name="r"/>'s area that lies outside <paramref name="inside"/>.
        /// </summary>
        private static float OutsideAreaShare(Rect r, Rect inside)
        {
            float area = r.width * r.height;
            if (area <= 0f)
                return 0f;
            float w = Mathf.Max(0f, Mathf.Min(r.xMax, inside.xMax) - Mathf.Max(r.xMin, inside.xMin));
            float h = Mathf.Max(0f, Mathf.Min(r.yMax, inside.yMax) - Mathf.Max(r.yMin, inside.yMin));
            return 1f - w * h / area;
        }

        private static string ColorChannel(PawnRenderNode node) =>
            colorChannelProperty.GetValue(addonField.GetValue(node.Props)) as string;

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
            first = -1;
            last = -1;
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] < minCount)
                    continue;
                if (first < 0)
                    first = i;
                last = i;
            }
            return first >= 0;
        }

        /// <summary>
        /// Alpha-blends a texture in grayscale over the canvas into the given canvas pixel rect, with bilinear sampling.
        /// </summary>
        private static void DrawLayer(Color[] canvas, int canvasW, int canvasH, Color32[] src, int srcW, int srcH, Rect dest)
        {
            int x0 = Mathf.Max(0, Mathf.FloorToInt(dest.xMin)), x1 = Mathf.Min(canvasW, Mathf.CeilToInt(dest.xMax));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(dest.yMin)), y1 = Mathf.Min(canvasH, Mathf.CeilToInt(dest.yMax));
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
                    float gray = s.grayscale;
                    s = new Color(gray, gray, gray, s.a);
                    ref Color d = ref canvas[y * canvasW + x];
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
