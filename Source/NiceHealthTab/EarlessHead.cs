using System.Collections.Generic;
using NiceHealthTab;
using UnityEngine;

namespace NiceHealthTabPatch
{
    /// <summary>
    /// Makes copies of Nice Health Tab's doll head and outline without the human ears, for pawns
    /// whose own ears are drawn on the doll.
    /// </summary>
    /// <remarks>
    /// The doll head has its ears drawn into its texture (a bump on each side of the lower head, with
    /// the black border around it) and the doll outline repeats that bump. The copies are made from
    /// Nice Health Tab's own textures at runtime, so none of its art is shipped. The side edge at
    /// 45% of the head height is the plateau; in the rows below it whose edges stick out past the
    /// plateau, the side runs straight down to the middle of the ear and then slopes to where the
    /// jaw border is back to its normal width. Everything outside that line is cut, the border is
    /// redrawn at its plateau width by distance to the new edge, and dark pixels left inside (the
    /// old ear border and ear lines) take the plateau row's pixel. A texture whose shape does not
    /// look like that (Nice Health Tab changed its art) gets no copy, and the original is kept.
    /// </remarks>
    internal static class EarlessHead
    {
        private const byte Alpha = 32;
        private const int DarkLimit = 100;   // mean RGB of a border pixel
        private const int FillLimit = 200;   // mean RGB below which a pixel inside is an ear line

        private sealed class Cut
        {
            public Texture2D tex;
            public int firstRow, lastRow, plateau;   // texture rows counted from the top
        }

        private static readonly Dictionary<Texture2D, Cut> heads = new Dictionary<Texture2D, Cut>();
        private static readonly Dictionary<Texture2D, Texture2D> outlines = new Dictionary<Texture2D, Texture2D>();

        public static Texture2D Head(Texture2D head) => head == null ? null : HeadCut(head)?.tex;

        /// <summary>
        /// The doll outline without the ear bumps of <paramref name="headPart"/>, or null.
        /// </summary>
        public static Texture2D Outline(Texture2D outline, DollBodyPartDef headPart)
        {
            if (outline == null || headPart?.tex == null)
                return null;
            if (outlines.TryGetValue(outline, out Texture2D result))
                return result;
            Cut cut = HeadCut(headPart.tex);
            if (cut != null)
            {
                // The outline is centered on the doll origin, in doll units of one texture pixel.
                int top = Mathf.RoundToInt(outline.height / 2f + headPart.position.y - headPart.tex.height / 2f);
                const int pad = 8;   // the outline runs outside the head, so its bump reaches further
                var rows = new List<int>();
                for (int y = top + cut.firstRow - pad; y <= top + cut.lastRow + pad; y++)
                {
                    if (y > top + cut.plateau)
                        rows.Add(y);
                }
                result = Strip(outline, true, rows, top + cut.plateau)?.tex;
            }
            outlines[outline] = result;
            return result;
        }

        private static Cut HeadCut(Texture2D head)
        {
            if (!heads.TryGetValue(head, out Cut cut))
            {
                cut = Strip(head, false, null, -1);
                heads[head] = cut;
            }
            return cut;
        }

        private sealed class Image
        {
            public int w, h;
            public Color32[] px;

            // Rows are counted from the top; Unity stores them from the bottom.
            public Color32 this[int x, int y]
            {
                get => px[(h - 1 - y) * w + x];
                set => px[(h - 1 - y) * w + x] = value;
            }

            public bool Opaque(int x, int y) => this[x, y].a >= Alpha;

            public bool Edges(int y, out int left, out int right)
            {
                left = -1;
                right = -1;
                for (int x = 0; x < w; x++)
                {
                    if (!Opaque(x, y))
                        continue;
                    if (left < 0)
                        left = x;
                    right = x;
                }
                return left >= 0;
            }

            /// <summary>Dark opaque pixels from the row's edge inward: the head's black border.</summary>
            public int DarkRun(int y, bool fromLeft)
            {
                int n = 0;
                bool started = false;
                for (int i = 0; i < w; i++)
                {
                    int x = fromLeft ? i : w - 1 - i;
                    Color32 c = this[x, y];
                    if (c.a < Alpha)
                    {
                        if (started)
                            break;
                        continue;
                    }
                    started = true;
                    if ((c.r + c.g + c.b) / 3 > DarkLimit)
                        break;
                    n++;
                }
                return n;
            }
        }

