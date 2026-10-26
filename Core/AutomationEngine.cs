using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CratePilot.Core
{
    public sealed class AutomationEngine : IDisposable
    {
        private readonly object sync = new object();
        private CancellationTokenSource activeCancellation;
        private bool running, disposed;
        private readonly AutomationRunGate runGate = new AutomationRunGate();
        public bool IsUserPaused { get { return runGate.IsUserPaused; } }
        public void Pause() { runGate.SetUserPaused(true); }
        public void Resume() { runGate.SetUserPaused(false); }
        public bool TogglePause() { return runGate.ToggleUserPause(); }
        public event Action<AutomationSnapshot> SnapshotChanged;

        public Task StartAsync(AutomationOptions options, CancellationToken cancellationToken)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.WindowHandle == IntPtr.Zero) throw new ArgumentException("Nenhuma janela válida do Roblox está disponível. Abra o jogo para iniciar.", nameof(options));
            if (options.MaxCrates < 0) throw new ArgumentOutOfRangeException(nameof(options), "A quantidade de caixas não pode ser negativa.");
            if (options.TapIntervalMs < 8 || options.TapIntervalMs > 1000)
                throw new ArgumentOutOfRangeException(nameof(options), "Use um intervalo entre 8 e 1.000 ms.");
            if (options.DetectionTimeoutSeconds < 5 || options.DetectionTimeoutSeconds > 300)
                throw new ArgumentOutOfRangeException(nameof(options), "Use um tempo limite entre 5 e 300 segundos.");
            var copy = new AutomationOptions
            {
                WindowHandle = options.WindowHandle, MaxCrates = options.MaxCrates,
                TapIntervalMs = options.TapIntervalMs, DetectionTimeoutSeconds = options.DetectionTimeoutSeconds
            };
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(AutomationEngine));
                if (running) throw new InvalidOperationException("Já existe uma automação em execução.");
                uint expectedProcessId;
                long expectedProcessStartUtcTicks;
                if (!WindowTreePolicy.TryReadExpectedIdentity(options, out expectedProcessId, out expectedProcessStartUtcTicks) && !cancellationToken.IsCancellationRequested)
                    throw new InvalidOperationException("Não foi possível confirmar o PID e início do Roblox. Reconecte a janela automaticamente antes de iniciar.");
                var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                runGate.BeginRun();
                running = true;
                activeCancellation = source;
                var stopRegistration = source.Token.Register(runGate.Stop);
                // All OCR and capture work stays off the WPF dispatcher.
                return Task.Run(() => RunAsync(copy, source, stopRegistration, expectedProcessId, expectedProcessStartUtcTicks));
            }
        }

        public void Stop()
        {
            lock (sync)
            {
                if (activeCancellation != null) { runGate.Stop(); activeCancellation.Cancel(); }
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                runGate.Stop();
                if (activeCancellation != null) activeCancellation.Cancel();
            }
        }

        private async Task RunAsync(AutomationOptions options, CancellationTokenSource source, CancellationTokenRegistration stopRegistration, uint expectedProcessId, long expectedProcessStartUtcTicks)
        {
            var token = source.Token;
            var session = new Session(options, runGate);
            try
            {
                token.ThrowIfCancellationRequested();
                long initialInputEpoch = session.Gate.Epoch;
                session.Guard = new WindowGuard(options.WindowHandle, expectedProcessId, expectedProcessStartUtcTicks);
                session.Mouse = new SafeMouse(session.Guard, null, session.Gate, () => CheckTimeout(session));
                session.Ocr = new WindowsOcrRecognizer();
                token.ThrowIfCancellationRequested();
                LocalLog.Write("[info] Motor iniciado; versão=" + typeof(AutomationEngine).Assembly.GetName().Version +
                    "; build=" + typeof(AutomationEngine).Module.ModuleVersionId.ToString("N").Substring(0, 12) +
                    "; arquitetura=" + (IntPtr.Size * 8) + "; PID=" + expectedProcessId + "; quantidade=" + options.MaxCrates +
                    "; intervalo=" + options.TapIntervalMs + " ms; OCR=" + session.Ocr.LanguageName + ".");
                token.ThrowIfCancellationRequested();
                session.Gate.Execute(initialInputEpoch, token, () =>
                {
                    session.Guard.TryInitialFocus();
                    return GuardStatus.Ready;
                });
                await WaitForUserPauseAsync(session, token).ConfigureAwait(false);
                Publish(session, AutomationState.CountingDown, "Iniciando...", true);
                await Task.Delay(100, token).ConfigureAwait(false);
                session.Gate.StartClock();
                session.LastProgress = session.Elapsed;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    await WaitForReadyAsync(session, token).ConfigureAwait(false);
                    try
                    {
                        var recognized = await ReadScreenAsync(session, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        var observation = recognized.Observation;
                        TrackProgress(session, observation);
                        int beforeOpened = session.Controller.Opened;
                        var decision = session.Controller.Observe(observation);
                        UpdateCounters(session);
                        if (session.Controller.Opened != beforeOpened)
                        {
                            session.EstimatedTaps = session.EstimatedTaps.HasValue ?
                                session.EstimatedTaps.Value * .65 + session.Snapshot.CurrentCrateClicks * .35 : session.Snapshot.CurrentCrateClicks;
                            MarkProgress(session);
                            session.CollectionPending = false;
                            session.CollectionRetry = 0;
                        }
                        if (decision.Action == AutomationAction.Complete)
                        {
                            Publish(session, AutomationState.Completed, decision.Message, false);
                            return;
                        }
                        if (decision.Action == AutomationAction.Fail) throw new InvalidOperationException(decision.Message);
                        if (decision.Action != AutomationAction.Wait) MarkProgress(session);
                        CheckTimeout(session);
                        if (decision.Action == AutomationAction.TapCrate)
                        {
                            Publish(session, AutomationState.Tapping, decision.Message, true);
                            await TapBurstAsync(session, recognized.Frame, observation, token).ConfigureAwait(false);
                            continue;
                        }
                        if (decision.Action == AutomationAction.OpenCrate || decision.Action == AutomationAction.AdvanceNext || decision.Action == AutomationAction.AdvanceClaim)
                        {
                            var target = decision.Target.Value;
                            var point = new Point(target.Left + target.Width / 2, target.Top + target.Height / 2);
                            var evidence = DecisionFrameGuard.EvidenceRegions(observation);
                            CheckTimeout(session);
                            var status = session.Mouse.Click(point, recognized.Frame.Geometry, token, evidence, recognized.Frame.EvidenceVerifiedAt, recognized.Frame.InputEpoch);
                            if (status != GuardStatus.Ready)
                            {
                                await HandleUnsafePointAsync(session, status, token, evidence, recognized.Frame.Geometry).ConfigureAwait(false);
                                continue;
                            }
                            session.Controller.RecordAction(decision.Action);
                            session.Snapshot.Clicks++;
                            if (decision.Action == AutomationAction.OpenCrate)
                            {
                                session.Snapshot.CurrentCrateClicks = 0;
                                session.CollectionPending = false;
                                session.CollectionRetry = 0;
                                session.LastContentKey = null;
                                session.LastRecognizedKind = ScreenKind.MidGradeMenu;
                                session.TapPhaseSeen = false;
                                session.PendingCollectionKey = null;
                                session.PendingCollectionFrames = 0;
                                session.CrateStartedAt = session.Elapsed;
                                MarkProgress(session);
                                Publish(session, AutomationState.Opening, "OPEN clicado! Aguardando o aviso de toque da caixa.", true);
                                await Task.Delay(60, token).ConfigureAwait(false);
                            }
                            else
                            {
                                session.Snapshot.ItemsAdvanced++;
                                RecordCollectionInput(session, observation);
                                Publish(session, AutomationState.Collecting, decision.Action == AutomationAction.AdvanceNext ?
                                    "NEXT acionado; verificando o próximo item." : "CLAIM acionado; verificando o próximo item ou o estoque.", true);
                                await Task.Delay(50, token).ConfigureAwait(false);
                            }
                            continue;
                        }
                        var waitingState = session.Controller.CrateInFlight ?
                            (session.Controller.SawCrateContent ? AutomationState.Collecting : AutomationState.Opening) : AutomationState.Searching;
                        Publish(session, waitingState, decision.Message, true);
                        await Task.Delay(35, token).ConfigureAwait(false);
                    }
                    catch (DecisionFrameChangedException)
                    {
                        // A transition/old OCR frame requests a new scan, not a failed click or a user pause.
                        if (session.Gate.IsUserPaused)
                        {
                            await WaitForUserPauseAsync(session, token).ConfigureAwait(false);
                            continue;
                        }
                        CheckTimeout(session);
                        Publish(session, AutomationState.Searching, "A tela mudou durante a leitura; reconhecendo novamente sem clicar.", true);
                        await Task.Delay(65, token).ConfigureAwait(false);
                    }
                    catch (WindowUnavailableException ex)
                    {
                        if (ex.Status == GuardStatus.InvalidWindow) throw new InvalidOperationException("A janela original do Roblox foi fechada ou substituída.");
                        if (ex.Status == GuardStatus.ChangedGeometry)
                        {
                            session.SafetyRegions = null;
                            session.SafetyGeometry = null;
                            CheckTimeout(session);
                            Publish(session, AutomationState.Searching, "A janela mudou de posição ou tamanho; recalibrando sem clicar.", true);
                            await Task.Delay(200, token).ConfigureAwait(false);
                        }
                        else await HandleUnsafePointAsync(session, ex.Status, token, ex.Regions, ex.Geometry).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Publish(session, AutomationState.Stopped, "Automação parada. Nenhum clique pendente.", false);
            }
            catch (Exception ex)
            {
                string message = ex is InvalidOperationException || ex is ArgumentException ? ex.Message :
                    "Não foi possível continuar com segurança. Copie o log exibido no aplicativo para diagnóstico.";
                LocalLog.Write("[error] Motor interrompido (" + ex.GetType().Name + "): " + message);
                Publish(session, AutomationState.Error, message, false);
            }
            finally
            {
                session.Gate.StopClock();
                session.Gate.Stop();
                if (session.Ocr != null)
                {
                    try { session.Ocr.Dispose(); }
                    catch (Exception ex) { LocalLog.Write("[warning] Falha ao liberar o OCR: " + ex.GetType().Name + "."); }
                }
                LocalLog.Write("[info] Motor encerrado; estado=" + session.Snapshot.State + "; caixas confirmadas=" + session.Snapshot.Opened + "; cliques=" + session.Snapshot.Clicks + ".");
                stopRegistration.Dispose();
                lock (sync)
                {
                    if (activeCancellation == source) activeCancellation = null;
                    running = false;
                    source.Dispose();
                }
            }
        }

        private async Task<RecognizedFrame> ReadScreenAsync(Session session, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            long inputEpoch = session.Gate.Epoch;
            EnsureDecisionCurrent(session, inputEpoch, token);
            var timer = Stopwatch.StartNew();
            var frame = ScreenCapture.Capture(session.Guard);
            frame.InputEpoch = inputEpoch;
            var observation = await session.Ocr.RecognizeAsync(frame, token).ConfigureAwait(false);
            EnsureDecisionCurrent(session, inputEpoch, token);
            WindowGeometry current;
            var status = session.Guard.Inspect(out current);
            if (status != GuardStatus.Ready) throw new WindowUnavailableException(status);
            if (!frame.Geometry.SameAs(current)) throw new WindowUnavailableException(GuardStatus.ChangedGeometry);
            var evidence = DecisionFrameGuard.EvidenceRegions(observation);
            if (evidence.Count > 0)
            {
                session.SafetyRegions = evidence;
                session.SafetyGeometry = frame.Geometry;
            }
            frame.EvidenceVerifiedAt = Stopwatch.GetTimestamp();
            session.Snapshot.RecognitionMs = timer.Elapsed.TotalMilliseconds;
            return new RecognizedFrame { Frame = frame, Observation = observation };
        }

        private static IReadOnlyList<Rectangle> VerifyEvidence(Session session, CapturedFrame frame, ScreenObservation observation, CancellationToken token)
        {
            EnsureDecisionCurrent(session, frame.InputEpoch, token);
            IReadOnlyList<Rectangle> regions;
            try { regions = DecisionFrameGuard.EvidenceRegions(observation); }
            catch (DecisionFrameChangedException)
            {
                LogDiagnostic(session, "MISSING_EVIDENCE", "A leitura não trouxe todos os pixels de suporte do estoque/ação; aguardando nova confirmação.");
                throw;
            }
            if (regions.Count == 0) return regions;
            long oldestProof = Int64.MaxValue;
            try
            {
                var status = session.Guard.ValidateRegions(regions, frame.Geometry);
                if (status != GuardStatus.Ready) throw new WindowUnavailableException(status, regions, frame.Geometry);
                if (!DecisionFrameGuard.IsFresh(frame.CapturedAt, Stopwatch.GetTimestamp(), DecisionFrameGuard.MaximumOcrAgeMs))
                {
                    LogDiagnostic(session, "OCR_AGE", "A leitura do OCR ficou antiga antes da confirmação; será refeita.");
                    throw new DecisionFrameChangedException();
                }
                foreach (var region in regions)
                {
                    EnsureDecisionCurrent(session, frame.InputEpoch, token);
                    if (!frame.ClientRegion.Contains(region)) throw new DecisionFrameChangedException();
                    var local = new Rectangle(region.Left - frame.ClientRegion.Left, region.Top - frame.ClientRegion.Top, region.Width, region.Height);
                    var baseline = ScreenCapture.Crop(frame, local);
                    var current = ScreenCapture.Capture(session.Guard, region);
                    if (!DecisionFrameGuard.PatchesMatch(baseline, current))
                    {
                        LogDiagnostic(session, "PATCH_CHANGED", "A região que sustentava a leitura mudou; refazendo OCR sem clicar (ROI " + region.Width + "×" + region.Height + ").");
                        throw new DecisionFrameChangedException();
                    }
                    oldestProof = Math.Min(oldestProof, current.CapturedAt);
                }
                status = session.Guard.ValidateRegions(regions, frame.Geometry);
                if (status != GuardStatus.Ready) throw new WindowUnavailableException(status, regions, frame.Geometry);
            }
            catch (WindowUnavailableException exception)
            {
                throw new WindowUnavailableException(exception.Status, regions, frame.Geometry);
            }
            EnsureDecisionCurrent(session, frame.InputEpoch, token);
            long now = Stopwatch.GetTimestamp();
            if (!DecisionFrameGuard.IsFresh(frame.CapturedAt, now, DecisionFrameGuard.MaximumOcrAgeMs) ||
                !DecisionFrameGuard.IsFresh(oldestProof, now, DecisionFrameGuard.MaximumProofAgeMs))
            {
                LogDiagnostic(session, "PROOF_AGE", "A prova visual expirou durante a verificação; será recapturada.");
                throw new DecisionFrameChangedException();
            }
            frame.EvidenceVerifiedAt = oldestProof;
            return regions;
        }

        private async Task TapBurstAsync(Session session, CapturedFrame fullFrame, ScreenObservation observation, CancellationToken token)
        {
            EnsureDecisionCurrent(session, fullFrame.InputEpoch, token);
            Rectangle prompt = observation.TapPrompt ?? new Rectangle(fullFrame.Width / 2 - 80, (int)(fullFrame.Height * 0.70), 160, 40);
            var point = TapTargetLocator.Locate(fullFrame.Pixels, fullFrame.Width, fullFrame.Height, prompt);
            var evidence = new[] { prompt };
            session.SafetyRegions = evidence;
            session.SafetyGeometry = fullFrame.Geometry;
            var burst = Stopwatch.StartNew();
            int maxBurstMs = 450;
            do
            {
                EnsureDecisionCurrent(session, fullFrame.InputEpoch, token);
                CheckTimeout(session);
                var status = session.Mouse.FastTap(point, fullFrame.Geometry, token);
                if (status != GuardStatus.Ready)
                {
                    await HandleUnsafePointAsync(session, status, token, evidence, fullFrame.Geometry).ConfigureAwait(false);
                    return;
                }
                session.Controller.RecordAction(AutomationAction.TapCrate);
                session.Snapshot.Clicks++;
                session.Snapshot.CurrentCrateClicks++;
                Publish(session, AutomationState.Tapping, "Toques na caixa: " + session.Snapshot.CurrentCrateClicks + ".", true, true);
                int delay = Math.Max(1, session.Options.TapIntervalMs);
                await Task.Delay(delay, token).ConfigureAwait(false);
            } while (burst.ElapsedMilliseconds < maxBurstMs);
        }

        private async Task WaitForReadyAsync(Session session, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await WaitForUserPauseAsync(session, token).ConfigureAwait(false);
                WindowGeometry geometry;
                var status = session.Guard.Inspect(out geometry);
                if (status == GuardStatus.Ready && WindowGuard.IsPrimaryButtonDown()) status = GuardStatus.MouseHeld;
                if (status == GuardStatus.Ready && session.SafetyRegions != null)
                {
                    if (!geometry.SameAs(session.SafetyGeometry))
                    {
                        session.SafetyRegions = null;
                        session.SafetyGeometry = null;
                    }
                    else status = session.Guard.ValidateRegions(session.SafetyRegions, geometry);
                }
                if (status == GuardStatus.Ready)
                    return;
                if (status == GuardStatus.InvalidWindow) throw new InvalidOperationException("A janela original do Roblox foi fechada ou substituída.");
                if (!DecisionFrameGuard.IsUserPause(status)) CheckTimeout(session);
                await HandleUnsafePointAsync(session, status, token, session.SafetyRegions, session.SafetyGeometry).ConfigureAwait(false);
            }
        }

        private async Task HandleUnsafePointAsync(Session session, GuardStatus status, CancellationToken token,
            IReadOnlyList<Rectangle> regions = null, WindowGeometry expected = null)
        {
            if (status == GuardStatus.InvalidWindow) throw new InvalidOperationException("A janela original do Roblox foi fechada ou substituída.");
            if (status == GuardStatus.UserPaused)
            {
                await WaitForUserPauseAsync(session, token).ConfigureAwait(false);
                return;
            }
            if (!DecisionFrameGuard.IsUserPause(status))
            {
                session.SafetyRegions = null;
                session.SafetyGeometry = null;
                return;
            }
            // Only confirmed human/occlusion pauses suspend time; unchanged/unknown game states do not.
            using (session.Gate.PauseAmbientClock())
            {
                GuardStatus? lastStatus = null;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (lastStatus != status)
                    {
                        string message = status == GuardStatus.MouseHeld ? "Pausado: solte o botão esquerdo do mouse. Use Parar para encerrar." :
                            status == GuardStatus.CoveredPoint ? "Pausado: outra janela cobre a leitura. Remova-a para continuar; use Parar para encerrar." :
                            status == GuardStatus.Minimized ? "Pausado: restaure o Roblox. Use Parar para encerrar." :
                            "Pausado: volte à janela do Roblox para continuar. Use Parar para encerrar.";
                        if (!String.IsNullOrWhiteSpace(session.Guard.LastStatusDetails))
                            LogDiagnostic(session, "WINDOW_" + status, session.Guard.LastStatusDetails);
                        Publish(session, AutomationState.Paused, message, true);
                        lastStatus = status;
                    }
                    await Task.Delay(150, token).ConfigureAwait(false);
                    WindowGeometry current;
                    status = session.Guard.Inspect(out current);
                    if (status == GuardStatus.InvalidWindow) throw new InvalidOperationException("A janela original do Roblox foi fechada ou substituída.");
                    if (status == GuardStatus.Ready && WindowGuard.IsPrimaryButtonDown()) status = GuardStatus.MouseHeld;
                    if (status == GuardStatus.Ready && regions != null && expected != null)
                    {
                        if (!current.SameAs(expected))
                        {
                            session.SafetyRegions = null;
                            session.SafetyGeometry = null;
                            break;
                        }
                        status = session.Guard.ValidateRegions(regions, expected);
                    }
                    if (status == GuardStatus.Ready || status == GuardStatus.ChangedGeometry) break;
                }
            }
            await WaitForUserPauseAsync(session, token).ConfigureAwait(false);
            Publish(session, AutomationState.Searching, "Regiões livres e mouse disponível; fazendo uma nova leitura antes de continuar.", true);
        }

        private async Task WaitForUserPauseAsync(Session session, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!session.Gate.IsUserPaused) return;
            Publish(session, AutomationState.Paused, "Pausado pelo usuário. Use o mesmo botão ou atalho de pausa para continuar.", true);
            while (session.Gate.IsUserPaused)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(80, token).ConfigureAwait(false);
            }
            // Both pause and unpause invalidate the epoch: the caller must capture/OCR a new frame.
        }

        private static void EnsureDecisionCurrent(Session session, long inputEpoch, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!session.Gate.IsCurrent(inputEpoch)) throw new DecisionFrameChangedException();
        }

        private static void LogDiagnostic(Session session, string key, string message)
        {
            long now = Stopwatch.GetTimestamp();
            if (session.LastDiagnosticKey == key && (now - session.LastDiagnosticAt) * 1000.0 / Stopwatch.Frequency < 2000)
            { session.SuppressedDiagnostics++; return; }
            string suffix = session.SuppressedDiagnostics == 0 ? String.Empty : " (" + session.SuppressedDiagnostics + " repetições resumidas)";
            session.LastDiagnosticKey = key;
            session.LastDiagnosticAt = now;
            session.SuppressedDiagnostics = 0;
            LocalLog.Write("[warning] " + message + suffix);
        }

        private static void TrackProgress(Session session, ScreenObservation observation)
        {
            if (session.Controller.CrateInFlight)
            {
                if (observation.Kind == ScreenKind.TapPrompt)
                {
                    if (!session.TapPhaseSeen && session.LastRecognizedKind != ScreenKind.TapPrompt) MarkProgress(session);
                    session.TapPhaseSeen = true;
                }
                if (observation.Kind == ScreenKind.Collection)
                {
                    string key = ContentKey(observation);
                    bool firstCollection = session.LastRecognizedKind == ScreenKind.TapPrompt || session.LastContentKey == null;
                    bool confirmedItemChange = false;
                    if (!firstCollection && key != session.LastContentKey)
                    {
                        if (session.PendingCollectionKey == key) session.PendingCollectionFrames = Math.Min(2, session.PendingCollectionFrames + 1);
                        else { session.PendingCollectionKey = key; session.PendingCollectionFrames = 1; }
                        confirmedItemChange = session.PendingCollectionFrames >= 2;
                    }
                    else
                    {
                        session.PendingCollectionKey = null;
                        session.PendingCollectionFrames = 0;
                    }
                    if (firstCollection || confirmedItemChange)
                    {
                        MarkProgress(session);
                        session.CollectionPending = false;
                        session.CollectionRetry = 0;
                        session.CollectionTransitionSeen = false;
                        session.LastContentKey = key;
                        session.PendingCollectionKey = null;
                        session.PendingCollectionFrames = 0;
                        session.TapPhaseSeen = false;
                    }
                }
                // Unknown OCR and moving-world hashes are not proof that NEXT actually advanced an item.
            }
            if (observation.Kind != ScreenKind.Unknown && observation.Kind != ScreenKind.Ambiguous)
                session.LastRecognizedKind = observation.Kind;
        }

        private static string ContentKey(ScreenObservation observation)
        {
            string label = observation.ClaimButton.HasValue ? "CLAIM" : observation.NextButton.HasValue ? "NEXT" : "NONE";
            var button = observation.ClaimButton ?? observation.NextButton;
            if (!button.HasValue || observation.Words == null) return label;
            var r = button.Value;
            double center = r.Left + r.Width * .5;
            double halfWidth = Math.Max(r.Width * 2.5, observation.Width * .18);
            double top = Math.Max(0, r.Top - Math.Max(r.Height * 12, observation.Height * .65));
            double bottom = r.Top - Math.Max(2, r.Height * .5);
            // Relative to the actual loot button, not a fixed screen coordinate or the 3D background.
            var tokens = observation.Words.Where(w => w != null && w.Bounds.Top >= top && w.Bounds.Bottom <= bottom &&
                Math.Abs(w.Bounds.Left + w.Bounds.Width * .5 - center) <= halfWidth)
                .SelectMany(w => RecognitionClassifier.Normalize(w.Text).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                .Where(text => text.Any(Char.IsLetter) && !ControlVocabulary.Contains(text))
                .Distinct(StringComparer.Ordinal).OrderBy(text => text, StringComparer.Ordinal);
            return label + "|" + String.Join(" ", tokens);
        }

        private static readonly HashSet<string> ControlVocabulary = new HashSet<string>(StringComparer.Ordinal)
        { "NEXT", "CLAIM", "SPAM", "TAP", "TO", "OPEN", "ALL", "LOW", "MID", "HIGH", "GRADE", "CRATE", "REMAINING", "BUY", "PURCHASE", "SHOP", "ROBUX", "PRICE", "COST" };

        private static bool CanAdvanceCollection(Session session, ScreenObservation observation)
        {
            if (session.Elapsed - session.LastCollectionClick < TimeSpan.FromMilliseconds(650)) return false;
            if (!session.CollectionPending) return true;
            // One retry is allowed for an input that did not register. Further identical input stops until real visual progress.
            return session.CollectionRetry < 1 && session.Elapsed - session.LastCollectionClick >= TimeSpan.FromMilliseconds(1500);
        }

        private static void RecordCollectionInput(Session session, ScreenObservation observation)
        {
            if (session.CollectionPending) session.CollectionRetry++;
            session.CollectionPending = true;
            session.CollectionTransitionSeen = false;
            session.LastCollectionClick = session.Elapsed;
            session.LastCollectionSignature = observation.VisualSignature;
            session.LastContentKey = ContentKey(observation);
            session.PendingCollectionKey = null;
            session.PendingCollectionFrames = 0;
        }

        private static bool SameTarget(Rectangle? first, Rectangle? second, int width, int height)
        {
            if (!first.HasValue || !second.HasValue) return false;
            var a = first.Value; var b = second.Value;
            return Math.Abs(a.Left + a.Width / 2.0 - b.Left - b.Width / 2.0) <= Math.Max(5, width * .012) &&
                Math.Abs(a.Top + a.Height / 2.0 - b.Top - b.Height / 2.0) <= Math.Max(5, height * .012);
        }
        private static bool PatchChanged(CapturedFrame baseline, CapturedFrame current)
        {
            if (baseline.Width != current.Width || baseline.Height != current.Height) return true;
            int changed = 0, samples = 0;
            for (int y = 2; y < baseline.Height - 2; y += 3)
                for (int x = 2; x < baseline.Width - 2; x += 3)
                {
                    int a = PromptFingerprint.Luma(baseline.Pixels, baseline.Width, x, y);
                    int b = PromptFingerprint.Luma(current.Pixels, current.Width, x, y);
                    if (Math.Abs(a - b) > 65) changed++;
                    samples++;
                }
            return samples > 0 && changed > samples * .32;
        }
        private static void MarkProgress(Session session) { session.LastProgress = session.Elapsed; }
        private static void CheckTimeout(Session session)
        {
            if (session.Gate.IsStopped) return;
            if (session.Elapsed - session.LastProgress > TimeSpan.FromSeconds(Math.Max(90, session.Options.DetectionTimeoutSeconds * 4)))
                throw new InvalidOperationException("Tempo limite sem uma transição confiável. Nenhum novo clique será enviado. Verifique o menu, o estoque e os itens antes de reiniciar.");
        }
        private static void UpdateCounters(Session session)
        {
            session.Snapshot.Remaining = session.Controller.Remaining;
            session.Snapshot.Opened = session.Controller.Opened;
            session.Snapshot.TargetCount = session.Controller.TargetCount;
        }

        private void Publish(Session session, AutomationState state, string message, bool isRunning, bool throttle = false)
        {
            if (throttle && session.PublishClock.ElapsedMilliseconds < 100) return;
            bool stateChanged = session.Snapshot.State != state;
            bool messageChanged = !String.Equals(session.Snapshot.Message, message, StringComparison.Ordinal);
            session.Snapshot.State = state;
            session.Snapshot.Message = message;
            session.Snapshot.IsRunning = isRunning;
            UpdateCounters(session);
            if (stateChanged || (messageChanged && state != AutomationState.Tapping))
                LocalLog.Write("[" + (state == AutomationState.Error ? "error" : state == AutomationState.Paused ? "warning" : "info") + "] " + message);
            session.PublishClock.Restart();
            var handler = SnapshotChanged;
            if (handler == null) return;
            var snapshot = new AutomationSnapshot
            {
                State = state, Message = message, IsRunning = isRunning, Remaining = session.Snapshot.Remaining,
                Opened = session.Snapshot.Opened, Clicks = session.Snapshot.Clicks,
                CurrentCrateClicks = session.Snapshot.CurrentCrateClicks, ItemsAdvanced = session.Snapshot.ItemsAdvanced,
                TargetCount = session.Snapshot.TargetCount, RecognitionMs = session.Snapshot.RecognitionMs
            };
            foreach (Action<AutomationSnapshot> subscriber in handler.GetInvocationList())
            {
                try { subscriber(snapshot); }
                catch (Exception ex) { LocalLog.Write("[warning] O observador da interface não recebeu o estado: " + ex.GetType().Name + "."); }
            }
        }

        private sealed class RecognizedFrame
        {
            internal CapturedFrame Frame;
            internal ScreenObservation Observation;
        }
        private sealed class Session
        {
            internal readonly AutomationOptions Options;
            internal readonly AutomationController Controller;
            internal readonly AutomationSnapshot Snapshot = new AutomationSnapshot { State = AutomationState.Idle };
            internal readonly Stopwatch ActiveClock;
            internal readonly AutomationRunGate Gate;
            internal TimeSpan Elapsed { get { return Gate.Elapsed; } }
            internal readonly Stopwatch PublishClock = Stopwatch.StartNew();
            internal WindowGuard Guard;
            internal SafeMouse Mouse;
            internal WindowsOcrRecognizer Ocr;
            internal IReadOnlyList<Rectangle> SafetyRegions;
            internal WindowGeometry SafetyGeometry;
            internal TimeSpan LastProgress, CrateStartedAt, LastCollectionClick = TimeSpan.FromSeconds(-10);
            internal ScreenKind LastRecognizedKind;
            internal string LastContentKey;
            internal bool CollectionPending, CollectionTransitionSeen;
            internal int CollectionRetry;
            internal ulong LastCollectionSignature;
            internal double? EstimatedTaps;
            internal bool TapPhaseSeen;
            internal string PendingCollectionKey;
            internal int PendingCollectionFrames;
            internal string LastDiagnosticKey;
            internal long LastDiagnosticAt;
            internal int SuppressedDiagnostics;
            internal Session(AutomationOptions options) : this(options, new AutomationRunGate()) { }
            internal Session(AutomationOptions options, AutomationRunGate gate)
            { Options = options; Controller = new AutomationController(options.MaxCrates); Gate = gate; ActiveClock = gate.Clock; }
        }
    }
}
