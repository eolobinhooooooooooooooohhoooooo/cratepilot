using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CratePilot.Core
{
    public static class WindowDiscovery
    {
        public static WindowTarget GetPreferredRobloxWindow() { return GetPreferredRobloxWindow(IntPtr.Zero); }
        public static WindowTarget GetPreferredRobloxWindow(IntPtr previousHandle)
        {
            var windows = GetRobloxWindows();
            return WindowTreePolicy.ChoosePreferred(windows, previousHandle, NativeMethods.GetForegroundWindow(),
                (window, foreground) => WindowGuard.IsRelatedWindow(window.Handle, foreground));
        }
        public static bool IsRobloxWindow(IntPtr handle) { return WindowGuard.IsAllowedWindow(handle); }

        public static IReadOnlyList<WindowTarget> GetRobloxWindows()
        {
            var windows = new List<WindowTarget>();
            NativeMethods.EnumWindows((handle, unused) =>
            {
                try
                {
                    if (!WindowGuard.IsAllowedWindow(handle) || NativeMethods.GetWindow(handle, 4) != IntPtr.Zero) return true;
                    var title = new StringBuilder(512);
                    NativeMethods.GetWindowText(handle, title, title.Capacity);
                    windows.Add(new WindowTarget { Handle = handle, Title = title.Length == 0 ? "Roblox" : title.ToString() });
                }
                catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is ArgumentException)
                {
                    // A player can exit between enumeration and inspection.
                }
                return true;
            }, IntPtr.Zero);
            return windows.AsReadOnly();
        }
    }

    internal enum GuardStatus { Ready, LostFocus, Minimized, InvalidWindow, ChangedGeometry, CoveredPoint, MouseHeld, StaleFrame, UserPaused }

    internal sealed class WindowGeometry
    {
        internal Rectangle ClientOnScreen;
        internal int Width { get { return ClientOnScreen.Width; } }
        internal int Height { get { return ClientOnScreen.Height; } }
        internal bool SameAs(WindowGeometry other) { return other != null && ClientOnScreen == other.ClientOnScreen; }
    }

    internal sealed class WindowGuard
    {
        private readonly IntPtr handle;
        private readonly uint processId;
        private readonly long processStart;
        internal string LastStatusDetails { get; private set; } = String.Empty;

        internal WindowGuard(IntPtr windowHandle)
        {
            if (!IsAllowedWindow(windowHandle)) throw new InvalidOperationException("A janela do Roblox desta execução não está disponível. Deixe o jogo visível e inicie novamente.");
            handle = windowHandle;
            NativeMethods.GetWindowThreadProcessId(handle, out processId);
            using (var process = Process.GetProcessById((int)processId)) processStart = process.StartTime.ToUniversalTime().Ticks;
        }

        internal WindowGuard(IntPtr windowHandle, uint expectedProcessId, long expectedProcessStartUtcTicks) : this(windowHandle)
        {
            if (expectedProcessId == 0 || expectedProcessStartUtcTicks <= 0 || processId != expectedProcessId || processStart != expectedProcessStartUtcTicks)
                throw new InvalidOperationException("A identidade do Roblox mudou antes do início. Nenhum clique foi enviado; inicie novamente para reconectar ao processo correto.");
        }

        internal static bool IsAllowedWindow(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle) || !NativeMethods.IsWindowVisible(windowHandle) ||
                NativeMethods.IsIconic(windowHandle)) return false;
            NativeMethods.Rect client;
            if (!NativeMethods.GetClientRect(windowHandle, out client) || client.Right - client.Left < 200 || client.Bottom - client.Top < 150)
                return false;
            uint pid;
            if (NativeMethods.GetWindowThreadProcessId(windowHandle, out pid) == 0 || pid == 0) return false;
            try
            {
                using (var process = Process.GetProcessById((int)pid))
                {
                    string name = process.ProcessName;
                    return !process.HasExited && WindowTreePolicy.IsAllowedProcessName(name);
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception)
            { return false; }
        }

        internal GuardStatus Inspect(out WindowGeometry geometry)
        {
            geometry = null;
            using (new DpiScope())
            {
                if (!NativeMethods.IsWindow(handle)) return GuardStatus.InvalidWindow;
                uint pid;
                if (NativeMethods.GetWindowThreadProcessId(handle, out pid) == 0 || pid != processId) return GuardStatus.InvalidWindow;
                try
                {
                    using (var process = Process.GetProcessById((int)pid))
                    {
                        if (process.HasExited || !WindowTreePolicy.IsExpectedProcess(processId, processStart, pid,
                            process.StartTime.ToUniversalTime().Ticks, process.ProcessName)) return GuardStatus.InvalidWindow;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception)
                { return GuardStatus.InvalidWindow; }
                if (!NativeMethods.IsWindowVisible(handle) || NativeMethods.IsIconic(handle)) return GuardStatus.Minimized;
                if (!IsForegroundRoblox()) return GuardStatus.LostFocus;
                NativeMethods.Rect client;
                var origin = new NativeMethods.Point();
                if (!NativeMethods.GetClientRect(handle, out client) || !NativeMethods.ClientToScreen(handle, ref origin) ||
                    client.Right - client.Left < 200 || client.Bottom - client.Top < 150) return GuardStatus.InvalidWindow;
                geometry = new WindowGeometry { ClientOnScreen = new Rectangle(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top) };
                return GuardStatus.Ready;
            }
        }

        internal GuardStatus ValidatePoint(Point clientPoint, WindowGeometry expected, out Point screenPoint)
        {
            screenPoint = Point.Empty;
            WindowGeometry current;
            var status = Inspect(out current);
            if (status != GuardStatus.Ready) return status;
            if (!current.SameAs(expected)) return GuardStatus.ChangedGeometry;
            if (clientPoint.X < 2 || clientPoint.Y < 2 || clientPoint.X >= current.Width - 2 || clientPoint.Y >= current.Height - 2)
                return GuardStatus.CoveredPoint;
            screenPoint = new Point(current.ClientOnScreen.Left + clientPoint.X, current.ClientOnScreen.Top + clientPoint.Y);
            using (new DpiScope())
            {
                if (!OwnsScreenPoint(screenPoint)) return GuardStatus.CoveredPoint;
            }
            return GuardStatus.Ready;
        }

        internal GuardStatus ValidateRegions(IReadOnlyList<Rectangle> regions, WindowGeometry expected)
        {
            WindowGeometry current;
            var status = Inspect(out current);
            if (status != GuardStatus.Ready) return status;
            if (!current.SameAs(expected)) return GuardStatus.ChangedGeometry;
            if (regions == null) return GuardStatus.Ready;
            foreach (var region in regions)
                if (region.Left < 0 || region.Top < 0 || region.Width < 1 || region.Height < 1 ||
                    (long)region.Left + region.Width > current.Width || (long)region.Top + region.Height > current.Height)
                    return GuardStatus.ChangedGeometry;
            using (new DpiScope())
            {
                if (!DecisionFrameGuard.SamplesAreOwned(regions, point => OwnsScreenPoint(new Point(current.ClientOnScreen.Left + point.X, current.ClientOnScreen.Top + point.Y))))
                    return GuardStatus.CoveredPoint;
            }
            WindowGeometry after;
            status = Inspect(out after);
            return status != GuardStatus.Ready ? status : current.SameAs(after) ? GuardStatus.Ready : GuardStatus.ChangedGeometry;
        }

        private bool OwnsScreenPoint(Point point)
        {
            var underPointer = NativeMethods.WindowFromPoint(new NativeMethods.Point { X = point.X, Y = point.Y });
            bool owned = BelongsToSelectedTree(underPointer);
            if (!owned)
            {
                NativeMethods.SetForegroundWindow(handle);
                NativeMethods.BringWindowToTop(handle);
                underPointer = NativeMethods.WindowFromPoint(new NativeMethods.Point { X = point.X, Y = point.Y });
                owned = BelongsToSelectedTree(underPointer);
            }
            if (!owned)
            {
                NativeMethods.Rect rect;
                if (NativeMethods.GetClientRect(handle, out rect))
                {
                    var origin = new NativeMethods.Point();
                    if (NativeMethods.ClientToScreen(handle, ref origin))
                    {
                        var clientRect = new Rectangle(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
                        if (clientRect.Contains(point)) return true;
                    }
                }
                LastStatusDetails = "Ponto coberto por HWND=" + HandleText(underPointer) + "; alvo=" + HandleText(handle) + ".";
            }
            return owned;
        }

        private bool IsForegroundRoblox()
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (BelongsToSelectedTree(fg)) return true;
            if (NativeMethods.IsIconic(handle))
                NativeMethods.ShowWindow(handle, 9);
            NativeMethods.SetForegroundWindow(handle);
            NativeMethods.BringWindowToTop(handle);
            fg = NativeMethods.GetForegroundWindow();
            if (BelongsToSelectedTree(fg)) return true;
            if (NativeMethods.IsWindowVisible(handle) && !NativeMethods.IsIconic(handle))
                return true;
            LastStatusDetails = "Foco fora da árvore autorizada; alvo=" + HandleText(handle) + ", PID=" + processId +
                "; foco=" + HandleText(fg) + ", PID=" + ReadProcessId(fg) + ".";
            return false;
        }

        private bool BelongsToSelectedTree(IntPtr candidate)
        {
            return WindowTreePolicy.BelongsToTarget(handle, processId, candidate, ReadProcessId, ReadRoot, ReadRootOwner);
        }
        internal static bool IsRelatedWindow(IntPtr target, IntPtr candidate)
        {
            return WindowTreePolicy.BelongsToTarget(target, ReadProcessId(target), candidate, ReadProcessId, ReadRoot, ReadRootOwner);
        }
        private static uint ReadProcessId(IntPtr window)
        {
            uint pid;
            return window != IntPtr.Zero && NativeMethods.GetWindowThreadProcessId(window, out pid) != 0 ? pid : 0;
        }
        private static IntPtr ReadRoot(IntPtr window) { return window == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(window, 2); }
        private static IntPtr ReadRootOwner(IntPtr window) { return window == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(window, 3); }
        private static string HandleText(IntPtr window) { return "0x" + window.ToInt64().ToString("X"); }

        internal GuardStatus FinalInputStatus(Point screenPoint)
        {
            if (!NativeMethods.IsWindow(handle) || ReadProcessId(handle) != processId) return GuardStatus.InvalidWindow;
            if (NativeMethods.IsIconic(handle) || !NativeMethods.IsWindowVisible(handle)) return GuardStatus.Minimized;
            return OwnsScreenPoint(screenPoint) ? GuardStatus.Ready : GuardStatus.CoveredPoint;
        }

        internal static bool IsPrimaryButtonDown() { return (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0; }

        internal bool TryInitialFocus()
        {
            // Only the explicit initial countdown may request focus. Never attach input queues.
            if (!IsAllowedWindow(handle)) return false;
            EnsureWindowReady();
            return IsForegroundRoblox();
        }

        internal void EnsureWindowReady()
        {
            if (handle == IntPtr.Zero) return;
            if (NativeMethods.IsIconic(handle))
                NativeMethods.ShowWindow(handle, 9);
            NativeMethods.SetForegroundWindow(handle);
            NativeMethods.BringWindowToTop(handle);
        }
    }

    internal sealed class DpiScope : IDisposable
    {
        private IntPtr previous;
        internal DpiScope()
        {
            try { previous = NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { }
        }
        public void Dispose()
        {
            if (previous == IntPtr.Zero) return;
            NativeMethods.SetThreadDpiAwarenessContext(previous);
            previous = IntPtr.Zero;
        }
    }

    internal sealed class SafeMouse
    {
        private readonly WindowGuard guard;
        private readonly Func<bool> primaryButtonDown;
        private readonly AutomationRunGate runGate;
        private readonly Action beforeInput;
        internal SafeMouse(WindowGuard windowGuard, Func<bool> isPrimaryButtonDown = null, AutomationRunGate inputGate = null, Action validateBeforeInput = null)
        {
            guard = windowGuard;
            primaryButtonDown = isPrimaryButtonDown ?? WindowGuard.IsPrimaryButtonDown;
            runGate = inputGate;
            beforeInput = validateBeforeInput;
        }

        internal GuardStatus Click(Point clientPoint, WindowGeometry expected, CancellationToken token, IReadOnlyList<Rectangle> evidenceRegions = null, long evidenceVerifiedAt = 0, long inputEpoch = -1)
        {
            token.ThrowIfCancellationRequested();
            if (runGate != null && !runGate.IsCurrent(inputEpoch)) return runGate.IsUserPaused ? GuardStatus.UserPaused : GuardStatus.StaleFrame;
            if (primaryButtonDown()) return GuardStatus.MouseHeld;
            using (new DpiScope())
            {
                token.ThrowIfCancellationRequested();
                Point screen;
                var status = guard.ValidatePoint(clientPoint, expected, out screen);
                if (status != GuardStatus.Ready) return status;

                guard.EnsureWindowReady();
                NativeMethods.SetCursorPos(screen.X, screen.Y);
                Thread.Sleep(20);

                token.ThrowIfCancellationRequested();
                if (primaryButtonDown()) return GuardStatus.MouseHeld;
                if (beforeInput != null) beforeInput();

                NativeMethods.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(45);
                NativeMethods.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(20);

                return GuardStatus.Ready;
            }
        }

        internal GuardStatus FastTap(Point clientPoint, WindowGeometry expected, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (primaryButtonDown()) return GuardStatus.MouseHeld;
            using (new DpiScope())
            {
                token.ThrowIfCancellationRequested();
                Point screen;
                var status = guard.ValidatePoint(clientPoint, expected, out screen);
                if (status != GuardStatus.Ready) return status;

                NativeMethods.SetCursorPos(screen.X, screen.Y);

                token.ThrowIfCancellationRequested();
                if (primaryButtonDown()) return GuardStatus.MouseHeld;

                NativeMethods.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(15);
                NativeMethods.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);

                return GuardStatus.Ready;
            }
        }

        private GuardStatus ExecuteInput(long epoch, CancellationToken token, Func<GuardStatus> transaction)
        {
            token.ThrowIfCancellationRequested();
            return runGate == null ? transaction() : runGate.Execute(epoch, token, transaction);
        }

        private static NativeMethods.Input MakeInput(uint flags)
        {
            return new NativeMethods.Input { Type = 0, Data = new NativeMethods.InputUnion { Mouse = new NativeMethods.MouseInput { Flags = flags } } };
        }
        private static void SendOne(NativeMethods.Input input)
        {
            if (NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(NativeMethods.Input))) != 1)
                throw new InvalidOperationException("O Windows bloqueou a entrada. Execute o jogo e o aplicativo no mesmo nível de permissão.");
        }
        private static void ReleaseButton()
        {
            var release = MakeInput(0x0004);
            // Release is unconditional once our down was accepted, even after cancellation/focus loss.
            for (int attempt = 0; attempt < 3; attempt++)
                if (NativeMethods.SendInput(1, new[] { release }, Marshal.SizeOf(typeof(NativeMethods.Input))) == 1) return;
            // Only an UP fallback is permitted; never use legacy injection to bypass a blocked click.
            NativeMethods.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            throw new InvalidOperationException("O Windows bloqueou a liberação da entrada. Automação interrompida por segurança.");
        }
    }

    internal static class NativeMethods
    {
        internal delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfoHeader
        {
            internal uint Size;
            internal int Width, Height;
            internal ushort Planes, BitCount;
            internal uint Compression, SizeImage;
            internal int XPelsPerMeter, YPelsPerMeter;
            internal uint ClrUsed, ClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo { internal BitmapInfoHeader Header; internal uint Colors; }
        [StructLayout(LayoutKind.Sequential)] internal struct Input { internal uint Type; internal InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] internal MouseInput Mouse; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
        {
            internal int X, Y;
            internal uint MouseData, Flags, Time;
            internal UIntPtr ExtraInfo;
        }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(IntPtr window, out Rect rectangle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(IntPtr deviceContext);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateDIBSection(IntPtr deviceContext, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr item);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(IntPtr item);
        [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GdiFlush();
    }
}
