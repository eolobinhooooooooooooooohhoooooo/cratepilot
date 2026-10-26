using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using CratePilot.PortableOcr;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace CratePilot.Core
{
    internal sealed class WindowsOcrRecognizer : IDisposable
    {
        private readonly OcrEngine engine;
        private TesseractRecognizer portable;
        internal string LanguageName { get; private set; }

        internal WindowsOcrRecognizer()
        {
            try
            {
                var languages = OcrEngine.AvailableRecognizerLanguages;
                Language chosen = languages.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ??
                    languages.FirstOrDefault(l => l.LanguageTag.StartsWith("pt", StringComparison.OrdinalIgnoreCase)) ??
                    languages.FirstOrDefault(l => l.LanguageTag.StartsWith("es", StringComparison.OrdinalIgnoreCase)) ?? languages.FirstOrDefault();
                if (chosen != null) engine = OcrEngine.TryCreateFromLanguage(chosen);
                if (engine == null) engine = OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch (Exception ex)
            {
                LocalLog.Write("Native OCR unavailable: " + ex.GetType().Name);
            }
            if (engine != null) LanguageName = "Windows " + engine.RecognizerLanguage.LanguageTag;
            else
            {
                string error;
                if (!TesseractRecognizer.TryCreate(out portable, out error))
                    throw new InvalidOperationException("O OCR nativo não possui idioma instalado e o OCR portátil não pôde iniciar. " + error);
                LanguageName = "OCR portátil (Tesseract)";
            }
        }

        internal async Task<ScreenObservation> RecognizeAsync(CapturedFrame frame, CancellationToken token)
        {
            if (engine == null) return await ReadPortableAsync(frame, token).ConfigureAwait(false);
            var original = await ReadAsync(frame, 0, token).ConfigureAwait(false);
            var best = RecognitionClassifier.Analyze(original, frame.Geometry.Width, frame.Geometry.Height);
            if (IsComplete(best)) { best.VisualSignature = Signature(frame); return best; }
            // Black-on-white outlined text is substantially more stable over a moving 3D scene.
            var highContrast = await ReadAsync(frame, 1, token).ConfigureAwait(false);
            var contrastObservation = RecognitionClassifier.Analyze(highContrast, frame.Geometry.Width, frame.Geometry.Height);
            if (Score(contrastObservation) > Score(best)) best = contrastObservation;
            if (!IsComplete(best))
            {
                var merged = Merge(original, highContrast);
                var combined = RecognitionClassifier.Analyze(merged, frame.Geometry.Width, frame.Geometry.Height);
                if (Score(combined) > Score(best)) best = combined;
            }
            best.VisualSignature = Signature(frame);
            return best;
        }

        private async Task<ScreenObservation> ReadPortableAsync(CapturedFrame frame, CancellationToken token)
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var words = ReadPortableWords(frame, false, token);
                var best = RecognitionClassifier.Analyze(words, frame.Geometry.Width, frame.Geometry.Height);
                if (!IsComplete(best))
                {
                    var contrast = ReadPortableWords(frame, true, token);
                    var alternate = RecognitionClassifier.Analyze(contrast, frame.Geometry.Width, frame.Geometry.Height);
                    if (Score(alternate) > Score(best)) best = alternate;
                    if (!IsComplete(best))
                    {
                        var combined = RecognitionClassifier.Analyze(Merge(words, contrast), frame.Geometry.Width, frame.Geometry.Height);
                        if (Score(combined) > Score(best)) best = combined;
                    }
                }
                best.VisualSignature = Signature(frame);
                return best;
            }, token).ConfigureAwait(false);
        }

        private List<DetectedWord> ReadPortableWords(CapturedFrame frame, bool contrast, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // Tesseract receives physical pixels at the capture's native resolution.
            using (var bitmap = new Bitmap(frame.Width, frame.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                var bits = bitmap.LockBits(new Rectangle(0, 0, frame.Width, frame.Height),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    var pixels = PreparePixels(frame, frame.Width, frame.Height, contrast ? 1 : 0);
                    for (int y = 0; y < frame.Height; y++)
                        System.Runtime.InteropServices.Marshal.Copy(pixels, y * frame.Width * 4,
                            IntPtr.Add(bits.Scan0, y * bits.Stride), frame.Width * 4);
                }
                finally { bitmap.UnlockBits(bits); }
                token.ThrowIfCancellationRequested();
                var result = portable.Recognize(bitmap);
                token.ThrowIfCancellationRequested();
                return result.Select(w =>
                {
                    var r = w.Bounds;
                    var bounds = Rectangle.Intersect(Rectangle.FromLTRB(frame.ClientRegion.Left + (int)Math.Floor(r.Left),
                        frame.ClientRegion.Top + (int)Math.Floor(r.Top), frame.ClientRegion.Left + (int)Math.Ceiling(r.Right),
                        frame.ClientRegion.Top + (int)Math.Ceiling(r.Bottom)), new Rectangle(0, 0, frame.Geometry.Width, frame.Geometry.Height));
                    return new DetectedWord(w.Text, bounds);
                }).Where(w => w.Bounds.Width > 0 && w.Bounds.Height > 0).ToList();
            }
        }

        public void Dispose()
        {
            if (portable != null) { portable.Dispose(); portable = null; }
        }

        private async Task<List<DetectedWord>> ReadAsync(CapturedFrame frame, int mode, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            double scale = Math.Min(1.0, Math.Min(2600, (double)OcrEngine.MaxImageDimension) / Math.Max(frame.Width, frame.Height));
            int width = Math.Max(1, (int)Math.Round(frame.Width * scale));
            int height = Math.Max(1, (int)Math.Round(frame.Height * scale));
            var pixels = PreparePixels(frame, width, height, mode);
            using (var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore))
            {
                bitmap.CopyFromBuffer(pixels.AsBuffer());
                var result = await engine.RecognizeAsync(bitmap).AsTask(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var words = new List<DetectedWord>();
                foreach (var line in result.Lines)
                    foreach (var word in line.Words)
                    {
                        var r = word.BoundingRect;
                        int x = frame.ClientRegion.Left + (int)Math.Floor(r.X / scale);
                        int y = frame.ClientRegion.Top + (int)Math.Floor(r.Y / scale);
                        int right = frame.ClientRegion.Left + (int)Math.Ceiling((r.X + r.Width) / scale);
                        int bottom = frame.ClientRegion.Top + (int)Math.Ceiling((r.Y + r.Height) / scale);
                        var bounds = Rectangle.Intersect(Rectangle.FromLTRB(x, y, right, bottom), new Rectangle(0, 0, frame.Geometry.Width, frame.Geometry.Height));
                        if (bounds.Width > 0 && bounds.Height > 0) words.Add(new DetectedWord(word.Text, bounds));
                    }
                return words;
            }
        }

        private static byte[] PreparePixels(CapturedFrame frame, int width, int height, int mode)
        {
            var output = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int source = ((y * frame.Height / height) * frame.Width + x * frame.Width / width) * 4;
                    int target = (y * width + x) * 4;
                    if (mode == 0)
                    {
                        output[target] = frame.Pixels[source]; output[target + 1] = frame.Pixels[source + 1]; output[target + 2] = frame.Pixels[source + 2];
                    }
                    else
                    {
                        int luma = (frame.Pixels[source] * 29 + frame.Pixels[source + 1] * 150 + frame.Pixels[source + 2] * 77) >> 8;
                        byte value = luma >= 180 ? (byte)0 : (byte)255;
                        output[target] = output[target + 1] = output[target + 2] = value;
                    }
                    output[target + 3] = 255;
                }
            return output;
        }

        private static List<DetectedWord> Merge(List<DetectedWord> original, List<DetectedWord> alternate)
        {
            var merged = new List<DetectedWord>(original);
            foreach (var word in alternate)
            {
                bool overlaps = merged.Any(w => Rectangle.Intersect(w.Bounds, word.Bounds).Width > Math.Min(w.Bounds.Width, word.Bounds.Width) * .5 &&
                    Rectangle.Intersect(w.Bounds, word.Bounds).Height > Math.Min(w.Bounds.Height, word.Bounds.Height) * .5);
                if (!overlaps) merged.Add(word);
            }
            return merged;
        }

        private static bool IsComplete(ScreenObservation o)
        {
            return !o.HasAmbiguousActions && (o.Kind == ScreenKind.TapPrompt || o.Kind == ScreenKind.Collection ||
                (o.Kind == ScreenKind.MidGradeMenu && o.Remaining.HasValue && (o.Remaining.Value == 0 || o.OpenButton.HasValue)));
        }
        private static int Score(ScreenObservation o)
        {
            if (o.Kind == ScreenKind.Ambiguous) return -1;
            if (IsComplete(o)) return 100;
            return (o.IsMidGradeMenu ? 30 : 0) + (o.Remaining.HasValue ? 20 : 0) + (o.OpenButton.HasValue ? 10 : 0);
        }

        internal static ulong Signature(CapturedFrame frame)
        {
            var samples = new int[64];
            int total = 0;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    // Exclude side HUDs; the center contains the crate/item, rather than chat.
                    int sx = Math.Min(frame.Width - 1, frame.Width / 5 + (2 * x + 1) * frame.Width * 3 / 80);
                    int sy = Math.Min(frame.Height - 1, frame.Height / 8 + (2 * y + 1) * frame.Height * 3 / 64);
                    samples[y * 8 + x] = PromptFingerprint.Luma(frame.Pixels, frame.Width, sx, sy);
                    total += samples[y * 8 + x];
                }
            ulong result = 0;
            for (int i = 0; i < 64; i++) if (samples[i] >= total / 64) result |= 1UL << i;
            return result;
        }
    }
}
