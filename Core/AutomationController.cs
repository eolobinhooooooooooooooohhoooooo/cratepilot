using System;
using System.Drawing;

namespace CratePilot.Core
{
    public enum AutomationAction { Wait, OpenCrate, TapCrate, AdvanceNext, AdvanceClaim, Complete, Fail }

    public sealed class AutomationDecision
    {
        public AutomationAction Action { get; internal set; }
        public string Message { get; internal set; }
        public Rectangle? Target { get; internal set; }
    }

    // Pure state machine. Call Observe for fresh frames and RecordAction only after a successful input.
    // Counted crates require two same-grade stock observations and baseline - 1.
    // Mid-flow recovery establishes a fresh baseline without incrementing Opened.
    public sealed class AutomationController
    {
        private readonly int maximum;
        private bool inFlight;
        private bool recoveringExistingCrate;
        private bool sawCrateContent;
        private bool sawCollection;
        private bool terminal;
        private AutomationAction lastDecision;
        private string failureMessage;

        public int? Remaining { get; private set; }
        public int? TargetCount { get { return maximum > 0 ? maximum : (int?)null; } }
        public int Opened { get; private set; }
        public bool CrateInFlight { get { return inFlight; } }
        public bool SawCrateContent { get { return sawCrateContent; } }
        public bool SawCollection { get { return sawCollection; } }
        public bool RecoveringExistingCrate { get { return recoveringExistingCrate; } }

        public AutomationController(int maxCrates)
        {
            if (maxCrates < 0) throw new ArgumentOutOfRangeException(nameof(maxCrates));
            maximum = maxCrates;
        }

        public AutomationDecision Observe(ScreenObservation observation)
        {
            if (observation == null) throw new ArgumentNullException(nameof(observation));
            if (failureMessage != null) return Decision(AutomationAction.Fail, failureMessage);
            if (terminal) return Decision(AutomationAction.Complete, "Automação concluída.");

            // 1. Tapping stage (SPAM TAP TO OPEN / TAP TO OPEN)
            if (observation.TapPrompt.HasValue && IsUsableTarget(observation.TapPrompt, observation))
            {
                inFlight = true;
                sawCrateContent = true;
                return Decision(AutomationAction.TapCrate, "Abrindo a caixa; tocando na tela.", observation.TapPrompt);
            }

            // If we clicked OPEN (inFlight is true), and we're not on the menu or rewards, tap the 3D crate in center!
            if (inFlight && !observation.IsCrateMenu && !observation.NextButton.HasValue && !observation.ClaimButton.HasValue)
            {
                sawCrateContent = true;
                Rectangle fallbackCrate = new Rectangle(observation.Width / 2 - 80, (int)(observation.Height * 0.40), 160, 160);
                return Decision(AutomationAction.TapCrate, "Abrindo a caixa; tocando na tela.", fallbackCrate);
            }

            // 2. Crate Menu stage (LOW, MID, HIGH GRADE CRATE)
            // When on crate menu, always handle menu actions (OPEN), never spurious CLAIM buttons!
            if (observation.IsCrateMenu)
            {
                return ObserveMenu(observation);
            }

            // 3. Rewards / Collection stage (NEXT / CLAIM)
            if (observation.NextButton.HasValue && IsUsableTarget(observation.NextButton, observation))
            {
                inFlight = true;
                sawCrateContent = true;
                sawCollection = true;
                return Decision(AutomationAction.AdvanceNext, "Resgatando item: NEXT.", observation.NextButton);
            }
            if (observation.ClaimButton.HasValue && IsUsableTarget(observation.ClaimButton, observation))
            {
                inFlight = true;
                sawCrateContent = true;
                sawCollection = true;
                return Decision(AutomationAction.AdvanceClaim, "Resgatando recompensa: CLAIM.", observation.ClaimButton);
            }

            if (observation.OpenButton.HasValue && IsUsableTarget(observation.OpenButton, observation))
            {
                return ObserveMenu(observation);
            }

            return Decision(AutomationAction.Wait, inFlight ? "Aguardando transição da caixa..." : "Procurando menu de caixa ou itens...");
        }

