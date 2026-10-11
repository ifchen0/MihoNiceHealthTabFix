using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using NiceHealthTab;
using UnityEngine;
using Verse;

namespace NiceHealthTabPatch
{
    /// <summary>
    /// Marks a drawn doll part with a cross when one of its undrawn child parts is missing.
    /// </summary>
    /// <remarks>
    /// Nice Health Tab only colors parts that have their own doll image, so a missing ear, jaw,
    /// tongue or limb bone leaves the head, arm or leg looking intact. The part color is left
    /// alone (injury and prosthesis colors stay readable); a white cross with a black outline is
    /// drawn beside the part, on its outer side, after the whole doll. The cross and the part
    /// tooltip list the missing parts, and clicking the cross opens the surgery menu of the missing
    /// parts. Only the child parts listed in <see cref="MarkedParents"/> are handled.
    /// </remarks>
    [StaticConstructorOnStartup]
    public static class MissingChildPartMarker
    {
        private static readonly Dictionary<string, string> MarkedParents = new Dictionary<string, string>
        {
            { "Ear", "Head" },
            { "Jaw", "Head" },
            { "Tongue", "Head" },
            { "Humerus", "Arm" },
            { "Radius", "Arm" },
            { "Femur", "Leg" },
            { "Tibia", "Leg" },
        };

        private static readonly Color CrossFillColor = Color.white;
        private static readonly Color CrossOutlineColor = Color.black;
        private const float CrossSize = 20f;

        private static Texture2D crossTex;
        private static int crossTexPixels;

        private sealed class Cross
        {
            public Rect rect;
            public string tip;
            public List<BodyPartRecord> missingParts;
        }

        private static readonly List<Cross> pendingCrosses = new List<Cross>();
        private static readonly Dictionary<Doll, List<Cross>> lastDrawnCrosses = new Dictionary<Doll, List<Cross>>();
        private static readonly AccessTools.FieldRef<FloatMenu, List<FloatMenuOption>> floatMenuOptions =
            AccessTools.FieldRefAccess<FloatMenu, List<FloatMenuOption>>("options");
        private static readonly Dictionary<(Texture2D, Rect), Rect> opaqueBounds = new Dictionary<(Texture2D, Rect), Rect>();
        private static readonly HashSet<BodyPartRecord> markedThisDoll = new HashSet<BodyPartRecord>();

        static MissingChildPartMarker()
        {
            try
            {
                var harmony = new Harmony("ifchen0.nicehealthtabpatch");
                harmony.Patch(AccessTools.Method(typeof(DollRenderer), nameof(DollRenderer.DrawDoll)),
                    prefix: new HarmonyMethod(typeof(MissingChildPartMarker), nameof(DrawDollPrefix)),
                    postfix: new HarmonyMethod(typeof(MissingChildPartMarker), nameof(DrawDollPostfix)));
                harmony.Patch(AccessTools.Method(typeof(DollPartRenderer), nameof(DollPartRenderer.DrawPart)),
                    postfix: new HarmonyMethod(typeof(MissingChildPartMarker), nameof(DrawPartPostfix)));
            }
            catch (Exception e)
            {
                Log.Warning("[NiceHealthTabPatch] Nice Health Tab doll drawing not found; missing part marker skipped. " + e.Message);
            }
        }

        private static void DrawDollPrefix(DollRenderContext ctx, Doll doll, bool selectable)
        {
            pendingCrosses.Clear();
            markedThisDoll.Clear();
            // Handled before the doll parts so a cross over a part's hitbox does not open that part's menu too.
            if (!selectable || ctx?.Pawn == null || Event.current.type != EventType.MouseDown || ctx.ArmorMode
                || !lastDrawnCrosses.TryGetValue(doll, out List<Cross> crosses))
                return;
            foreach (Cross cross in crosses)
            {
                if (!Mouse.IsOver(cross.rect))
                    continue;
                OpenSurgeryMenu(ctx.Pawn, cross.missingParts, ctx.ThingForMedBills);
                Event.current.Use();
                return;
            }
        }

