using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CratePilot.PortableOcr
{
    public sealed class PortableOcrWord
    {
        public string Text { get; set; }
        public RectangleF Bounds { get; set; }
    }

    public sealed class TesseractRecognizer : IDisposable
    {
        private readonly object sync = new object();
        private readonly PortableRuntime runtime;
        private IntPtr engine;

        private TesseractRecognizer(PortableRuntime runtime)
        {
            this.runtime = runtime;
            engine = runtime.Api.Create();
            if (engine == IntPtr.Zero) throw new InvalidOperationException("Cannot allocate the portable OCR engine.");
            try
            {
                // OEM_LSTM_ONLY is required by the compact official tessdata_fast model.
                if (runtime.Api.Init(engine, NativeApi.FileSystemPath(runtime.DataDirectory), NativeApi.Utf8("eng"), 1) != 0)
                    throw new InvalidOperationException("Cannot initialize the embedded English OCR model: " + runtime.DataDirectory);
                runtime.Api.SetPageSegMode(engine, 11); // PSM_SPARSE_TEXT, not OSD; no osd model required.
                runtime.Api.SetVariable(engine, NativeApi.Utf8("user_defined_dpi"), NativeApi.Utf8("150"));
                // Intentionally no character whitelist: mixed-case labels and numbers are required.
            }
            catch { Dispose(); throw; }
        }

        public string BackendDescription
        {
            get { return "Tesseract " + runtime.Api.Version + " portable / eng fast 4.1.0 / " + runtime.Architecture; }
        }

        public string RuntimeDirectory
        {
            get { return runtime.DirectoryPath; }
        }

        public static bool TryCreate(out TesseractRecognizer recognizer, out string error)
        {
            recognizer = null;
            error = null;
            try
            {
                recognizer = new TesseractRecognizer(PortableRuntime.GetOrCreate());
                return true;
            }
            catch (Exception exception)
            {
                error = "Portable OCR unavailable: " + exception.GetBaseException().Message;
                return false;
            }
        }

        public IReadOnlyList<PortableOcrWord> Recognize(Bitmap bitmap)
        {
            if (bitmap == null) throw new ArgumentNullException("bitmap");
            lock (sync)
            {
                if (engine == IntPtr.Zero) throw new ObjectDisposedException("TesseractRecognizer");
                int width = bitmap.Width;
                int height = bitmap.Height;
                if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || (long)width * height > 16000000)
                    throw new ArgumentException("Portable OCR accepts images up to 16 megapixels and 16384 pixels per side.", "bitmap");
                int stride = checked(width * 4);
                byte[] image = PrepareImage(bitmap, stride);
                GCHandle pinned = GCHandle.Alloc(image, GCHandleType.Pinned);
                IntPtr iterator = IntPtr.Zero;
                IntPtr monitor = IntPtr.Zero;
                NativeApi api = runtime.Api;
                try
                {
                    api.SetImage(engine, pinned.AddrOfPinnedObject(), width, height, 4, stride);
                    api.SetSourceResolution(engine, 150);
                    monitor = api.CreateMonitor();
                    if (monitor == IntPtr.Zero) throw new InvalidOperationException("Cannot allocate the OCR progress monitor.");
                    api.SetDeadline(monitor, 8000);
                    if (api.Recognize(engine, monitor) != 0)
                        throw new InvalidOperationException("Portable OCR recognition failed or exceeded its eight-second deadline.");
                    List<PortableOcrWord> words = new List<PortableOcrWord>();
                    iterator = api.GetIterator(engine);
                    if (iterator == IntPtr.Zero) return words.AsReadOnly();
                    // Borrowed pointer: owned by the result iterator, never delete it separately.
                    IntPtr page = api.GetPageIterator(iterator);
                    if (page == IntPtr.Zero) return words.AsReadOnly();
                    do
                    {
                        const int wordLevel = 3; // RIL_WORD
                        IntPtr nativeText = api.GetWordText(iterator, wordLevel);
                        try
                        {
                            string text = NativeApi.ReadUtf8(nativeText).Trim();
                            int left, top, right, bottom;
                            if (text.Length == 0 || api.GetBounds(page, wordLevel,
                                out left, out top, out right, out bottom) == 0) continue;
                            left = Math.Max(0, Math.Min(width, left));
                            top = Math.Max(0, Math.Min(height, top));
                            right = Math.Max(left, Math.Min(width, right));
                            bottom = Math.Max(top, Math.Min(height, bottom));
                            if (right > left && bottom > top)
                                words.Add(new PortableOcrWord { Text = text,
                                    Bounds = RectangleF.FromLTRB(left, top, right, bottom) });
                        }
                        finally { if (nativeText != IntPtr.Zero) api.DeleteText(nativeText); }
                    }
                    while (api.NextWord(iterator, 3) != 0);
                    return words.AsReadOnly();
                }
                finally
                {
                    if (iterator != IntPtr.Zero) api.DeleteIterator(iterator);
                    if (monitor != IntPtr.Zero) api.DeleteMonitor(monitor);
                    api.Clear(engine);
                    pinned.Free();
                }
            }
        }

        private static byte[] PrepareImage(Bitmap source, int stride)
        {
            int width = source.Width;
            int height = source.Height;
            byte[] pixels = new byte[checked(stride * height)];
            // Flatten alpha onto white and normalize arbitrary GDI+ pixel formats.
            // All work uses a copy; the caller's bitmap is never mutated or disposed.
            using (Bitmap copy = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(copy))
                {
                    graphics.Clear(Color.White);
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    graphics.DrawImage(source, new Rectangle(0, 0, width, height),
                        0, 0, width, height, GraphicsUnit.Pixel);
                }
                BitmapData data = copy.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(IntPtr.Add(data.Scan0, checked(y * data.Stride)), pixels, y * stride, stride);
                }
                finally { copy.UnlockBits(data); }
            }
            int[] histogram = new int[256];
            int minimum = 255;
            int maximum = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int gray = (29 * pixels[i] + 150 * pixels[i + 1] + 77 * pixels[i + 2] + 128) >> 8;
                pixels[i] = (byte)gray;
                histogram[gray]++;
                minimum = Math.Min(minimum, gray);
                maximum = Math.Max(maximum, gray);
            }
            long darkPixels = 0;
            for (int i = 0; i < 128; i++) darkPixels += histogram[i];
            bool invert = darkPixels > (long)width * height / 2;
            int range = maximum - minimum;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int gray = pixels[i];
                if (range >= 32) gray = (gray - minimum) * 255 / range;
                if (invert) gray = 255 - gray;
                // BGRA source becomes opaque gray BGRA: byte order is also valid
                // for Tesseract's four-channel RGB interpretation, without rescaling.
                pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)gray;
                pixels[i + 3] = 255;
            }
            return pixels;
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (engine != IntPtr.Zero)
                {
                    IntPtr value = engine;
                    engine = IntPtr.Zero;
                    try { runtime.Api.End(value); }
                    finally { runtime.Api.Delete(value); }
                }
            }
            GC.SuppressFinalize(this);
        }

        ~TesseractRecognizer()
        {
            try { Dispose(); }
            catch { /* Finalizers must not escape during process teardown. */ }
        }
    }
}
