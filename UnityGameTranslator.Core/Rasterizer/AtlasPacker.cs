using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// Packs rasterized SDF glyph bitmaps into a single power-of-2 texture atlas
    /// using shelf (row-based) packing. Sorts glyphs by height for optimal packing.
    /// </summary>
    public static class AtlasPacker
    {
        /// <summary>
        /// Pack rasterized glyphs into an RGBA atlas.
        /// Sets AtlasX/AtlasY on each glyph.
        /// Returns RGBA pixel data (4 bytes/pixel: R=G=B=255, A=SDF value).
        /// </summary>
        /// <param name="glyphs">Glyphs with SDF bitmaps to pack</param>
        /// <param name="padding">Pixels between glyphs to prevent bleeding</param>
        /// <returns>RGBA atlas data, width, and height</returns>
        public static AtlasResult PackAtlas(List<RasterizedGlyph> glyphs, int padding = 1)
        {
            if (glyphs == null || glyphs.Count == 0)
                return new AtlasResult { RgbaData = new byte[4], Width = 1, Height = 1 };

            // Filter out empty glyphs (spaces etc.)
            var packable = new List<RasterizedGlyph>();
            foreach (var g in glyphs)
            {
                if (g.Bitmap != null && g.Width > 0 && g.Height > 0)
                    packable.Add(g);
            }

            if (packable.Count == 0)
                return new AtlasResult { RgbaData = new byte[4], Width = 1, Height = 1 };

            // Sort by height descending (better shelf packing)
            packable.Sort((a, b) => b.Height.CompareTo(a.Height));

            // Find minimum atlas size that fits all glyphs
            int atlasW, atlasH;
            FindAtlasSize(packable, padding, out atlasW, out atlasH);

            // Place glyphs using shelf packing
            ShelfPack(packable, atlasW, atlasH, padding);

            // Compose RGBA atlas
            var rgba = new byte[atlasW * atlasH * 4];

            // SDF in grayscale RGB with A=255 (same format as msdf-atlas-gen).
            // ConvertSdfTextureForTMP will copy R to Alpha for TMP shader.
            // Init to black (SDF=0 = far outside) with A=255
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = 0;       // R = SDF (outside)
                rgba[i + 1] = 0;   // G
                rgba[i + 2] = 0;   // B
                rgba[i + 3] = 255; // A = opaque
            }

            // Blit each glyph into the atlas
            foreach (var glyph in packable)
            {
                for (int gy = 0; gy < glyph.Height; gy++)
                {
                    for (int gx = 0; gx < glyph.Width; gx++)
                    {
                        int atlasX = glyph.AtlasX + gx;
                        int atlasY = glyph.AtlasY + gy;

                        if (atlasX >= atlasW || atlasY >= atlasH)
                            continue;

                        int atlasIdx = (atlasY * atlasW + atlasX) * 4;
                        byte sdfValue = glyph.Bitmap[gy * glyph.Width + gx];

                        rgba[atlasIdx] = sdfValue;     // R = SDF
                        rgba[atlasIdx + 1] = sdfValue; // G = SDF
                        rgba[atlasIdx + 2] = sdfValue; // B = SDF
                        rgba[atlasIdx + 3] = 255;      // A = opaque
                    }
                }
            }

            return new AtlasResult { RgbaData = rgba, Width = atlasW, Height = atlasH };
        }

        /// <summary>
        /// Find the smallest power-of-2 atlas size that fits all glyphs.
        /// </summary>
        private static void FindAtlasSize(List<RasterizedGlyph> glyphs, int padding,
            out int width, out int height)
        {
            // Estimate total area needed
            long totalArea = 0;
            int maxGlyphW = 0;
            int maxGlyphH = 0;

            foreach (var g in glyphs)
            {
                int pw = g.Width + padding;
                int ph = g.Height + padding;
                totalArea += pw * ph;
                if (pw > maxGlyphW) maxGlyphW = pw;
                if (ph > maxGlyphH) maxGlyphH = ph;
            }

            // Start with the smallest power-of-2 that could fit the area
            // and is at least as wide/tall as the largest glyph
            int minDim = Math.Max(maxGlyphW, maxGlyphH);
            int startSize = NextPowerOf2(Math.Max(minDim, (int)Math.Sqrt(totalArea)));

            // Try sizes until one fits
            int[] sizes = { 128, 256, 512, 1024, 2048, 4096, 8192 };

            foreach (int size in sizes)
            {
                if (size < startSize) continue;

                // Try square first
                if (TryShelfPack(glyphs, size, size, padding))
                {
                    width = size;
                    height = size;
                    return;
                }

                // Try wider
                if (size < 8192 && TryShelfPack(glyphs, size * 2, size, padding))
                {
                    width = size * 2;
                    height = size;
                    return;
                }
            }

            // Fallback: maximum size
            width = 8192;
            height = 8192;
        }

        /// <summary>
        /// Test if glyphs fit in the given atlas dimensions using shelf packing.
        /// Does not modify glyph positions.
        /// </summary>
        private static bool TryShelfPack(List<RasterizedGlyph> glyphs, int atlasW, int atlasH, int padding)
        {
            int shelfX = 0;
            int shelfY = 0;
            int shelfHeight = 0;

            foreach (var g in glyphs)
            {
                int gw = g.Width + padding;
                int gh = g.Height + padding;

                if (shelfX + gw > atlasW)
                {
                    // Start new shelf
                    shelfX = 0;
                    shelfY += shelfHeight;
                    shelfHeight = 0;
                }

                if (shelfY + gh > atlasH)
                    return false; // Doesn't fit

                shelfX += gw;
                if (gh > shelfHeight)
                    shelfHeight = gh;
            }

            return true;
        }

        /// <summary>
        /// Place glyphs using shelf packing. Sets AtlasX/AtlasY on each glyph.
        /// </summary>
        private static void ShelfPack(List<RasterizedGlyph> glyphs, int atlasW, int atlasH, int padding)
        {
            int shelfX = 0;
            int shelfY = 0;
            int shelfHeight = 0;

            foreach (var g in glyphs)
            {
                int gw = g.Width + padding;
                int gh = g.Height + padding;

                if (shelfX + gw > atlasW)
                {
                    shelfX = 0;
                    shelfY += shelfHeight;
                    shelfHeight = 0;
                }

                g.AtlasX = shelfX;
                g.AtlasY = shelfY;

                shelfX += gw;
                if (gh > shelfHeight)
                    shelfHeight = gh;
            }
        }

        private static int NextPowerOf2(int v)
        {
            v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            v++;
            return Math.Max(v, 1);
        }
    }

    /// <summary>
    /// Result of atlas packing.
    /// </summary>
    public class AtlasResult
    {
        public byte[] RgbaData;
        public int Width;
        public int Height;
    }
}
