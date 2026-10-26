using System;
using System.Diagnostics;
using System.Threading;

namespace CratePilot.Core
{
    // A short synchronous input transaction shares its lock with Pause/Stop. Never await here.
    internal sealed class AutomationRunGate
    {
        private readonly object sync = new object();
        private readonly Stopwatch clock = new Stopwatch();
        private bool userPaused, stopped, clockEnabled;
        private int ambientPauses;
        private long epoch;

        internal bool IsUserPaused { get { lock (sync) return userPaused; } }
        internal bool IsStopped { get { lock (sync) return stopped; } }
        internal long Epoch { get { lock (sync) return epoch; } }
        internal Stopwatch Clock { get { return clock; } }
        internal TimeSpan Elapsed { get { lock (sync) return clock.Elapsed; } }

        internal void BeginRun()
        {
            lock (sync)
            {
                clock.Reset(); clockEnabled = false; userPaused = false; stopped = false; ambientPauses = 0; epoch++;
            }
        }
        internal bool SetUserPaused(bool value)
        {
            lock (sync)
            {
                if (userPaused != value) { userPaused = value; epoch++; }
                UpdateClock();
                return userPaused;
            }
        }
        internal bool ToggleUserPause()
        {
            lock (sync) { userPaused = !userPaused; epoch++; UpdateClock(); return userPaused; }
        }
        internal void Stop()
        {
            lock (sync) { if (!stopped) { stopped = true; epoch++; } UpdateClock(); }
        }
        internal void StartClock() { lock (sync) { clockEnabled = true; UpdateClock(); } }
        internal void StopClock() { lock (sync) { clockEnabled = false; UpdateClock(); } }
        internal bool IsCurrent(long decisionEpoch)
        {
            lock (sync) return !stopped && !userPaused && decisionEpoch == epoch;
        }
        internal GuardStatus Execute(long decisionEpoch, CancellationToken token, Func<GuardStatus> transaction)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            lock (sync)
            {
                token.ThrowIfCancellationRequested();
                if (userPaused) return GuardStatus.UserPaused;
                if (stopped || decisionEpoch != epoch) return GuardStatus.StaleFrame;
                return transaction();
            }
        }
        internal IDisposable PauseAmbientClock()
        {
            lock (sync) { ambientPauses++; UpdateClock(); }
            return new AmbientPause(this);
        }
        private void EndAmbientPause()
        {
            lock (sync) { if (ambientPauses > 0) ambientPauses--; UpdateClock(); }
        }
        private void UpdateClock()
        {
            if (clockEnabled && !stopped && !userPaused && ambientPauses == 0) clock.Start();
            else clock.Stop();
        }
        private sealed class AmbientPause : IDisposable
        {
            private AutomationRunGate owner;
            internal AmbientPause(AutomationRunGate gate) { owner = gate; }
            public void Dispose() { var gate = Interlocked.Exchange(ref owner, null); if (gate != null) gate.EndAmbientPause(); }
        }
    }

    internal static class PairedInputTransaction
    {
        internal static void Execute(Action down, Action up)
        {
            if (down == null) throw new ArgumentNullException(nameof(down));
            if (up == null) throw new ArgumentNullException(nameof(up));
            bool pressed = false;
            try { down(); pressed = true; }
            finally { if (pressed) up(); }
        }
    }
}
