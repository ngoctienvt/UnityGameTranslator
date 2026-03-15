using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// Converts TrueType glyph outlines to binary bitmaps using scanline rasterization
    /// with non-zero winding rule. Handles quadratic Bezier curves and implicit on-curve points.
    /// </summary>
    public static class GlyphRasterizer
    {
        /// <summary>
        /// Rasterize a glyph outline to a binary bitmap (255 = inside, 0 = outside).
        /// The bitmap includes sdfPadding pixels on each side for the SDF distance range.
        /// </summary>
        /// <param name="outline">Glyph outline from TtfParser</param>
        /// <param name="metrics">Font metrics</param>
        /// <param name="renderSize">Pixel size for rasterization</param>
        /// <param name="sdfPadding">Extra padding in pixels for SDF spread</param>
        /// <returns>RasterizedGlyph with binary bitmap, or null if glyph is empty</returns>
        public static RasterizedGlyph Rasterize(GlyphOutline outline, FontMetrics metrics,
            float renderSize, int sdfPadding)
        {
            if (outline == null) return null;

            float scale = renderSize / metrics.UnitsPerEm;

            // Empty glyph (space, etc.) — return with metrics but no bitmap
            if (outline.IsEmpty || outline.Contours == null || outline.Contours.Length == 0)
            {
                return new RasterizedGlyph
                {
                    Unicode = outline.Unicode,
                    Width = 0,
                    Height = 0,
                    Bitmap = null,
                    AdvanceWidth = outline.AdvanceWidth,
                    BearingX = outline.LeftSideBearing,
                    BearingY = outline.YMax
                };
            }

            // Calculate bitmap dimensions from glyph bounds
            float xMin = outline.XMin * scale;
            float yMin = outline.YMin * scale;
            float xMax = outline.XMax * scale;
            float yMax = outline.YMax * scale;

            int bitmapW = (int)Math.Ceiling(xMax - xMin) + sdfPadding * 2;
            int bitmapH = (int)Math.Ceiling(yMax - yMin) + sdfPadding * 2;

            // Minimum size
            if (bitmapW < 1) bitmapW = 1;
            if (bitmapH < 1) bitmapH = 1;

            // Safety limit
            if (bitmapW > 512 || bitmapH > 512)
            {
                bitmapW = Math.Min(bitmapW, 512);
                bitmapH = Math.Min(bitmapH, 512);
            }

            // Offset to transform font coordinates to bitmap coordinates
            // Font Y-axis points UP, bitmap Y-axis points DOWN → negate Y
            float offsetX = -xMin + sdfPadding;
            float offsetY = yMax + sdfPadding; // will be used as: bitmapY = -fontY + offsetY

            // Flatten all contours to line segments in bitmap space
            var edges = new List<Edge>();
            foreach (var contour in outline.Contours)
            {
                if (contour.Points == null || contour.Points.Length < 2)
                    continue;

                var flatPoints = FlattenContour(contour, scale, offsetX, offsetY);
                if (flatPoints.Count < 2)
                    continue;

                // Create edges from flattened points
                for (int i = 0; i < flatPoints.Count; i++)
                {
                    int next = (i + 1) % flatPoints.Count;
                    float y0 = flatPoints[i].Y;
                    float y1 = flatPoints[next].Y;

                    // Skip horizontal edges
                    if (Math.Abs(y1 - y0) < 0.001f)
                        continue;

                    edges.Add(new Edge
                    {
                        X0 = flatPoints[i].X,
                        Y0 = y0,
                        X1 = flatPoints[next].X,
                        Y1 = y1
                    });
                }
            }

            // Scanline fill with non-zero winding rule
            var bitmap = new byte[bitmapW * bitmapH];
            ScanlineFill(bitmap, bitmapW, bitmapH, edges);

            return new RasterizedGlyph
            {
                Unicode = outline.Unicode,
                Width = bitmapW,
                Height = bitmapH,
                Bitmap = bitmap,
                AdvanceWidth = outline.AdvanceWidth,
                BearingX = outline.LeftSideBearing,
                BearingY = outline.YMax
            };
        }

        #region Contour Flattening

        private struct FlatPoint
        {
            public float X, Y;
        }

        /// <summary>
        /// Flatten a contour into line segments.
        /// Handles both TrueType (quadratic) and CFF (cubic) Bezier curves.
        /// </summary>
        private static List<FlatPoint> FlattenContour(GlyphContour contour, float scale,
            float offsetX, float offsetY)
        {
            var result = new List<FlatPoint>();
            var pts = contour.Points;
            int n = pts.Length;
            if (n == 0) return result;

            // Detect if this is a CFF contour (has cubic control points)
            bool hasCubic = false;
            for (int i = 0; i < n; i++)
            {
                if (pts[i].IsCubic) { hasCubic = true; break; }
            }

            if (hasCubic)
                return FlattenCffContour(contour, scale, offsetX, offsetY);

            return FlattenTrueTypeContour(contour, scale, offsetX, offsetY);
        }

        /// <summary>
        /// Flatten a CFF contour (cubic Bezier curves).
        /// CFF contours are sequential: on-curve, [cubic, cubic, on-curve]*, ...
        /// </summary>
        private static List<FlatPoint> FlattenCffContour(GlyphContour contour, float scale,
            float offsetX, float offsetY)
        {
            var result = new List<FlatPoint>();
            var pts = contour.Points;
            int n = pts.Length;
            if (n == 0) return result;

            float curX = pts[0].X * scale + offsetX;
            float curY = pts[0].Y * -scale + offsetY;
            result.Add(new FlatPoint { X = curX, Y = curY });

            int i = 1;
            while (i < n)
            {
                if (pts[i].OnCurve)
                {
                    // Line segment
                    curX = pts[i].X * scale + offsetX;
                    curY = pts[i].Y * -scale + offsetY;
                    result.Add(new FlatPoint { X = curX, Y = curY });
                    i++;
                }
                else if (pts[i].IsCubic && i + 2 < n)
                {
                    // Cubic Bezier: control1, control2, end
                    float c1x = pts[i].X * scale + offsetX, c1y = pts[i].Y * -scale + offsetY;
                    float c2x = pts[i + 1].X * scale + offsetX, c2y = pts[i + 1].Y * -scale + offsetY;
                    float ex = pts[i + 2].X * scale + offsetX, ey = pts[i + 2].Y * -scale + offsetY;
                    FlattenCubic(curX, curY, c1x, c1y, c2x, c2y, ex, ey, result, 0.25f);
                    curX = ex; curY = ey;
                    i += 3;
                }
                else
                {
                    // Fallback: treat as line
                    curX = pts[i].X * scale + offsetX;
                    curY = pts[i].Y * -scale + offsetY;
                    result.Add(new FlatPoint { X = curX, Y = curY });
                    i++;
                }
            }

            return result;
        }

        /// <summary>
        /// Flatten a TrueType contour (quadratic Bezier curves with implicit on-curve points).
        /// </summary>
        private static List<FlatPoint> FlattenTrueTypeContour(GlyphContour contour, float scale,
            float offsetX, float offsetY)
        {
            var result = new List<FlatPoint>();
            var pts = contour.Points;
            int n = pts.Length;

            // Find the first on-curve point to start from
            int startIdx = -1;
            for (int i = 0; i < n; i++)
            {
                if (pts[i].OnCurve) { startIdx = i; break; }
            }

            float startX, startY;
            if (startIdx == -1)
            {
                startX = (pts[0].X + pts[n - 1].X) * 0.5f * scale + offsetX;
                startY = (pts[0].Y + pts[n - 1].Y) * 0.5f * -scale + offsetY;
                startIdx = 0;
                result.Add(new FlatPoint { X = startX, Y = startY });
            }
            else
            {
                startX = pts[startIdx].X * scale + offsetX;
                startY = pts[startIdx].Y * -scale + offsetY;
                result.Add(new FlatPoint { X = startX, Y = startY });
            }

            float curX = startX, curY = startY;
            int i2 = startIdx;
            int processed = 0;

            while (processed < n)
            {
                int nextIdx = (i2 + 1) % n;
                processed++;

                var nextPt = pts[nextIdx];
                float nx = nextPt.X * scale + offsetX;
                float ny = nextPt.Y * -scale + offsetY;

                if (nextPt.OnCurve)
                {
                    result.Add(new FlatPoint { X = nx, Y = ny });
                    curX = nx; curY = ny;
                    i2 = nextIdx;
                }
                else
                {
                    int afterIdx = (nextIdx + 1) % n;
                    var afterPt = pts[afterIdx];
                    float ax = afterPt.X * scale + offsetX;
                    float ay = afterPt.Y * -scale + offsetY;

                    float endX, endY;
                    if (afterPt.OnCurve)
                    {
                        endX = ax; endY = ay;
                        processed++;
                        i2 = afterIdx;
                    }
                    else
                    {
                        endX = (nx + ax) * 0.5f;
                        endY = (ny + ay) * 0.5f;
                        i2 = nextIdx;
                    }

                    FlattenQuadratic(curX, curY, nx, ny, endX, endY, result, 0.25f);
                    curX = endX; curY = endY;
                }
            }

            return result;
        }

        /// <summary>
        /// Adaptively flatten a quadratic Bezier curve into line segments.
        /// </summary>
        private static void FlattenQuadratic(float x0, float y0, float cx, float cy,
            float x1, float y1, List<FlatPoint> points, float tolerance)
        {
            float mx = (x0 + x1) * 0.5f;
            float my = (y0 + y1) * 0.5f;
            float dx = cx - mx;
            float dy = cy - my;

            if (dx * dx + dy * dy <= tolerance * tolerance)
            {
                points.Add(new FlatPoint { X = x1, Y = y1 });
                return;
            }

            float lx = (x0 + cx) * 0.5f, ly = (y0 + cy) * 0.5f;
            float rx = (cx + x1) * 0.5f, ry = (cy + y1) * 0.5f;
            float midx = (lx + rx) * 0.5f, midy = (ly + ry) * 0.5f;

            FlattenQuadratic(x0, y0, lx, ly, midx, midy, points, tolerance);
            FlattenQuadratic(midx, midy, rx, ry, x1, y1, points, tolerance);
        }

        /// <summary>
        /// Adaptively flatten a cubic Bezier curve (CFF) into line segments.
        /// </summary>
        private static void FlattenCubic(float x0, float y0, float c1x, float c1y,
            float c2x, float c2y, float x1, float y1, List<FlatPoint> points, float tolerance)
        {
            // Flatness test: max distance of control points from the chord
            float dx = x1 - x0, dy = y1 - y0;
            float d1 = Math.Abs((c1x - x1) * dy - (c1y - y1) * dx);
            float d2 = Math.Abs((c2x - x1) * dy - (c2y - y1) * dx);
            float dSq = (d1 + d2);
            dSq = dSq * dSq;
            float lenSq = dx * dx + dy * dy;

            if (dSq <= tolerance * tolerance * lenSq || lenSq < 0.001f)
            {
                points.Add(new FlatPoint { X = x1, Y = y1 });
                return;
            }

            // De Casteljau subdivision at t=0.5
            float m01x = (x0 + c1x) * 0.5f, m01y = (y0 + c1y) * 0.5f;
            float m12x = (c1x + c2x) * 0.5f, m12y = (c1y + c2y) * 0.5f;
            float m23x = (c2x + x1) * 0.5f, m23y = (c2y + y1) * 0.5f;
            float m012x = (m01x + m12x) * 0.5f, m012y = (m01y + m12y) * 0.5f;
            float m123x = (m12x + m23x) * 0.5f, m123y = (m12y + m23y) * 0.5f;
            float mx = (m012x + m123x) * 0.5f, my = (m012y + m123y) * 0.5f;

            FlattenCubic(x0, y0, m01x, m01y, m012x, m012y, mx, my, points, tolerance);
            FlattenCubic(mx, my, m123x, m123y, m23x, m23y, x1, y1, points, tolerance);
        }

        #endregion

        #region Scanline Fill

        private struct Edge
        {
            public float X0, Y0, X1, Y1;
        }

        /// <summary>
        /// Scanline fill using non-zero winding rule.
        /// </summary>
        private static void ScanlineFill(byte[] bitmap, int width, int height, List<Edge> edges)
        {
            if (edges.Count == 0) return;

            // For each scanline
            var intersections = new List<Intersection>();

            for (int y = 0; y < height; y++)
            {
                float scanY = y + 0.5f; // Sample at pixel center
                intersections.Clear();

                // Find intersections with all edges
                for (int e = 0; e < edges.Count; e++)
                {
                    var edge = edges[e];
                    float yMin = Math.Min(edge.Y0, edge.Y1);
                    float yMax = Math.Max(edge.Y0, edge.Y1);

                    if (scanY < yMin || scanY >= yMax)
                        continue;

                    // Calculate x intersection
                    float t = (scanY - edge.Y0) / (edge.Y1 - edge.Y0);
                    float x = edge.X0 + t * (edge.X1 - edge.X0);

                    // Winding direction: +1 if going up, -1 if going down
                    int dir = edge.Y1 > edge.Y0 ? 1 : -1;

                    intersections.Add(new Intersection { X = x, Direction = dir });
                }

                if (intersections.Count == 0)
                    continue;

                // Sort by x
                intersections.Sort((a, b) => a.X.CompareTo(b.X));

                // Fill using non-zero winding rule
                int winding = 0;
                int iIdx = 0;

                for (int x = 0; x < width; x++)
                {
                    float pixelX = x + 0.5f;

                    // Advance past all intersections <= pixelX
                    while (iIdx < intersections.Count && intersections[iIdx].X <= pixelX)
                    {
                        winding += intersections[iIdx].Direction;
                        iIdx++;
                    }

                    if (winding != 0)
                        bitmap[y * width + x] = 255;
                }
            }
        }

        private struct Intersection
        {
            public float X;
            public int Direction;
        }

        #endregion
    }
}