        private static void DrawDollPostfix(Doll doll)
        {
            if (pendingCrosses.Count == 0)
            {
                lastDrawnCrosses.Remove(doll);
                return;
            }
            lastDrawnCrosses[doll] = new List<Cross>(pendingCrosses);
            int pixels = Mathf.Max(8, Mathf.RoundToInt(CrossSize * Prefs.UIScale));
            if (crossTex == null || crossTexPixels != pixels)
            {
                if (crossTex != null)
                    UnityEngine.Object.Destroy(crossTex);
                crossTex = MakeCrossTexture(pixels);
                crossTexPixels = pixels;
            }
            Color saved = GUI.color;
            GUI.color = Color.white;
            foreach (Cross cross in pendingCrosses)
            {
                GUI.DrawTexture(cross.rect, crossTex);
                TooltipHandler.TipRegion(cross.rect, cross.tip);
            }
            GUI.color = saved;
            pendingCrosses.Clear();
        }

        /// <summary>
        /// Opens Nice Health Tab's surgery menu for the missing parts, merged into one menu when
        /// several parts are missing (surgery labels already name their part).
        /// </summary>
        /// <remarks>
        /// No surgery targets a limb bone, so a part without options falls back to its nearest
        /// ancestor that has some (femur to leg, humerus to shoulder), stopping below the torso,
        /// whose menu lists whole-body surgeries instead.
        /// </remarks>
        private static void OpenSurgeryMenu(Pawn pawn, List<BodyPartRecord> parts, Thing thingForMedBills)
        {
            var merged = new List<FloatMenuOption>();
            var visited = new HashSet<BodyPartRecord>();
            foreach (BodyPartRecord missing in parts)
            {
                for (BodyPartRecord part = missing; part != null && part.parent != null; part = part.parent)
                {
                    if (!visited.Add(part))
                        break;
                    List<FloatMenuOption> options = SurgeryOptions(pawn, part, thingForMedBills);
                    if (options == null)
                        continue;
                    merged.AddRange(options);
                    break;
                }
            }
            if (merged.Count > 0)
                Find.WindowStack.Add(new FloatMenu(merged));
        }

        /// <summary>
        /// Captures the options of the menu Nice Health Tab opens for a part, or null when it opens none.
        /// </summary>
        private static List<FloatMenuOption> SurgeryOptions(Pawn pawn, BodyPartRecord part, Thing thingForMedBills)
        {
            FloatMenu previous = Find.WindowStack.WindowOfType<FloatMenu>();
            DollOperations.OpenFloatMenuForPart(pawn, part, thingForMedBills);
            FloatMenu menu = Find.WindowStack.WindowOfType<FloatMenu>();
            if (menu == null || menu == previous)
                return null;
            Find.WindowStack.TryRemove(menu, false);
            return floatMenuOptions(menu);
        }