        /// <summary>
        /// Cuts the ears off <paramref name="tex"/>. For the head the rows and plateau are found from
        /// its shape; for the outline they come from the head. Null when the shape does not fit.
        /// </summary>
        private static Cut Strip(Texture2D tex, bool outline, List<int> rowsHint, int plateauHint)
        {
            var img = new Image { w = tex.width, h = tex.height, px = TexturePixels.Read(tex) };
            int w = img.w, h = img.h;
            int plateau = plateauHint;
            if (rowsHint == null)
            {
                int top = -1, bottom = -1;
                for (int y = 0; y < h; y++)
                {
                    if (!img.Edges(y, out _, out _))
                        continue;
                    if (top < 0)
                        top = y;
                    bottom = y;
                }
                if (top < 0)
                    return null;
                plateau = top + (int)((bottom - top) * 0.45f);
            }
            if (plateau < 0 || plateau >= h || !img.Edges(plateau, out int lp, out int rp))
                return null;

            var rows = new List<int>();
            if (rowsHint == null)
            {
                int widest = 0;
                for (int y = plateau + 1; y < h; y++)
                {
                    if (img.Edges(y, out int l, out int r) && (l < lp || r > rp))
                    {
                        rows.Add(y);
                        widest = Mathf.Max(widest, Mathf.Max(lp - l, r - rp));
                    }
                }
                // Sanity: a few rows of ear that stick out a little, below a dark border.
                if (rows.Count < 3 || rows.Count > h / 3 || widest > w * 0.15f)
                    return null;
            }
            else
            {
                foreach (int y in rowsHint)
                {
                    if (y >= 0 && y < h)
                        rows.Add(y);
                }
                if (rows.Count == 0)
                    return null;
            }
            int bw = img.DarkRun(plateau, true);
            if (!outline && bw < 2)
                return null;
            Color32 border = img[Mathf.Min(w - 1, lp + bw / 2), plateau];

            Dictionary<int, float> leftLine = Target(img, rows, lp, bw, true);
            Dictionary<int, float> rightLine = Target(img, rows, rp, bw, false);
            var inside = new bool[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                    inside[y * w + x] = img.Opaque(x, y);
            }
            var cut = new bool[w * h];
            int firstRow = int.MaxValue, lastRow = int.MinValue;
            foreach (int y in Rows(leftLine, rightLine))
            {
                firstRow = Mathf.Min(firstRow, y);
                lastRow = Mathf.Max(lastRow, y);
                for (int x = 0; x < w; x++)
                {
                    if (!inside[y * w + x])
                        continue;
                    if ((leftLine.TryGetValue(y, out float l) && x < l) || (rightLine.TryGetValue(y, out float r) && x > r))
                    {
                        inside[y * w + x] = false;
                        cut[y * w + x] = true;
                    }
                }
            }

            var src = (Color32[])img.px.Clone();
            Color32 Src(int x, int y) => src[(h - 1 - y) * w + x];
            int reach = bw + 2;
            int y0 = Mathf.Max(0, firstRow - bw - 2), y1 = Mathf.Min(h - 1, lastRow + bw + 2);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (cut[y * w + x])
                    {
                        img[x, y] = new Color32(0, 0, 0, 0);
                        continue;
                    }
                    if (!inside[y * w + x] || (x >= lp + bw * 3 && x <= rp - bw * 3))
                        continue;
                    float d = DistanceOutside(inside, w, h, x, y, reach);
                    if (outline)
                    {
                        img[x, y] = d <= bw ? border : new Color32(0, 0, 0, 0);
                    }
                    else if (d <= bw)
                    {
                        img[x, y] = border;
                    }
                    else
                    {
                        Color32 c = Src(x, y);
                        if ((c.r + c.g + c.b) / 3 < FillLimit)
                            img[x, y] = Src(x, plateau);
                    }
                }
            }

            bool mips = tex.mipmapCount > 1;
            var result = new Texture2D(w, h, TextureFormat.RGBA32, mips)
            {
                filterMode = tex.filterMode,
                wrapMode = TextureWrapMode.Clamp,
                name = tex.name + "_earless",
            };
            result.SetPixels32(img.px);
            result.Apply(mips, true);
            return new Cut { tex = result, firstRow = firstRow, lastRow = lastRow, plateau = plateau };
        }

        /// <summary>
        /// The new edge per row: straight down from the plateau to the middle of the ear rows, then a
        /// slope to the first row below them where the border is back to its normal width.
        /// </summary>
        private static Dictionary<int, float> Target(Image img, List<int> rows, int start, int bw, bool left)
        {
            int first = rows[0], last = rows[rows.Count - 1];
            int end = last;
            while (end + 1 < img.h && img.Edges(end + 1, out _, out _) && img.DarkRun(end + 1, left) > bw * 1.4f)
                end++;
            end = Mathf.Min(img.h - 1, end + 1);
            float to = start;
            if (img.Edges(end, out int l, out int r))
                to = left ? l : r;
            int knee = (first + last) / 2;
            var line = new Dictionary<int, float>();
            for (int y = first; y <= end; y++)
                line[y] = y <= knee ? start : start + (to - start) * (y - knee) / Mathf.Max(1, end - knee);
            return line;
        }

        private static IEnumerable<int> Rows(Dictionary<int, float> a, Dictionary<int, float> b)
        {
            var all = new SortedSet<int>(a.Keys);
            all.UnionWith(b.Keys);
            return all;
        }

        private static float DistanceOutside(bool[] inside, int w, int h, int x, int y, int reach)
        {
            float best = reach + 1f;
            for (int dy = -reach; dy <= reach; dy++)
            {
                int yy = y + dy;
                for (int dx = -reach; dx <= reach; dx++)
                {
                    int xx = x + dx;
                    if (xx >= 0 && xx < w && yy >= 0 && yy < h && inside[yy * w + xx])
                        continue;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d < best)
                        best = d;
                }
            }
            return best;
        }
    }
}
