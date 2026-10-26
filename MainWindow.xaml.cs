using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CratePilot.Core;
using CratePilot.UI;

namespace CratePilot
{
    public partial class MainWindow : Window
    {
        private readonly bool diagnosticMode;
        private readonly MainViewModel viewModel;
        private readonly LocalActivityLog activityLog;
        private readonly DispatcherTimer snapshotTimer;
        private readonly DispatcherTimer discoveryTimer;
        private readonly CancellationTokenSource discoveryCancellation;
        private readonly object snapshotLock = new object();
        private readonly object logLock = new object();
        private readonly Queue<string> pendingLogLines = new Queue<string>();
        private readonly Queue<string> acceptedLogOrder = new Queue<string>();
        private readonly HashSet<string> acceptedLogLines = new HashSet<string>(new LogLineReferenceComparer());
        private AutomationEngine engine;
        private AutomationSnapshot pendingSnapshot;
        private AutomationSnapshot lastSnapshot;
        private AutoWindowCandidate selectedTarget;
        private AutoWindowCandidate lockedTarget;
        private HotKeyService hotKeys;
        private CancellationTokenSource sessionCancellation;
        private Task sessionTask;
        private Task discoveryTask;
        private bool preparing;
        private bool minimizedForSession;
        private bool closing;
        private bool allowClose;
        private bool disposed;
        private bool handlingUiError;
        private string targetFailureMessage;
        private WindowState stateBeforeSession;

