using System;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// Generates Signed Distance Fields from binary bitmaps using the 8SSEDT algorithm
    /// (8-point Signed Sequential Euclidean Distance Transform).
    /// Two-pass algorithm: O(width * height) complexity.
    /// </summary>
    public static class SdfGenerator
    {
        // Large distance value used for initialization
        private const int INF = 9999;

        /// <summary>
        /// Convert a binary bitmap (255=inside, 0=outside) to a Signed Distance Field.
        /// Returns a grayscale bitmap where 127 = boundary, 255 = deep inside, 0 = far outside.
        /// </summary>
        /// <param name="binaryBitmap">Input bitmap (255=inside, 0=outside)</param>
        /// <param name="width">Bitmap width</param>
        /// <param name="height">Bitmap height</param>
        /// <param name="distanceRange">SDF spread in pixels (typically 4-8)</param>
        /// <returns>SDF bitmap (same dimensions), 1 byte per pixel</returns>
        public static byte[] GenerateSdf(byte[] binaryBitmap, int width, int height, float distanceRange)
        {
            if (binaryBitmap == null || width <= 0 || height <= 0)
                return null;

            int size = width * height;

            // Compute outside distances (distance from outside pixels to nearest inside pixel)
            var outsideDist = ComputeDistanceField(binaryBitmap, width, height, false);

            // Compute inside distances (distance from inside pixels to nearest outside pixel)
            var insideDist = ComputeDistanceField(binaryBitmap, width, height, true);

            // Combine: SDF = outsideDistance - insideDistance
            // Normalize to [0, 255] with 127 at the boundary
            var sdf = new byte[size];
            float invRange = 127.0f / distanceRange;

            for (int i = 0; i < size; i++)
            {
                float outside = (float)Math.Sqrt(outsideDist[i]);
                float inside = (float)Math.Sqrt(insideDist[i]);
                float dist = outside - inside;

                // Map [-distanceRange, +distanceRange] to [0, 255]
                // Boundary (dist=0) maps to 127
                // Inside (dist<0) maps to 128-255
                // Outside (dist>0) maps to 0-126
                float normalized = 127.0f - dist * invRange;
                int value = (int)(normalized + 0.5f);
                sdf[i] = (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
            }

            return sdf;
        }

        /// <summary>
        /// Compute squared distance field using 8SSEDT.
        /// If invert=false, computes distance from 0-pixels to nearest 255-pixel (outside distance).
        /// If invert=true, computes distance from 255-pixels to nearest 0-pixel (inside distance).
        /// Returns squared distances.
        /// </summary>
        private static int[] ComputeDistanceField(byte[] bitmap, int width, int height, bool invert)
        {
            int size = width * height;

            // dx[i], dy[i] = offset to nearest boundary pixel from pixel i
            var dx = new short[size];
            var dy = new short[size];

            // Initialize: boundary pixels = 0 distance, others = INF
            for (int i = 0; i < size; i++)
            {
                bool isTarget = invert ? (bitmap[i] > 127) : (bitmap[i] <= 127);
                if (isTarget)
                {
                    dx[i] = (short)INF;
                    dy[i] = (short)INF;
                }
                // else dx[i]=dy[i]=0 (already zeroed)
            }

            // Forward pass: top-to-bottom, left-to-right
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    CompareAndUpdate(dx, dy, width, height, i, x, y, -1, 0);  // left
                    CompareAndUpdate(dx, dy, width, height, i, x, y, -1, -1); // up-left
                    CompareAndUpdate(dx, dy, width, height, i, x, y, 0, -1);  // up
                    CompareAndUpdate(dx, dy, width, height, i, x, y, 1, -1);  // up-right
                }

                // Right-to-left within same row
                for (int x = width - 1; x >= 0; x--)
                {
                    int i = y * width + x;
                    CompareAndUpdate(dx, dy, width, height, i, x, y, 1, 0);   // right
                }
            }

            // Backward pass: bottom-to-top, right-to-left
            for (int y = height - 1; y >= 0; y--)
            {
                for (int x = width - 1; x >= 0; x--)
                {
                    int i = y * width + x;
                    CompareAndUpdate(dx, dy, width, height, i, x, y, 1, 0);   // right
                    CompareAndUpdate(dx, dy, width, height, i, x, y, 1, 1);   // down-right
                    CompareAndUpdate(dx, dy, width, height, i, x, y, 0, 1);   // down
                    CompareAndUpdate(dx, dy, width, height, i, x, y, -1, 1);  // down-left
                }

                // Left-to-right within same row
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    CompareAndUpdate(dx, dy, width, height, i, x, y, -1, 0);  // left
                }
            }

            // Convert to squared distances
            var distSq = new int[size];
            for (int i = 0; i < size; i++)
            {
                distSq[i] = dx[i] * dx[i] + dy[i] * dy[i];
            }

            return distSq;
        }

        /// <summary>
        /// Compare pixel at (x,y) with neighbor at (x+ox, y+oy) and propagate shorter distance.
        /// </summary>
        private static void CompareAndUpdate(short[] dx, short[] dy, int width, int height,
            int idx, int x, int y, int ox, int oy)
        {
            int nx = x + ox;
            int ny = y + oy;

            if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                return;

            int nIdx = ny * width + nx;

            // Candidate distance: neighbor's offset + offset to neighbor
            int cdx = dx[nIdx] + ox;
            int cdy = dy[nIdx] + oy;
            int candidateDistSq = cdx * cdx + cdy * cdy;
            int currentDistSq = dx[idx] * dx[idx] + dy[idx] * dy[idx];

            if (candidateDistSq < currentDistSq)
            {
                dx[idx] = (short)cdx;
                dy[idx] = (short)cdy;
            }
        }
    }
}
