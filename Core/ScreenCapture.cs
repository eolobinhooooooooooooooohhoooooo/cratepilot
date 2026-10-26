using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace CratePilot.Core
{
    internal sealed class CapturedFrame
    {
        internal byte[] Pixels;
        internal int Width, Height;
        internal Rectangle ClientRegion;
        internal WindowGeometry Geometry;
        internal long CapturedAt;
        internal long EvidenceVerifiedAt;
        internal long InputEpoch;
    }

    internal static class ScreenCapture
    {
        internal static CapturedFrame Capture(WindowGuard guard, Rectangle? region = null)
        {
            using (new DpiScope())
            {
                WindowGeometry geometry;
                var status = guard.Inspect(out geometry);
                if (status != GuardStatus.Ready) throw new WindowUnavailableException(status);
                var rect = region ?? new Rectangle(0, 0, geometry.Width, geometry.Height);
                if (rect.Width < 1 || rect.Height < 1 || rect.Left < 0 || rect.Top < 0 || rect.Right > geometry.Width || rect.Bottom > geometry.Height)
                    throw new WindowUnavailableException(GuardStatus.ChangedGeometry);
                // Keep allocations bounded on an unexpectedly huge desktop/window.
                if ((long)rect.Width * rect.Height > 40000000) throw new InvalidOperationException("A janela é grande demais para uma captura segura. Reduza seu tamanho.");
                IntPtr screen = IntPtr.Zero, memory = IntPtr.Zero, bitmap = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    screen = NativeMethods.GetDC(IntPtr.Zero);
                    memory = NativeMethods.CreateCompatibleDC(screen);
                    if (screen == IntPtr.Zero || memory == IntPtr.Zero) throw new InvalidOperationException("Não foi possível capturar a janela do jogo.");
                    var info = new NativeMethods.BitmapInfo
                    {
                        Header = new NativeMethods.BitmapInfoHeader
                        {
                            Size = (uint)Marshal.SizeOf(typeof(NativeMethods.BitmapInfoHeader)),
                            Width = rect.Width, Height = -rect.Height, Planes = 1, BitCount = 32
                        }
                    };
                    IntPtr pixels;
                    bitmap = NativeMethods.CreateDIBSection(screen, ref info, 0, out pixels, IntPtr.Zero, 0);
                    if (bitmap == IntPtr.Zero || pixels == IntPtr.Zero) throw new InvalidOperationException("Falha ao reservar a captura da janela.");
                    old = NativeMethods.SelectObject(memory, bitmap);
                    if (old == IntPtr.Zero || old == new IntPtr(-1)) throw new InvalidOperationException("Falha ao preparar a captura da janela.");
                    long capturedAt = Stopwatch.GetTimestamp();
                    if (!NativeMethods.BitBlt(memory, 0, 0, rect.Width, rect.Height, screen,
                        geometry.ClientOnScreen.Left + rect.Left, geometry.ClientOnScreen.Top + rect.Top, 0x00CC0020 | 0x40000000))
                        throw new InvalidOperationException("O Windows não permitiu capturar a tela do jogo.");
                    NativeMethods.GdiFlush();
                    var data = new byte[checked(rect.Width * rect.Height * 4)];
                    Marshal.Copy(pixels, data, 0, data.Length);
                    WindowGeometry after;
                    status = guard.Inspect(out after);
                    if (status != GuardStatus.Ready) throw new WindowUnavailableException(status);
                    if (!geometry.SameAs(after)) throw new WindowUnavailableException(GuardStatus.ChangedGeometry);
                    return new CapturedFrame { Pixels = data, Width = rect.Width, Height = rect.Height, ClientRegion = rect, Geometry = geometry, CapturedAt = capturedAt };
                }
                finally
                {
                    if (old != IntPtr.Zero && old != new IntPtr(-1) && memory != IntPtr.Zero) NativeMethods.SelectObject(memory, old);
                    if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
                    if (memory != IntPtr.Zero) NativeMethods.DeleteDC(memory);
                    if (screen != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screen);
                }
            }
        }

        internal static CapturedFrame Crop(CapturedFrame source, Rectangle rect)
        {
            rect.Intersect(new Rectangle(0, 0, source.Width, source.Height));
            if (rect.Width < 1 || rect.Height < 1) throw new ArgumentOutOfRangeException(nameof(rect));
            var bytes = new byte[rect.Width * rect.Height * 4];
            for (int y = 0; y < rect.Height; y++)
                Buffer.BlockCopy(source.Pixels, ((rect.Top + y) * source.Width + rect.Left) * 4, bytes, y * rect.Width * 4, rect.Width * 4);
            return new CapturedFrame
            {
                Pixels = bytes, Width = rect.Width, Height = rect.Height,
                ClientRegion = new Rectangle(source.ClientRegion.Left + rect.Left, source.ClientRegion.Top + rect.Top, rect.Width, rect.Height), Geometry = source.Geometry,
                CapturedAt = source.CapturedAt, InputEpoch = source.InputEpoch
            };
        }
    }

    internal sealed class WindowUnavailableException : Exception
    {
        internal GuardStatus Status { get; private set; }
        internal IReadOnlyList<Rectangle> Regions { get; private set; }
        internal WindowGeometry Geometry { get; private set; }
        internal WindowUnavailableException(GuardStatus status, IReadOnlyList<Rectangle> regions = null, WindowGeometry geometry = null)
            : base("A janela do Roblox não está disponível para entrada segura.") { Status = status; Regions = regions; Geometry = geometry; }
    }
}