        public static readonly DependencyProperty IsNarrowLayoutProperty = DependencyProperty.Register(
            "IsNarrowLayout", typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
        private static readonly DependencyPropertyKey CanStartAutomationKey = DependencyProperty.RegisterReadOnly(
            "CanStartAutomation", typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
        private static readonly DependencyPropertyKey CanPauseAutomationKey = DependencyProperty.RegisterReadOnly(
            "CanPauseAutomation", typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
        private static readonly DependencyPropertyKey CanStopAutomationKey = DependencyProperty.RegisterReadOnly(
            "CanStopAutomation", typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
        private static readonly DependencyPropertyKey CanRebindPauseKey = DependencyProperty.RegisterReadOnly(
            "CanRebindPause", typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
        public static readonly DependencyProperty CanStartAutomationProperty = CanStartAutomationKey.DependencyProperty;
        public static readonly DependencyProperty CanPauseAutomationProperty = CanPauseAutomationKey.DependencyProperty;
        public static readonly DependencyProperty CanStopAutomationProperty = CanStopAutomationKey.DependencyProperty;
        public static readonly DependencyProperty CanRebindPauseProperty = CanRebindPauseKey.DependencyProperty;

        public bool IsDiagnosticMode { get { return diagnosticMode; } }
        public bool IsEngineCreated { get { return engine != null; } }
        public bool IsDiscoveryScheduled { get { return discoveryTimer != null && discoveryTimer.IsEnabled; } }
        public int DiscoveryPasses { get; private set; }
        public bool CanStartAutomation { get { return (bool)GetValue(CanStartAutomationProperty); } }
        public bool CanPauseAutomation { get { return (bool)GetValue(CanPauseAutomationProperty); } }
        public bool CanStopAutomation { get { return (bool)GetValue(CanStopAutomationProperty); } }
        public bool CanRebindPause { get { return (bool)GetValue(CanRebindPauseProperty); } }
        public bool IsNarrowLayout { get { return (bool)GetValue(IsNarrowLayoutProperty); } private set { SetValue(IsNarrowLayoutProperty, value); } }

        public MainWindow() : this(false) { }

        public MainWindow(bool diagnosticMode)
        {
            this.diagnosticMode = diagnosticMode;
            InitializeComponent();
            viewModel = new MainViewModel();
            DataContext = viewModel;
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            activityLog = new LocalActivityLog(!diagnosticMode);
            activityLog.MessageWritten += Log_MessageWritten;
            if (!diagnosticMode)
            {
                foreach (string line in activityLog.Snapshot()) EnqueueLog(line);
                discoveryCancellation = new CancellationTokenSource();
                snapshotTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
                snapshotTimer.Tick += SnapshotTimer_Tick;
                snapshotTimer.Start();
                discoveryTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
                discoveryTimer.Tick += DiscoveryTimer_Tick;
            }
            SourceInitialized += Window_SourceInitialized;
            Loaded += Window_Loaded;
            Closing += Window_Closing;
            StateChanged += Window_StateChanged;
            SizeChanged += Window_SizeChanged;
            PreviewKeyDown += Window_PreviewKeyDown;
            DataObject.AddPastingHandler(ManualLimitInput, ManualLimit_Pasting);
            CreateTaskbarIcon();
            FitInitialWindowToWorkArea();
            UpdateAvailability();
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "CanStart" || e.PropertyName == "CanPause" || e.PropertyName == "CanStop"
                || e.PropertyName == "IsRunning" || e.PropertyName == "IsBindingKey") UpdateAvailability();
        }

        private void UpdateAvailability()
        {
            bool enabled = !diagnosticMode && !closing && !disposed;
            bool active = sessionTask != null && !sessionTask.IsCompleted;
            SetAvailability(CanStartAutomationKey, enabled && !preparing && !viewModel.IsBindingKey && (active || (selectedTarget != null && viewModel.CanStart)));
            SetAvailability(CanPauseAutomationKey, enabled && !preparing && !viewModel.IsBindingKey && engine != null && viewModel.CanPause);
            SetAvailability(CanStopAutomationKey, enabled && active && (preparing || viewModel.CanStop));
            SetAvailability(CanRebindPauseKey, enabled && !active && !viewModel.IsRunning && !preparing);
        }

        private void SetAvailability(DependencyPropertyKey key, bool value)
        {
            if ((bool)GetValue(key.DependencyProperty) != value) SetValue(key, value);
        }

        private void FitInitialWindowToWorkArea()
        {
            Rect area = SystemParameters.WorkArea;
            if (area.Width <= 0 || area.Height <= 0) return;
            Rect bounds = CalculateInitialWindowBounds(area);
            MinWidth = Math.Min(640, bounds.Width); MinHeight = Math.Min(420, bounds.Height);
            Width = bounds.Width; Height = bounds.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = bounds.Left; Top = bounds.Top;
            UpdateResponsiveLayout(new Size(Width, Height));
        }

        internal static Rect CalculateInitialWindowBounds(Rect area)
        {
            double width = Math.Min(880, Math.Max(1, area.Width - 32));
            double height = Math.Min(560, Math.Max(1, area.Height - 32));
            return new Rect(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e) { UpdateResponsiveLayout(e.NewSize); }
        private void UpdateResponsiveLayout(Size size)
        {
            bool narrow = size.Width < 840;
            if (IsNarrowLayout != narrow) IsNarrowLayout = narrow;
        }

        internal FrameworkElement ArrangePreviewLayout(Size size)
        {
            UpdateResponsiveLayout(size);
            if (!IsVisible) { Width = size.Width; Height = size.Height; }
            var content = (FrameworkElement)Content;
            content.HorizontalAlignment = HorizontalAlignment.Left; content.VerticalAlignment = VerticalAlignment.Top;
            content.Width = size.Width; content.Height = size.Height;
            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
            EnsurePreviewControlBounds(StartAutomationButton, content, size, 44);
            EnsurePreviewControlBounds(FooterBar, content, size, 16);
            return content;
        }

        private static void EnsurePreviewControlBounds(FrameworkElement control, FrameworkElement root, Size size, double minimumHeight)
        {
            Rect bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
            if (bounds.Width <= 0 || bounds.Height < minimumHeight || bounds.Left < -1 || bounds.Top < -1
                || bounds.Right > size.Width + 1 || bounds.Bottom > size.Height + 1)
                throw new InvalidOperationException("A critical interface control is outside the preview bounds: " + control.Name);
        }

        public void SetDiagnosticConnection(AutoWindowCandidate candidate)
        {
            if (!diagnosticMode) throw new InvalidOperationException("Diagnostic state injection is read-only-preview only.");
            ApplySelection(candidate);
        }

        public void SetDiagnosticSnapshot(AutomationSnapshot snapshot, bool userPaused)
        {
            if (!diagnosticMode) throw new InvalidOperationException("Diagnostic state injection is read-only-preview only.");
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.IsRunning && !viewModel.IsRunning) viewModel.BeginSession();
            if (!snapshot.IsRunning) viewModel.EndSession();
            viewModel.IsPaused = userPaused;
            viewModel.ApplySnapshot(snapshot, GetSnapshotMessage(snapshot));
            UpdateAvailability();
        }

        public void SetDiagnosticLogLines(IEnumerable<string> lines)
        {
            if (!diagnosticMode) throw new InvalidOperationException("Diagnostic state injection is read-only-preview only.");
            viewModel.Events.Clear();
            foreach (string line in (lines ?? Enumerable.Empty<string>()).Take(500)) viewModel.AddLogLine(line);
        }

        private void CreateTaskbarIcon()
        {
            try
            {
                var uri = new Uri("pack://application:,,,/Branding/CratePilot.ico", UriKind.Absolute);
                Icon = BitmapFrame.Create(uri);
            }
            catch
            {
                var artwork = TryFindResource("PixelAppIcon") as ImageSource;
                if (artwork == null) return;
                var visual = new DrawingVisual();
                using (DrawingContext context = visual.RenderOpen()) context.DrawImage(artwork, new Rect(0, 0, 32, 32));
                var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual); bitmap.Freeze(); Icon = bitmap;
            }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (diagnosticMode || closing) return;
            discoveryTimer.Start();
            await DiscoverAsync();
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            if (diagnosticMode) return;
            hotKeys = new HotKeyService(this);
            hotKeys.ToggleRequested += ToggleRequested;
            hotKeys.PauseRequested += OnPauseRequested;
            UpdateBindingDisplay();
            if (!hotKeys.ToggleRegistered || !hotKeys.PauseRegistered) AddEvent("Atalho global ocupado. Os botões e o atalho com o app ativo continuam disponíveis.", "warning");
        }

        private async void DiscoveryTimer_Tick(object sender, EventArgs e) { await DiscoverAsync(); }

        private async Task DiscoverAsync()
        {
            if (diagnosticMode || closing || disposed || (discoveryTask != null && !discoveryTask.IsCompleted)) return;
            try
            {
                DiscoveryPasses++;
                if (sessionTask != null && !sessionTask.IsCompleted)
                {
                    AutoWindowCandidate expected = lockedTarget;
                    Task<bool> validation = Task.Run(() => AutoWindowDiscovery.IsStillValid(expected), discoveryCancellation.Token);
                    discoveryTask = validation;
                    bool valid = await validation;
                    if (!closing && !valid && expected != null && (preparing || viewModel.IsRunning))
                    {
                        targetFailureMessage = "O Roblox selecionado foi encerrado. Inicie outra sessão para reconectar.";
                        RequestStop();
                        selectedTarget = null;
                        viewModel.SelectedWindow = null;
                        SetConnectionText(null);
                    }
                }
                else
                {
                    Task<IReadOnlyList<AutoWindowCandidate>> search = Task.Run(() => AutoWindowDiscovery.GetCandidates(discoveryCancellation.Token), discoveryCancellation.Token);
                    discoveryTask = search;
                    IReadOnlyList<AutoWindowCandidate> candidates = await search;
                    if (!closing && !disposed && (sessionTask == null || sessionTask.IsCompleted)) ApplySelection(AutoWindowPicker.Select(candidates, selectedTarget));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (!closing) AddEvent("Não foi possível verificar o Roblox: " + exception.Message, "warning");
            }
            finally { UpdateAvailability(); }
        }

        private void ApplySelection(AutoWindowCandidate candidate)
        {
            bool changed = candidate == null ? selectedTarget != null : !candidate.SameIdentity(selectedTarget);
            selectedTarget = candidate;
            viewModel.SelectedWindow = candidate == null ? null : new WindowTarget { Handle = candidate.Handle, Title = candidate.Title };
            SetConnectionText(candidate);
            if (changed || viewModel.StateLabel == "AGUARDANDO" || viewModel.StateLabel == "SEM JANELA") viewModel.TargetChanged();
            if (changed && !diagnosticMode) AddEvent(candidate == null ? "Roblox desconectado." : "Roblox conectado · PID " + candidate.ProcessId + ".", "info");
            UpdateAvailability();
        }

        private void SetConnectionText(AutoWindowCandidate candidate)
        {
            viewModel.ConnectionLabel = candidate == null ? "Aguardando Roblox" : "Roblox conectado";
            viewModel.ConnectionDetails = candidate == null ? "Abra o Roblox para conectar automaticamente."
                : candidate.ProcessName + " · PID " + candidate.ProcessId.ToString(CultureInfo.InvariantCulture) + " · "
                    + new DateTime(candidate.ProcessStartUtcTicks, DateTimeKind.Utc).ToString("u", CultureInfo.InvariantCulture) + "\n" + candidate.Title;
        }

        private void Start_Click(object sender, RoutedEventArgs e) { TogglePause(); }

        private Task StartSessionAsync()
        {
            if (diagnosticMode || closing || disposed || preparing || viewModel.IsBindingKey || (sessionTask != null && !sessionTask.IsCompleted)) return Task.FromResult(0);
            AutoWindowCandidate target = selectedTarget;
            if (target == null || !target.HasVerifiedIdentity)
            {
                viewModel.ShowError("Aguardando uma janela válida do Roblox.");
                return Task.FromResult(0);
            }
            int maxCrates = 0;
            if (viewModel.ManualCount && (!int.TryParse(viewModel.ManualLimitText, NumberStyles.None, CultureInfo.InvariantCulture, out maxCrates) || maxCrates < 1 || maxCrates > 1000000))
            {
                viewModel.ShowError("Informe de 1 a 1.000.000 de caixas.");
                ManualLimitInput.Focus(); ManualLimitInput.SelectAll();
                return Task.FromResult(0);
            }
            var options = new AutomationOptions { WindowHandle = target.Handle, MaxCrates = maxCrates, TapIntervalMs = (int)viewModel.TapInterval, DetectionTimeoutSeconds = 25 };
            preparing = true;
            lockedTarget = target;
            targetFailureMessage = null;
            sessionCancellation = new CancellationTokenSource();
            sessionTask = StartAndRunSessionAsync(options, target, sessionCancellation.Token);
            UpdateAvailability();
            return sessionTask;
        }

        private async Task StartAndRunSessionAsync(AutomationOptions options, AutoWindowCandidate target, CancellationToken token)
        {
            await Task.Yield();
            bool began = false;
            try
            {
                if (diagnosticMode || closing) return;
                bool valid = await Task.Run(() => AutoWindowDiscovery.IsStillValid(target), token);
                token.ThrowIfCancellationRequested();
                if (!valid) { ApplySelection(null); viewModel.ShowError("O Roblox selecionado não está mais disponível."); return; }
                if (!TargetIdentityOptions.TryApply(options, target))
                { viewModel.ShowError("O motor precisa validar o PID e o início do processo antes de iniciar."); AddEvent("Contrato de identidade do alvo indisponível; início bloqueado por segurança.", "error"); return; }
                if (closing || diagnosticMode) return;
                if (engine == null) { engine = new AutomationEngine(); engine.SnapshotChanged += Engine_SnapshotChanged; }
                lock (snapshotLock) pendingSnapshot = null;
                lastSnapshot = null;
                viewModel.BeginSession(); began = true;
                AddEvent("Sessão iniciada · PID " + target.ProcessId + " · " + options.TapIntervalMs + " ms.", "info");
                stateBeforeSession = WindowState;
                minimizedForSession = viewModel.MinimizeOnStart;
                if (minimizedForSession) WindowState = WindowState.Minimized;
                preparing = false;
                Task run = engine.StartAsync(options, token);
                UpdateAvailability();
                await run;
                FlushSnapshot();
                if (token.IsCancellationRequested && (lastSnapshot == null || lastSnapshot.State != AutomationState.Error)) viewModel.ShowStopped();
            }
            catch (OperationCanceledException) { if (began) { FlushSnapshot(); viewModel.ShowStopped(); } }
            catch (Exception exception) { FlushSnapshot(); viewModel.ShowError(exception.Message); AddEvent(exception.ToString(), "error"); }
            finally
            {
                preparing = false;
                if (began) viewModel.EndSession();
                if (sessionCancellation != null) { sessionCancellation.Dispose(); sessionCancellation = null; }
                lockedTarget = null;
                if (targetFailureMessage != null) viewModel.ShowError(targetFailureMessage);
                RestoreAfterSession();
                // The task is finishing on the UI dispatcher; the next input observes IsCompleted.
                _ = Dispatcher.BeginInvoke(new Action(UpdateAvailability), DispatcherPriority.Background);
            }
        }

        private void RequestStop()
        {
            if (diagnosticMode || sessionTask == null || sessionTask.IsCompleted) return;
            viewModel.MarkStopping();
            if (sessionCancellation != null) sessionCancellation.Cancel();
            try { if (engine != null) engine.Stop(); } catch (ObjectDisposedException) { }
            AddEvent("Parada solicitada.", "warning");
            UpdateAvailability();
        }

        private bool IsTyping()
        {
            return Keyboard.FocusedElement is TextBoxBase || Keyboard.FocusedElement is PasswordBox;
        }

        private async void ToggleRequested()
        {
            if (diagnosticMode || closing || disposed) return;
            if (viewModel.IsBindingKey) { AddEvent("Capturando novo atalho de pausa; toque ignorado.", "warning"); return; }
            if (!viewModel.IsRunning && IsTyping()) return;
            if (sessionTask != null && !sessionTask.IsCompleted) RequestStop();
            else await StartSessionAsync();
        }

        private void Pause_Click(object sender, RoutedEventArgs e) { TogglePause(); }
        private void TogglePause()
        {
            if (diagnosticMode || closing || disposed || preparing || viewModel.IsBindingKey) return;
            if (!viewModel.IsRunning)
            {
                if (CanStartAutomation) _ = StartSessionAsync();
                return;
            }
            if (!viewModel.CanPause || engine == null) return;
            bool paused = engine.TogglePause();
            viewModel.IsPaused = paused;
            AddEvent(paused ? "Pausado pelo usuário." : "Despausado pelo usuário.", "info");
        }

        private void OnPauseRequested()
        {
            if (diagnosticMode || closing) return;
            if (viewModel.IsBindingKey) { AddEvent("Essa tecla já está selecionada.", "warning"); return; }
            if (!viewModel.IsRunning && IsTyping()) return;
            TogglePause();
        }

        private void ChangePauseKey_Click(object sender, RoutedEventArgs e)
        {
            if (diagnosticMode || closing || preparing || viewModel.IsRunning || hotKeys == null) return;
            viewModel.IsBindingKey = !viewModel.IsBindingKey;
            UpdateAvailability();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (diagnosticMode || closing) return;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (viewModel.IsBindingKey)
            {
                e.Handled = true;
                if (e.IsRepeat) return;
                if (key == Key.Escape) { viewModel.IsBindingKey = false; return; }
                if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt || key == Key.LeftShift || key == Key.RightShift) return;
                PauseKeyBinding next;
                string error;
                if (!PauseKeyBinding.TryCreate(key, Keyboard.Modifiers, out next, out error) || !hotKeys.TryRebind(next, out error))
                { AddEvent(error, "warning"); return; }
                viewModel.IsBindingKey = false;
                UpdateBindingDisplay();
                AddEvent("Pausa vinculada a " + next.Display + ".", "info");
                return;
            }
            if (e.IsRepeat || (!viewModel.IsRunning && IsTyping())) return;
            if (hotKeys != null && !hotKeys.PauseRegistered && hotKeys.Binding.Matches(key, Keyboard.Modifiers))
            { e.Handled = true; TogglePause(); }
        }

        private void UpdateBindingDisplay()
        {
            string label = hotKeys == null ? "F9" : hotKeys.Binding.Display;
            viewModel.PauseBindingLabel = label;
            string pause = hotKeys != null && !hotKeys.PauseRegistered ? label + " (local)" : label;
            viewModel.HotKeyHint = pause + " pausar/despausar";
        }

        private void Engine_SnapshotChanged(AutomationSnapshot snapshot)
        {
            if (snapshot == null || disposed || diagnosticMode) return;
            var copy = new AutomationSnapshot
            {
                State = snapshot.State, Message = snapshot.Message, Remaining = snapshot.Remaining, Opened = snapshot.Opened,
                Clicks = snapshot.Clicks, CurrentCrateClicks = snapshot.CurrentCrateClicks, ItemsAdvanced = snapshot.ItemsAdvanced,
                TargetCount = snapshot.TargetCount, RecognitionMs = snapshot.RecognitionMs, IsRunning = snapshot.IsRunning
            };
            lock (snapshotLock) pendingSnapshot = copy;
        }

        private void SnapshotTimer_Tick(object sender, EventArgs e) { FlushSnapshot(); DrainLogMessages(); UpdateAvailability(); }
        private void FlushSnapshot()
        {
            AutomationSnapshot snapshot;
            lock (snapshotLock) { snapshot = pendingSnapshot; pendingSnapshot = null; }
            if (snapshot == null || disposed) return;
            lastSnapshot = snapshot;
            viewModel.ApplySnapshot(snapshot, GetSnapshotMessage(snapshot));
        }

        private static string GetSnapshotMessage(AutomationSnapshot snapshot)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.Message)) return snapshot.Message;
            switch (snapshot.State)
            {
                case AutomationState.CountingDown: return "Prepare a janela do Roblox.";
                case AutomationState.Searching: return "Lendo a janela do Roblox.";
                case AutomationState.Opening: return "Abrindo a caixa selecionada.";
                case AutomationState.Tapping: return "Enviando cliques no intervalo configurado.";
                case AutomationState.Collecting: return "Aguardando o próximo comando visível.";
                case AutomationState.Paused: return "Aguardando condições seguras no Roblox.";
                case AutomationState.Completed: return "Sessão concluída.";
                case AutomationState.Stopped: return "Sessão encerrada.";
                default: return string.Empty;
            }
        }

