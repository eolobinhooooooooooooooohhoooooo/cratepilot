using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CratePilot.Core
{
    public enum AutomationState
    {
        Idle, CountingDown, Searching, Opening, Tapping, Collecting,
        Paused, Completed, Stopped, Error
    }

    public sealed class WindowTarget
    {
        public IntPtr Handle { get; set; }
        public string Title { get; set; }
        public override string ToString() { return Title ?? "Roblox"; }
    }

    public sealed class AutomationOptions
    {
        public IntPtr WindowHandle { get; set; }
        public int ExpectedProcessId { get; set; }
        public long ExpectedProcessStartUtcTicks { get; set; }
        public int MaxCrates { get; set; }
        public int TapIntervalMs { get; set; } = 8;
        public int DetectionTimeoutSeconds { get; set; } = 25;
    }

    public sealed class AutomationSnapshot
    {
        public AutomationState State { get; set; }
        public string Message { get; set; }
        public int? Remaining { get; set; }
        public int Opened { get; set; }
        public int Clicks { get; set; }
        public int CurrentCrateClicks { get; set; }
        public int ItemsAdvanced { get; set; }
        public int? TargetCount { get; set; }
        public double RecognitionMs { get; set; }
        public bool IsRunning { get; set; }
    }
}