        public void RecordAction(AutomationAction action)
        {
            if (terminal) return;
            if (action == AutomationAction.OpenCrate)
            {
                inFlight = true;
                sawCrateContent = false;
                sawCollection = false;
                lastDecision = AutomationAction.Wait;
                ResetCandidate();
            }
            else if (action == AutomationAction.TapCrate || action == AutomationAction.AdvanceNext || action == AutomationAction.AdvanceClaim)
            {
                inFlight = true;
                sawCrateContent = true;
                if (action == AutomationAction.AdvanceNext || action == AutomationAction.AdvanceClaim) sawCollection = true;
                if (action != AutomationAction.TapCrate) lastDecision = AutomationAction.Wait;
                ResetCandidate();
            }
        }

        private AutomationDecision ObserveMenu(ScreenObservation observation)
        {
            int count = observation.Remaining ?? -1;
            if (count >= 0) Remaining = count;

            if (count == 0 || (Remaining.HasValue && Remaining.Value == 0))
            {
                terminal = true;
                return Decision(AutomationAction.Complete, "Estoque esgotado; todas as caixas foram abertas!");
            }
            if (maximum > 0 && Opened >= maximum)
            {
                terminal = true;
                return Decision(AutomationAction.Complete, "Meta de caixas concluída!");
            }

            // Only count crate as finished if we actually saw crate content (taps) or collection!
            if (inFlight && (sawCrateContent || sawCollection))
            {
                Opened++;
                if (count >= 0) Remaining = count;
                else if (Remaining.HasValue && Remaining.Value > 0) Remaining = Remaining.Value - 1;
                inFlight = false;
                sawCrateContent = false;
                sawCollection = false;
            }
            else if (inFlight)
            {
                // Click didn't open the crate, we are still at menu: reset inFlight so it tries opening again!
                inFlight = false;
            }

            if (count == 0 || (Remaining.HasValue && Remaining.Value == 0))
            {
                terminal = true;
                return Decision(AutomationAction.Complete, "Estoque esgotado; todas as caixas foram abertas!");
            }
            if (maximum > 0 && Opened >= maximum)
            {
                terminal = true;
                return Decision(AutomationAction.Complete, "Meta de caixas concluída!");
            }

            if (observation.OpenButton.HasValue && IsUsableTarget(observation.OpenButton, observation))
            {
                return Decision(AutomationAction.OpenCrate, "Abrindo caixa (OPEN).", observation.OpenButton);
            }

            return Decision(AutomationAction.Wait, "Menu de caixa visível; aguardando botão OPEN.");
        }

        private void AttachToExistingCrate()
        {
            if (inFlight) return;
            // Recovery is allowed only for exact, unambiguous current visual actions.
            // An unrecorded opening can never become one of our counted purchases.
            inFlight = true;
            recoveringExistingCrate = true;
        }

        private AutomationDecision Fail(string message)
        {
            failureMessage = message;
            ResetCandidate();
            return Decision(AutomationAction.Fail, message);
        }
        private static bool HasConflictingSignals(ScreenObservation observation)
        {
            bool collection = observation.NextButton.HasValue || observation.ClaimButton.HasValue;
            return (observation.NextButton.HasValue && observation.ClaimButton.HasValue) ||
                (observation.TapPrompt.HasValue && collection) ||
                (observation.IsMidGradeMenu && (observation.TapPrompt.HasValue || collection)) ||
                (observation.OpenButton.HasValue && (!observation.IsMidGradeMenu || observation.Kind != ScreenKind.MidGradeMenu));
        }
        private static bool IsUsableTarget(Rectangle? target, ScreenObservation observation)
        {
            if (!target.HasValue) return false;
            var r = target.Value;
            long right = (long)r.Left + r.Width, bottom = (long)r.Top + r.Height;
            return r.Width > 0 && r.Height > 0 && r.Left >= 0 && r.Top >= 0 && right <= Int32.MaxValue && bottom <= Int32.MaxValue &&
                (observation.Width <= 0 || right <= observation.Width) && (observation.Height <= 0 || bottom <= observation.Height);
        }
        private void ResetCandidate() { }
        private AutomationDecision Decision(AutomationAction action, string message, Rectangle? target = null)
        {
            lastDecision = action;
            return new AutomationDecision { Action = action, Message = message, Target = target };
        }
    }
}
