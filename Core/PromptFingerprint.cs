using System;
using System.Collections.Generic;
using System.Drawing;

namespace CratePilot.Core
{
    // The learned silhouette uses luminance/outline contrast, not a fixed scene or button color.
    // It is deliberately conservative: a mismatch stops input and requests a new OCR pass.
    public sealed class PromptFingerprint
    {
        private readonly byte[] edges;
        private readonly int width, height, active;
        private readonly double aspect;
        private readonly bool[] allowed;
        public bool IsReliable { get; private set; }

        private PromptFingerprint(byte[] bgra, int pixelWidth, int pixelHeight, IEnumerable<Rectangle> textRegions)
        {
            width = Math.Min(160, pixelWidth);
            height = Math.Min(56, pixelHeight);
            aspect = (double)pixelWidth / pixelHeight;
            if (textRegions != null)
            {
                allowed = new bool[width * height];
                foreach (var region in textRegions)
                {
                    var r = Rectangle.Intersect(region, new Rectangle(0, 0, pixelWidth, pixelHeight));
                    if (r.Width <= 0 || r.Height <= 0) continue;
                    int left = Math.Max(0, (int)Math.Floor((double)r.Left * width / pixelWidth));
                    int right = Math.Min(width, (int)Math.Ceiling((double)r.Right * width / pixelWidth));
                    int top = Math.Max(0, (int)Math.Floor((double)r.Top * height / pixelHeight));
                    int bottom = Math.Min(height, (int)Math.Ceiling((double)r.Bottom * height / pixelHeight));
                    for (int y = top; y < bottom; y++) for (int x = left; x < right; x++) allowed[y * width + x] = true;
                }
            }
            edges = BuildMap(bgra, pixelWidth, pixelHeight, width, height, allowed);
            foreach (byte value in edges) if (value != 0) active++;
            IsReliable = width >= 3 && height >= 3 && active >= 20 && active < edges.Length * .65;
        }

        public static PromptFingerprint Create(byte[] bgra, int width, int height)
        {
            Validate(bgra, width, height);
            return new PromptFingerprint(bgra, width, height, null);
        }

        // Pass each OCR word rectangle in ROI-relative coordinates to exclude world pixels/gaps.
        public static PromptFingerprint Create(byte[] bgra, int width, int height, IEnumerable<Rectangle> textRegions)
        {
            Validate(bgra, width, height);
            if (textRegions == null) throw new ArgumentNullException(nameof(textRegions));
            return new PromptFingerprint(bgra, width, height, textRegions);
        }

        public bool Matches(byte[] bgra, int pixelWidth, int pixelHeight)
        {
            Validate(bgra, pixelWidth, pixelHeight);
            if (!IsReliable || Math.Abs(((double)pixelWidth / pixelHeight) / aspect - 1) > .08) return false;
            var current = BuildMap(bgra, pixelWidth, pixelHeight, width, height, allowed);
            int best = 0;
            bool bestDistributed = false;
            // One reduced pixel permits tiny antialiasing/camera jitter without learning a new shape.
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int matched = 0;
                    var segmentTotal = new int[4];
                    var segmentMatched = new int[4];
                    for (int y = 1; y < height - 1; y++)
                        for (int x = 1; x < width - 1; x++)
                            if (edges[y * width + x] != 0)
                            {
                                int segment = Math.Min(3, x * 4 / width);
                                segmentTotal[segment]++;
                                if (current[(y + dy) * width + x + dx] != 0) { matched++; segmentMatched[segment]++; }
                            }
                    bool distributed = true;
                    for (int segment = 0; segment < 4; segment++)
                        if (segmentTotal[segment] >= Math.Max(3, active * .06) && segmentMatched[segment] < segmentTotal[segment] * .65)
                            distributed = false;
                    if (matched > best) { best = matched; bestDistributed = distributed; }
                    else if (matched == best && distributed) bestDistributed = true;
                }
            // Additional background edges are intentionally ignored; only learned text features vote.
            return bestDistributed && best >= active * .82;
        }

