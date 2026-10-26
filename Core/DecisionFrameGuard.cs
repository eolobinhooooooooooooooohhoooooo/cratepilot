using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CratePilot.Core
{
    // Pixel/clock/ownership policy only. Tests supply generated frames and ownership predicates.
    internal static class DecisionFrameGuard
    {
        internal const int MaximumOcrAgeMs = 2000;
        internal const int MaximumProofAgeMs = 2000;
        private static readonly Regex CountLabel = new Regex(@"^(\d{1,6})\s*REMAINING$|^REMAINING\s*(\d{1,6})$", RegexOptions.Compiled);

        internal static bool IsFresh(long capturedAt, long now, int maximumAgeMs)
        {
            return capturedAt > 0 && now >= capturedAt && maximumAgeMs >= 0 &&
                (now - capturedAt) * 1000.0 / Stopwatch.Frequency <= maximumAgeMs;
        }

        internal static bool IsUserPause(GuardStatus status)
        {
            return status == GuardStatus.LostFocus || status == GuardStatus.Minimized ||
                status == GuardStatus.CoveredPoint || status == GuardStatus.MouseHeld;
        }

        internal static IReadOnlyList<Rectangle> EvidenceRegions(ScreenObservation observation)
        {
            if (observation == null) throw new ArgumentNullException(nameof(observation));
            var regions = new List<Rectangle>();
            if (observation.Kind == ScreenKind.Unknown || observation.Kind == ScreenKind.Ambiguous) return regions.AsReadOnly();
            AddLabel(regions, observation, observation.TitleBounds);
            AddLabel(regions, observation, observation.OpenButton);
            AddLabel(regions, observation, observation.TapPrompt);
            AddLabel(regions, observation, observation.NextButton);
            AddLabel(regions, observation, observation.ClaimButton);
            if (observation.RemainingBounds.HasValue)
                regions.Add(observation.RemainingBounds.Value);
            return regions.Distinct().ToArray();
        }

        private static void AddLabel(List<Rectangle> regions, ScreenObservation observation, Rectangle? label)
        {
            if (!label.HasValue) return;
            var words = observation.Words == null ? new DetectedWord[0] : observation.Words.Where(word => word != null && label.Value.Contains(word.Bounds)).ToArray();
            if (words.Length == 0) regions.Add(label.Value);
            else regions.AddRange(words.Select(word => word.Bounds));
        }

        private static bool AddRemaining(List<Rectangle> regions, ScreenObservation observation)
        {
            if (observation.Words == null || !observation.RemainingBounds.HasValue ||
                observation.RemainingWords == null || observation.RemainingWords.Count == 0) return false;
            var stockWords = observation.RemainingWords;
            var union = Rectangle.Empty;
            foreach (var token in stockWords)
            {
                if (token == null || token.Bounds.Width <= 0 || token.Bounds.Height <= 0 ||
                    !observation.RemainingBounds.Value.Contains(token.Bounds) ||
                    !observation.Words.Any(word => word != null && word.Bounds == token.Bounds &&
                        RecognitionClassifier.Normalize(word.Text) == RecognitionClassifier.Normalize(token.Text))) return false;
                union = union.IsEmpty ? token.Bounds : Rectangle.Union(union, token.Bounds);
            }
            if (union != observation.RemainingBounds.Value) return false;
            string text = String.Join(" ", stockWords.Select(word => RecognitionClassifier.Normalize(word.Text)));
            var match = CountLabel.Match(text);
            int count;
            if (!match.Success || !Int32.TryParse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value,
                NumberStyles.None, CultureInfo.InvariantCulture, out count) || count != observation.Remaining.Value) return false;
            regions.AddRange(stockWords.Select(word => word.Bounds));
            return true;
        }

        internal static IEnumerable<Point> SamplePoints(Rectangle region)
        {
            if (region.Width < 1 || region.Height < 1) yield break;
            int left = region.Left + Math.Min(2, (region.Width - 1) / 2);
            int top = region.Top + Math.Min(2, (region.Height - 1) / 2);
            int right = region.Right - 1 - Math.Min(2, (region.Width - 1) / 2);
            int bottom = region.Bottom - 1 - Math.Min(2, (region.Height - 1) / 2);
            for (int y = top; ; y = Math.Min(bottom, y + 24))
            {
                for (int x = left; ; x = Math.Min(right, x + 24))
                {
                    yield return new Point(x, y);
                    if (x == right) break;
                }
                if (y == bottom) break;
            }
            yield return new Point(region.Left + region.Width / 2, region.Top + region.Height / 2);
        }

        internal static bool SamplesAreOwned(IEnumerable<Rectangle> regions, Func<Point, bool> belongsToTarget)
        {
            if (regions == null || belongsToTarget == null) return false;
            foreach (var region in regions)
            {
                if (region.Width < 1 || region.Height < 1) return false;
                foreach (var point in SamplePoints(region)) if (!belongsToTarget(point)) return false;
            }
            return true;
        }

        internal static bool PatchesMatch(CapturedFrame baseline, CapturedFrame current)
        {
            if (baseline == null || current == null || baseline.Pixels == null || current.Pixels == null ||
                baseline.Width < 1 || baseline.Height < 1 || baseline.Width != current.Width || baseline.Height != current.Height ||
                (long)baseline.Width * baseline.Height * 4 != baseline.Pixels.Length || baseline.Pixels.Length != current.Pixels.Length) return false;
            
            int total = baseline.Width * baseline.Height;
            if (total == 0) return true;
            int changed = 0;
            for (int offset = 0; offset < baseline.Pixels.Length; offset += 4)
            {
                if (ColorDifference(baseline.Pixels, current.Pixels, offset) > 35)
                    changed++;
            }
            return (double)changed / total < 0.45;
        }

        private static bool LocalInkChanged(CapturedFrame before, CapturedFrame now, int offset, int darkInkShade, int brightInkShade)
        {
            int pixel = offset / 4, x = pixel % before.Width, y = pixel / before.Width;
            int minimumBefore, maximumBefore;
            LocalRange(before, x, y, out minimumBefore, out maximumBefore);
            bool darkAnchor = darkInkShade >= 0 && maximumBefore >= darkInkShade * 16 && minimumBefore <= darkInkShade * 16 + 15;
            bool brightAnchor = brightInkShade >= 0 && maximumBefore >= brightInkShade * 16 && minimumBefore <= brightInkShade * 16 + 15;
            if (maximumBefore - minimumBefore < 24 || (!darkAnchor && !brightAnchor))
                return false; // Untextured background or a weak isolated antialias fringe.
            int minimumNow, maximumNow;
            LocalRange(now, x, y, out minimumNow, out maximumNow);
            if (maximumNow - minimumNow < 24) return true;
            bool oldInk = Luma(before.Pixels, offset) > (minimumBefore + maximumBefore) / 2;
            bool newInk = Luma(now.Pixels, offset) > (minimumNow + maximumNow) / 2;
            return oldInk != newInk;
        }

        private static void LocalRange(CapturedFrame frame, int x, int y, out int minimum, out int maximum)
        {
            minimum = 255; maximum = 0;
            for (int row = Math.Max(0, y - 3); row <= Math.Min(frame.Height - 1, y + 3); row++)
                for (int column = Math.Max(0, x - 3); column <= Math.Min(frame.Width - 1, x + 3); column++)
                {
                    int value = Luma(frame.Pixels, (row * frame.Width + column) * 4);
                    minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value);
                }
        }

        private static int Luma(byte[] pixels, int offset) =>
            (pixels[offset] * 29 + pixels[offset + 1] * 150 + pixels[offset + 2] * 77) >> 8;

        private static int ColorDifference(byte[] before, byte[] after, int offset) =>
            Math.Max(Math.Abs(before[offset] - after[offset]), Math.Max(Math.Abs(before[offset + 1] - after[offset + 1]),
                Math.Abs(before[offset + 2] - after[offset + 2])));
    }

    internal sealed class DecisionFrameChangedException : Exception { }

    internal sealed class ActiveClockPause : IDisposable
    {
        private readonly Stopwatch clock;
        private readonly bool resume;
        private bool disposed;
        internal ActiveClockPause(Stopwatch activeClock)
        {
            clock = activeClock ?? throw new ArgumentNullException(nameof(activeClock));
            resume = clock.IsRunning;
            clock.Stop();
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (resume) clock.Start();
        }
    }
}
