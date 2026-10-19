using System;
using System.Collections.Generic;

namespace AgsMcp.Editor.Tools
{
    /// <summary>A pixel position (mask or room coordinates).</summary>
    internal sealed class MaskPoint
    {
        public MaskPoint(int x, int y) { X = x; Y = y; }
        public int X { get; }
        public int Y { get; }
        public override string ToString() => X + "," + Y;
    }

    /// <summary>
    /// Where to click an area: its centroid and a short list of sample points inside it, best first. Pure, so it is
    /// unit-tested on tiny masks; game_run_interaction confirms each point with hover_name.
    /// </summary>
    internal static class MaskGeometry
    {
        /// <summary>The rounded mean position of the pixels of 'area' in mask[y, x], or null if it has none.</summary>
        public static MaskPoint Centroid(byte[,] mask, int area)
        {
            long sx = 0, sy = 0, n = 0;
            int h = mask.GetLength(0), w = mask.GetLength(1);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (mask[y, x] == area) { sx += x; sy += y; n++; }
            if (n == 0) return null;
            return new MaskPoint((int)Math.Round((double)sx / n, MidpointRounding.AwayFromZero),
                                 (int)Math.Round((double)sy / n, MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// Up to 'max' distinct pixels of 'area': the one nearest the centroid (the centroid itself when it is inside;
        /// a concave area's centroid can fall outside), then, for each cell of a 3x3 grid over the area's bounding box
        /// (cells nearest the centroid first), the area pixel nearest the cell's centre. Empty if the area has no pixels.
        /// </summary>
        public static List<MaskPoint> SamplePoints(byte[,] mask, int area, int max)
        {
            MaskPoint c = Centroid(mask, area);
            if (c == null) return new List<MaskPoint>();
            int h = mask.GetLength(0), w = mask.GetLength(1);
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (mask[y, x] == area)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
            return Samples((x, y) => mask[y, x] == area, minX, minY, maxX, maxY, c, max);
        }

        /// <summary>The same sampling over a filled rectangle (a sprite's box): its centre, then the 3x3 cell centres.</summary>
        public static List<MaskPoint> RectSamplePoints(int left, int top, int width, int height, int max)
        {
            if (width <= 0 || height <= 0) return new List<MaskPoint>();
            int right = left + width - 1, bottom = top + height - 1;
            var centre = new MaskPoint(left + (width - 1) / 2, top + (height - 1) / 2);
            return Samples((x, y) => true, left, top, right, bottom, centre, max);
        }

        private static List<MaskPoint> Samples(Func<int, int, bool> inside, int minX, int minY, int maxX, int maxY, MaskPoint centre, int max)
        {
            var result = new List<MaskPoint>();
            if (max <= 0) return result;
            Add(result, Nearest(inside, minX, minY, maxX, maxY, centre.X, centre.Y));

            // Cell centres of a 3x3 grid over the bounding box, nearest the centre first (ties in reading order).
            var cells = new List<MaskPoint[]>(); // { cell centre, cell min, cell max }
            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            for (int gy = 0; gy < 3; gy++)
                for (int gx = 0; gx < 3; gx++)
                {
                    int x0 = minX + bw * gx / 3, x1 = minX + bw * (gx + 1) / 3 - 1;
                    int y0 = minY + bh * gy / 3, y1 = minY + bh * (gy + 1) / 3 - 1;
                    if (x1 < x0 || y1 < y0) continue;
                    cells.Add(new[] { new MaskPoint((x0 + x1) / 2, (y0 + y1) / 2), new MaskPoint(x0, y0), new MaskPoint(x1, y1) });
                }
            var ordered = new List<MaskPoint[]>(cells);
            ordered.Sort((a, b) =>
            {
                int d = Dist2(a[0], centre.X, centre.Y).CompareTo(Dist2(b[0], centre.X, centre.Y));
                return d != 0 ? d : cells.IndexOf(a).CompareTo(cells.IndexOf(b));
            });
            foreach (MaskPoint[] cell in ordered)
            {
                if (result.Count >= max) break;
                Add(result, Nearest(inside, cell[1].X, cell[1].Y, cell[2].X, cell[2].Y, cell[0].X, cell[0].Y));
            }
            if (result.Count > max) result.RemoveRange(max, result.Count - max);
            return result;
        }

        /// <summary>The inside pixel within [minX..maxX, minY..maxY] nearest (tx, ty); ties go to the first in reading order.</summary>
        private static MaskPoint Nearest(Func<int, int, bool> inside, int minX, int minY, int maxX, int maxY, int tx, int ty)
        {
            MaskPoint best = null;
            long bestD = long.MaxValue;
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    if (!inside(x, y)) continue;
                    long d = (long)(x - tx) * (x - tx) + (long)(y - ty) * (y - ty);
                    if (d < bestD) { bestD = d; best = new MaskPoint(x, y); }
                }
            return best;
        }

        private static long Dist2(MaskPoint p, int x, int y) => (long)(p.X - x) * (p.X - x) + (long)(p.Y - y) * (p.Y - y);

        private static void Add(List<MaskPoint> list, MaskPoint p)
        {
            if (p == null) return;
            foreach (MaskPoint q in list) if (q.X == p.X && q.Y == p.Y) return;
            list.Add(p);
        }
    }
}