        private void Log_MessageWritten(string line) { EnqueueLog(line); }
        private void EnqueueLog(string line)
        {
            if (disposed || string.IsNullOrEmpty(line)) return;
            lock (logLock)
            {
                if (disposed || !acceptedLogLines.Add(line)) return;
                // Snapshot replay can race a live callback for the same stored entry.
                // Compare references, not text: repeated real messages must remain distinct.
                acceptedLogOrder.Enqueue(line);
                if (acceptedLogOrder.Count > 500) acceptedLogLines.Remove(acceptedLogOrder.Dequeue());
                pendingLogLines.Enqueue(line);
                while (pendingLogLines.Count > 500) pendingLogLines.Dequeue();
            }
        }
        private sealed class LogLineReferenceComparer : IEqualityComparer<string>
        {
            public bool Equals(string left, string right) { return ReferenceEquals(left, right); }
            public int GetHashCode(string value) { return RuntimeHelpers.GetHashCode(value); }
        }
        private void DrainLogMessages()
        {
            var batch = new List<string>(128);
            lock (logLock) { while (pendingLogLines.Count > 0 && batch.Count < 128) batch.Add(pendingLogLines.Dequeue()); }
            foreach (string line in batch) viewModel.AddLogLine(line);
        }
        private void AddEvent(string message, string tone)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (diagnosticMode) viewModel.AddLogLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " [" + tone + "] " + message);
            else activityLog.Write(message, tone);
        }

        public string BuildCopyLogText()
        {
            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            var text = new StringBuilder("CratePilot ").AppendLine(version);
            text.AppendLine(Environment.OSVersion.VersionString + " · processo " + (Environment.Is64BitProcess ? "64" : "32") + " bits");
            text.AppendLine("Pausa: " + viewModel.PauseBindingLabel);
            AutoWindowCandidate target = lockedTarget ?? selectedTarget;
            text.AppendLine(target == null ? "Alvo: não conectado" : "Alvo: PID " + target.ProcessId.ToString(CultureInfo.InvariantCulture)
                + " · início UTC " + new DateTime(target.ProcessStartUtcTicks, DateTimeKind.Utc).ToString("u", CultureInfo.InvariantCulture));
            text.AppendLine("--- Atividade em memória ---");
            IEnumerable<string> lines = diagnosticMode ? viewModel.Events.Reverse().Select(entry => entry.FormattedLine) : activityLog.Snapshot();
            foreach (string line in lines) text.AppendLine(LogPrivacy.ForClipboard(line));
            return text.ToString();
        }

        private async void CopyLogs_Click(object sender, RoutedEventArgs e)
        {
            string text = BuildCopyLogText();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (closing || disposed) return;
                try { Clipboard.SetText(text); viewModel.CopyButtonLabel = "Copiado"; return; }
                catch (ExternalException) { if (attempt < 2) await Task.Delay(50); }
                catch (System.Security.SecurityException) { break; }
            }
            viewModel.CopyButtonLabel = "Copiar";
            AddEvent("Não foi possível acessar a área de transferência. Tente novamente.", "warning");
        }

        public void HandleUiException(Exception exception)
        {
            if (handlingUiError || disposed) return;
            handlingUiError = true;
            try
            {
                if (!diagnosticMode) RequestStop();
                viewModel.ShowError(exception == null ? "Falha inesperada na interface." : exception.Message);
                AddEvent(exception == null ? "Falha inesperada na interface." : exception.ToString(), "error");
                DrainLogMessages();
            }
            finally { handlingUiError = false; }
        }

        private void RestoreAfterSession()
        {
            if (!minimizedForSession || closing) return;
            minimizedForSession = false;
            if (WindowState == WindowState.Minimized) { WindowState = stateBeforeSession == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal; Activate(); }
        }

        private async void Window_Closing(object sender, CancelEventArgs e)
        {
            if (allowClose) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            viewModel.IsBindingKey = false;
            UpdateAvailability();
            if (discoveryTimer != null) discoveryTimer.Stop();
            if (discoveryCancellation != null) discoveryCancellation.Cancel();
            RequestStop();
            var tasks = new List<Task>();
            if (sessionTask != null) tasks.Add(sessionTask);
            if (discoveryTask != null) tasks.Add(discoveryTask);
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { AddEvent(exception.ToString(), "error"); }
            DisposeResources();
            allowClose = true;
            _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Normal);
        }

        private void DisposeResources()
        {
            if (disposed) return;
            if (snapshotTimer != null) { snapshotTimer.Stop(); snapshotTimer.Tick -= SnapshotTimer_Tick; }
            if (discoveryTimer != null) discoveryTimer.Tick -= DiscoveryTimer_Tick;
            if (hotKeys != null) { hotKeys.ToggleRequested -= ToggleRequested; hotKeys.PauseRequested -= OnPauseRequested; hotKeys.Dispose(); }
            if (engine != null) { engine.SnapshotChanged -= Engine_SnapshotChanged; try { engine.Dispose(); } catch (Exception exception) { AddEvent(exception.ToString(), "error"); } }
            DrainLogMessages();
            activityLog.MessageWritten -= Log_MessageWritten;
            activityLog.Dispose();
            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            if (discoveryCancellation != null) discoveryCancellation.Dispose();
            lock (logLock)
            {
                disposed = true;
                pendingLogLines.Clear(); acceptedLogOrder.Clear(); acceptedLogLines.Clear();
            }
        }

        private void Stop_Click(object sender, RoutedEventArgs e) { RequestStop(); }
        private void Minimize_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
        private void Maximize_Click(object sender, RoutedEventArgs e) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
        private void Window_StateChanged(object sender, EventArgs e) { WindowFrame.CornerRadius = new CornerRadius(0); }
        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            var element = e.OriginalSource as DependencyObject;
            while (element != null && element != sender)
            {
                if (element is ButtonBase) return;
                element = element is Visual || element is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
            }
            if (e.ClickCount == 2) Maximize_Click(sender, e);
            else { try { DragMove(); } catch (InvalidOperationException) { } }
        }
        private void ManualLimit_PreviewTextInput(object sender, TextCompositionEventArgs e) { e.Handled = e.Text.Any(character => character < '0' || character > '9'); }
        private void ManualLimit_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            string text = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string;
            if (string.IsNullOrEmpty(text) || text.Any(character => character < '0' || character > '9')) e.CancelCommand();
        }
    }
}
