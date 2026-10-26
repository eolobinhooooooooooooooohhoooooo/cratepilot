using System;
using System.Collections.Generic;

namespace CratePilot.Core
{
    // Metadata only. Tests inject delegates and never need real windows or input.
    internal static class WindowTreePolicy
    {
        internal static bool IsAllowedProcessName(string processName)
        {
            return String.Equals(processName, "RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(processName, "RobloxPlayer", StringComparison.OrdinalIgnoreCase);
        }
        internal static bool IsExpectedProcess(uint expectedPid, long expectedStart, uint actualPid, long actualStart, string processName)
        {
            return expectedPid != 0 && expectedPid == actualPid && expectedStart > 0 && expectedStart == actualStart && IsAllowedProcessName(processName);
        }
        internal static bool TryReadExpectedIdentity(object options, out uint processId, out long startUtcTicks)
        {
            processId = 0; startUtcTicks = 0;
            if (options == null) return false;
            // Capability-based bridge: the UI uses the same names while contracts are integrated independently.
            var type = options.GetType();
            var pidProperty = type.GetProperty("ExpectedProcessId");
            var startProperty = type.GetProperty("ExpectedProcessStartUtcTicks");
            if (pidProperty == null || startProperty == null || !pidProperty.CanRead || !startProperty.CanRead) return false;
            try
            {
                object pid = pidProperty.GetValue(options, null), started = startProperty.GetValue(options, null);
                if (pid is int && (int)pid > 0) processId = (uint)(int)pid;
                else if (pid is uint) processId = (uint)pid;
                if (started is long) startUtcTicks = (long)started;
                bool valid = processId != 0 && processId <= Int32.MaxValue && startUtcTicks > 0 && startUtcTicks <= DateTime.MaxValue.Ticks;
                if (!valid) { processId = 0; startUtcTicks = 0; }
                return valid;
            }
            catch (System.Reflection.TargetInvocationException) { processId = 0; startUtcTicks = 0; return false; }
            catch (ArgumentException) { processId = 0; startUtcTicks = 0; return false; }
        }
        internal static bool BelongsToTarget(IntPtr target, uint targetPid, IntPtr candidate,
            Func<IntPtr, uint> processId, Func<IntPtr, IntPtr> root, Func<IntPtr, IntPtr> rootOwner)
        {
            if (target == IntPtr.Zero || candidate == IntPtr.Zero || targetPid == 0 || processId == null || root == null || rootOwner == null)
                return false;
            if (processId(candidate) != targetPid) return false;
            return candidate == target || root(candidate) == target || rootOwner(candidate) == target;
        }
        internal static WindowTarget ChoosePreferred(IReadOnlyList<WindowTarget> windows, IntPtr previousHandle, IntPtr foreground,
            Func<WindowTarget, IntPtr, bool> belongsToWindow)
        {
            if (windows == null) throw new ArgumentNullException(nameof(windows));
            WindowTarget first = null, previous = null, foregroundTarget = null;
            foreach (var window in windows)
            {
                if (window == null || window.Handle == IntPtr.Zero) continue;
                if (first == null || window.Handle.ToInt64() < first.Handle.ToInt64()) first = window;
                if (window.Handle == previousHandle) previous = window;
                if (foreground != IntPtr.Zero && belongsToWindow != null && belongsToWindow(window, foreground) &&
                    (foregroundTarget == null || window.Handle == foreground ||
                     (foregroundTarget.Handle != foreground && window.Handle.ToInt64() < foregroundTarget.Handle.ToInt64())))
                    foregroundTarget = window;
            }
            return foregroundTarget ?? previous ?? first;
        }
    }
}
