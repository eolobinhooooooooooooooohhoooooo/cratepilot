using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using CratePilot.Core;

namespace CratePilot.UI
{
    public sealed class AutoWindowCandidate
    {
        public IntPtr Handle { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public string ProcessName { get; set; }
        public string Title { get; set; }
        public long ClientArea { get; set; }
        public bool IsVisible { get; set; }
        public bool IsMinimized { get; set; }
        public bool IsForeground { get; set; }

        public bool HasVerifiedIdentity
        {
            get { return Handle != IntPtr.Zero && ProcessId > 0 && ProcessStartUtcTicks > 0 && ProcessStartUtcTicks <= DateTime.MaxValue.Ticks && AutoWindowPicker.IsPlayerProcess(ProcessName); }
        }

        public bool SameIdentity(AutoWindowCandidate other)
        {
            return other != null && Handle == other.Handle && ProcessId == other.ProcessId
                && ProcessStartUtcTicks == other.ProcessStartUtcTicks;
        }
    }

    public static class AutoWindowPicker
    {
        public static bool IsPlayerProcess(string name)
        {
            string normalized = name ?? string.Empty;
            if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(0, normalized.Length - 4);
            return string.Equals(normalized, "RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "RobloxPlayer", StringComparison.OrdinalIgnoreCase);
        }

        public static AutoWindowCandidate Select(IEnumerable<AutoWindowCandidate> candidates, AutoWindowCandidate previous)
        {
            // No native access: deterministic selection is directly testable with synthetic descriptors.
            return (candidates ?? Enumerable.Empty<AutoWindowCandidate>())
                .Where(candidate => candidate != null && candidate.HasVerifiedIdentity && candidate.IsVisible && candidate.ClientArea > 0)
                .OrderByDescending(candidate => candidate.IsForeground)
                .ThenByDescending(candidate => candidate.SameIdentity(previous))
                .ThenBy(candidate => candidate.IsMinimized)
                .ThenByDescending(candidate => candidate.ClientArea)
                .ThenBy(candidate => candidate.ProcessId)
                .ThenBy(candidate => candidate.Handle.ToInt64())
                .FirstOrDefault();
        }
    }

    internal static class AutoWindowDiscovery
    {
        internal static IReadOnlyList<AutoWindowCandidate> GetCandidates(CancellationToken token)
        {
            var result = new List<AutoWindowCandidate>();
            IntPtr foreground = GetForegroundWindow();
            foreach (WindowTarget target in WindowDiscovery.GetRobloxWindows().Take(64))
            {
                token.ThrowIfCancellationRequested();
                AutoWindowCandidate candidate;
                if (TryDescribe(target.Handle, target.Title, foreground, out candidate)) result.Add(candidate);
            }
            return result;
        }

        internal static bool IsStillValid(AutoWindowCandidate expected)
        {
            if (expected == null || !expected.HasVerifiedIdentity) return false;
            AutoWindowCandidate current;
            return TryDescribe(expected.Handle, expected.Title, IntPtr.Zero, out current) && current.SameIdentity(expected);
        }

        private static bool TryDescribe(IntPtr handle, string title, IntPtr foreground, out AutoWindowCandidate candidate)
        {
            candidate = null;
            uint pid;
            if (handle == IntPtr.Zero || !IsWindow(handle) || GetWindowThreadProcessId(handle, out pid) == 0 || pid == 0 || pid > int.MaxValue) return false;
            try
            {
                using (var process = Process.GetProcessById((int)pid))
                {
                    if (process.HasExited || !AutoWindowPicker.IsPlayerProcess(process.ProcessName)) return false;
                    long started = process.StartTime.ToUniversalTime().Ticks;
                    uint checkedPid;
                    if (GetWindowThreadProcessId(handle, out checkedPid) == 0 || checkedPid != pid) return false;
                    NativeRect rect;
                    long area = GetClientRect(handle, out rect) ? Math.Max(0L, (long)rect.Right - rect.Left) * Math.Max(0L, (long)rect.Bottom - rect.Top) : 0;
                    candidate = new AutoWindowCandidate
                    {
                        Handle = handle, ProcessId = (int)pid, ProcessStartUtcTicks = started,
                        ProcessName = process.ProcessName, Title = title ?? "Roblox", ClientArea = area,
                        IsVisible = IsWindowVisible(handle), IsMinimized = IsIconic(handle),
                        IsForeground = foreground != IntPtr.Zero && WindowGuard.IsRelatedWindow(handle, foreground)
                    };
                    return candidate.HasVerifiedIdentity;
                }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    }

    internal static class TargetIdentityOptions
    {
        // Fail closed until the engine accepts and validates the same PID/start-time identity.
        internal static bool TryApply(AutomationOptions options, AutoWindowCandidate target)
        {
            return options != null && target != null && target.HasVerifiedIdentity
                && Set(options, "ExpectedProcessId", target.ProcessId)
                && Set(options, "ExpectedProcessStartUtcTicks", target.ProcessStartUtcTicks);
        }

        private static bool Set(object instance, string name, object value)
        {
            PropertyInfo property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanWrite) return false;
            try { property.SetValue(instance, Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture), null); return true; }
            catch (ArgumentException) { return false; }
            catch (InvalidCastException) { return false; }
            catch (TargetInvocationException) { return false; }
        }
    }
}