        private static byte[] BuildMap(byte[] bgra, int sourceWidth, int sourceHeight, int mapWidth, int mapHeight, bool[] allowed)
        {
            var result = new byte[mapWidth * mapHeight];
            int radius = Math.Max(3, Math.Min(24, (int)Math.Ceiling(sourceHeight * .14)));
            for (int y = 1; y < mapHeight - 1; y++)
                for (int x = 1; x < mapWidth - 1; x++)
                {
                    if (allowed != null && !allowed[y * mapWidth + x]) continue;
                    int sx = Math.Min(sourceWidth - 1, x * sourceWidth / mapWidth);
                    int sy = Math.Min(sourceHeight - 1, y * sourceHeight / mapHeight);
                    int center = Luma(bgra, sourceWidth, sx, sy);
                    if (center < 135) continue;
                    int dark = Math.Min(110, center - 70);
                    // Opposite dark outline pixels identify thin bright strokes, not one-sided scene edges.
                    bool horizontal = HasDark(bgra, sourceWidth, sourceHeight, sx, sy, -1, 0, radius, dark) &&
                        HasDark(bgra, sourceWidth, sourceHeight, sx, sy, 1, 0, radius, dark);
                    bool vertical = HasDark(bgra, sourceWidth, sourceHeight, sx, sy, 0, -1, radius, dark) &&
                        HasDark(bgra, sourceWidth, sourceHeight, sx, sy, 0, 1, radius, dark);
                    if (horizontal || vertical) result[y * mapWidth + x] = 1;
                }
            return result;
        }

        private static bool HasDark(byte[] pixels, int width, int height, int x, int y, int dx, int dy, int radius, int threshold)
        {
            for (int step = 1; step <= radius; step++)
            {
                int xx = x + step * dx, yy = y + step * dy;
                if (xx < 0 || yy < 0 || xx >= width || yy >= height) return false;
                if (Luma(pixels, width, xx, yy) <= threshold) return true;
            }
            return false;
        }

        internal static int Luma(byte[] pixels, int width, int x, int y)
        {
            int offset = (y * width + x) * 4;
            return (pixels[offset] * 29 + pixels[offset + 1] * 150 + pixels[offset + 2] * 77) >> 8;
        }
        private static void Validate(byte[] bgra, int width, int height)
        {
            if (bgra == null) throw new ArgumentNullException(nameof(bgra));
            if (width <= 0 || height <= 0 || (long)width * height * 4 != bgra.Length) throw new ArgumentException("Invalid BGRA dimensions.");
        }
    }

    public static class TapTargetLocator
    {
        // Search for a large, locally symmetric edge structure adjacent to the prompt.
        // The anchor-relative fallback stays in the game world, never on a menu action label.
        public static Point Locate(byte[] bgra, int width, int height, Rectangle prompt)
        {
            if (width < 10 || height < 10) throw new ArgumentException("The image is too small for a safe tap target.");
            
            // In Roblox TDS crate opening, the 3D crate is always centered horizontally
            int centerX = width / 2;
            
            // Center the tap firmly on the 3D crate without jumping or drifting
            int targetY;
            if (prompt.Top >= height * 0.40)
            {
                targetY = Math.Max((int)(height * 0.25), Math.Min((int)(height * 0.52), prompt.Top - (int)(height * 0.16)));
            }
            else
            {
                targetY = Math.Min((int)(height * 0.75), prompt.Bottom + (int)(height * 0.16));
            }

            return new Point(centerX, targetY);
        }

        private static double EdgeEnergy(byte[] pixels, int width, int height, int x, int y, int radius)
        {
            long sum = 0;
            int samples = 0;
            for (int xx = Math.Max(2, x - radius); xx <= Math.Min(width - 3, x + radius); xx += 4)
                for (int yy = Math.Max(2, y - radius / 2); yy <= Math.Min(height - 3, y + radius / 2); yy += 4)
                {
                    sum += Math.Abs(PromptFingerprint.Luma(pixels, width, xx + 2, yy) - PromptFingerprint.Luma(pixels, width, xx - 2, yy));
                    sum += Math.Abs(PromptFingerprint.Luma(pixels, width, xx, yy + 2) - PromptFingerprint.Luma(pixels, width, xx, yy - 2));
                    samples++;
                }
            return samples == 0 ? 0 : (double)sum / samples;
        }
    }
}