        /// <summary>
        /// Builds an anti-aliased white cross with a black outline along its strokes, at the exact
        /// screen pixel size it is drawn at.
        /// </summary>
        private static Texture2D MakeCrossTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            float half = size / 2f;
            float halfStroke = Mathf.Max(1f, size * 0.09f);
            float outline = Mathf.Max(1f, size * 0.06f);
            float reach = half - halfStroke - outline - 0.5f;
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - half, dy = y + 0.5f - half;
                    float d = Mathf.Min(SegmentDistance(dx, dy, reach), SegmentDistance(dx, -dy, reach));
                    float fill = Mathf.Clamp01(halfStroke - d + 0.5f);
                    Color c = Color.Lerp(CrossOutlineColor, CrossFillColor, fill);
                    c.a = Mathf.Clamp01(halfStroke + outline - d + 0.5f);
                    pixels[y * size + x] = c;
                }
            }
            tex.SetPixels(pixels);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>
        /// Distance from (x, y) to the diagonal segment from (-reach, -reach) to (reach, reach).
        /// </summary>
        private static float SegmentDistance(float x, float y, float reach)
        {
            float t = Mathf.Clamp((x + y) / 2f, -reach, reach);
            float ex = x - t, ey = y - t;
            return Mathf.Sqrt(ex * ex + ey * ey);
        }

        private static void DrawPartPostfix(bool __result, DollRenderContext ctx, DollBodyPartDef _part, Doll doll, Rect rectDoll, Vector2 extraOffset)
        {
            if (!__result || ctx == null || ctx.ArmorMode || ctx.FilterMode != DollFilterMode.None)
                return;
            if (_part is DollOrgan || _part is DollBone || _part is DollEye)
                return;
            HediffSet hediffSet = ctx.Pawn?.health?.hediffSet;
            BodyDef body = ctx.Pawn?.RaceProps?.body;
            if (hediffSet == null || body == null || ctx.Remap == null)
                return;
            int index = ctx.Remap.Get(_part.bodyPartId);
            if (index < 0)
                return;
            BodyPartRecord record = body.GetPartAtIndex(index);
            if (record == null || hediffSet.PartIsMissing(record))
                return;

            StringBuilder lines = null;
            List<BodyPartRecord> missingParts = null;
            foreach (Hediff_MissingPart missing in hediffSet.GetMissingPartsCommonAncestors())
            {
                if (MarkedParent(missing.Part) != record)
                    continue;
                if (lines == null)
                {
                    lines = new StringBuilder();
                    missingParts = new List<BodyPartRecord>();
                }
                missingParts.Add(missing.Part);
                lines.Append('\n').Append(missing.LabelCap).Append(": ").Append(missing.Part.LabelCap);
            }
            if (lines == null)
                return;

            if (ctx.HoveredPart == record && ctx.HoverTooltip != null && !ctx.HoverTooltip.EndsWith(lines.ToString()))
                ctx.HoverTooltip = ctx.HoverTooltip.TrimEnd('\n') + lines;

            if (!markedThisDoll.Add(record))
                return;
            Rect partRect = VisibleRect(_part, _part.GetRect(doll.BoundingBox, rectDoll, extraOffset));
            float size = CrossSize;
            float gap = size * 0.3f;
            float x = partRect.center.x >= rectDoll.center.x - 1f
                ? partRect.xMax + gap
                : partRect.xMin - gap - size;
            Rect crossRect = new Rect(SnapToPixel(x), SnapToPixel(partRect.center.y - size / 2f), size, size);
            pendingCrosses.Add(new Cross { rect = crossRect, tip = record.LabelCap + lines, missingParts = missingParts });
        }

        /// <summary>
        /// Shrinks a part's draw rect to the opaque pixels of its image, so the cross sits next to
        /// the visible part instead of the transparent padding around it.
        /// </summary>
        private static Rect VisibleRect(DollBodyPartDef part, Rect drawRect)
        {
            part.GetSource(out Texture2D tex, out Rect uv);
            if (tex == null)
                return drawRect;
            if (!opaqueBounds.TryGetValue((tex, uv), out Rect bounds))
            {
                bounds = FindOpaqueBounds(tex, uv);
                opaqueBounds[(tex, uv)] = bounds;
            }
            float xMin = bounds.xMin, xMax = bounds.xMax;
            if (part.width < 0f)
            {
                xMin = 1f - bounds.xMax;
                xMax = 1f - bounds.xMin;
            }
            // Texture fractions start at the bottom; GUI rects start at the top.
            return Rect.MinMaxRect(
                drawRect.xMin + xMin * drawRect.width,
                drawRect.yMin + (1f - bounds.yMax) * drawRect.height,
                drawRect.xMin + xMax * drawRect.width,
                drawRect.yMin + (1f - bounds.yMin) * drawRect.height);
        }

        /// <summary>
        /// Returns the opaque area inside <paramref name="uv"/> as fractions of that area.
        /// </summary>
        /// <remarks>Game textures are not CPU-readable, so the texture is copied through a render texture once.</remarks>
        private static Rect FindOpaqueBounds(Texture2D tex, Rect uv)
        {
            int w = tex.width, h = tex.height;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            Texture2D copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                copy.Apply();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
            Color32[] pixels = copy.GetPixels32();
            UnityEngine.Object.Destroy(copy);

            int x0 = Mathf.FloorToInt(uv.xMin * w), x1 = Mathf.CeilToInt(uv.xMax * w);
            int y0 = Mathf.FloorToInt(uv.yMin * h), y1 = Mathf.CeilToInt(uv.yMax * h);
            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    if (pixels[y * w + x].a < 32)
                        continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (maxX < 0)
                return DollBodyPartDef.FullUv;
            float uw = x1 - x0, uh = y1 - y0;
            return Rect.MinMaxRect((minX - x0) / uw, (minY - y0) / uh, (maxX + 1 - x0) / uw, (maxY + 1 - y0) / uh);
        }

        private static float SnapToPixel(float v) => Mathf.Round(v * Prefs.UIScale) / Prefs.UIScale;

        private static BodyPartRecord MarkedParent(BodyPartRecord part)
        {
            if (part == null || !MarkedParents.TryGetValue(part.def.defName, out string parentDef))
                return null;
            for (BodyPartRecord p = part.parent; p != null; p = p.parent)
            {
                if (p.def.defName == parentDef)
                    return p;
            }
            return null;
        }
    }
}
