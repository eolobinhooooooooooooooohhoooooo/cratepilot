using System;
using System.Collections.ObjectModel;
using System.Globalization;
using CratePilot.Core;

namespace CratePilot.UI
{
    public sealed class MainViewModel : ObservableObject
    {
        private WindowTarget selectedWindow;
        private bool automaticCount = true;
        private string manualLimitText = "10";
        private double tapInterval = 8;
        private bool minimizeOnStart = true;
        private bool isRunning;
        private bool stopping;
        private bool isPaused;
        private string statusTitle = "Aguardando Roblox";
        private string statusMessage = string.Empty;
        private string stateLabel = "AGUARDANDO";
        private string statusTone = "idle";
        private string opened = "—";
        private string remaining = "—";
        private string clicks = "—";
        private string items = "—";
        private string recognition = "— ms";
        private string hotKeyHint = "F9 pausar/despausar";
        private string connectionLabel = "Aguardando Roblox";
        private string connectionDetails = "Abra o Roblox para conectar automaticamente.";
        private string pauseBindingLabel = "F9";
        private bool isBindingKey;
        private string copyButtonLabel = "Copiar";

        public MainViewModel()
        {
            Events = new ObservableCollection<ActivityEntry>();
            Events.CollectionChanged += delegate
            {
                RaisePropertyChanged("HasEvents");
            };
        }

        public ObservableCollection<ActivityEntry> Events { get; private set; }

        public WindowTarget SelectedWindow
        {
            get { return selectedWindow; }
            set { if (SetProperty(ref selectedWindow, value)) RaisePropertyChanged("CanStart"); }
        }

        public bool AutomaticCount
        {
            get { return automaticCount; }
            set
            {
                if (!SetProperty(ref automaticCount, value)) return;
                RaisePropertyChanged("ManualCount");
                RaisePropertyChanged("CanEditManualCount");
            }
        }

        public bool ManualCount { get { return !AutomaticCount; } set { if (value) AutomaticCount = false; } }
        public bool CanEditManualCount { get { return ManualCount && !IsRunning; } }
        public string ManualLimitText { get { return manualLimitText; } set { SetProperty(ref manualLimitText, value); } }
        public double TapInterval
        {
            get { return tapInterval; }
            set { if (SetProperty(ref tapInterval, Math.Max(1, Math.Min(80, Math.Round(value))))) RaisePropertyChanged("TapIntervalLabel"); }
        }
        public string TapIntervalLabel { get { return ((int)TapInterval).ToString(CultureInfo.InvariantCulture) + " ms"; } }
        public bool MinimizeOnStart { get { return minimizeOnStart; } set { SetProperty(ref minimizeOnStart, value); } }
        public bool IsRunning
        {
            get { return isRunning; }
            set
            {
                if (!SetProperty(ref isRunning, value)) return;
                RaisePropertyChanged("CanConfigure");
                RaisePropertyChanged("CanStart");
                RaisePropertyChanged("CanStop");
                RaisePropertyChanged("CanPause");
                RaisePropertyChanged("CanEditManualCount");
                RaisePropertyChanged("StartButtonLabel");
                RaisePropertyChanged("MainButtonLabel");
            }
        }
        public bool CanConfigure { get { return !IsRunning; } }
        public bool CanStart { get { return !IsRunning && SelectedWindow != null; } }
        public bool CanStop { get { return IsRunning && !stopping; } }
        public bool CanPause { get { return IsRunning && !stopping; } }
        public bool IsPaused
        {
            get { return isPaused; }
            set
            {
                if (SetProperty(ref isPaused, value))
                {
                    RaisePropertyChanged("PauseButtonLabel");
                    RaisePropertyChanged("PauseTooltip");
                    RaisePropertyChanged("StartButtonLabel");
                    RaisePropertyChanged("MainButtonLabel");
                }
            }
        }
        public string PauseButtonLabel { get { return IsPaused ? "Despausar" : "Pausar"; } }
        public string PauseTooltip { get { return (IsPaused ? "Despausar" : "Pausar") + " a automação (" + PauseBindingLabel + ")"; } }
        public string ConnectionLabel { get { return connectionLabel; } set { SetProperty(ref connectionLabel, value); } }
        public string ConnectionDetails { get { return connectionDetails; } set { SetProperty(ref connectionDetails, value); } }
        public string PauseBindingLabel
        {
            get { return pauseBindingLabel; }
            set { if (SetProperty(ref pauseBindingLabel, value)) { RaisePropertyChanged("BindingButtonLabel"); RaisePropertyChanged("PauseTooltip"); } }
        }
        public bool IsBindingKey
        {
            get { return isBindingKey; }
            set { if (SetProperty(ref isBindingKey, value)) RaisePropertyChanged("BindingButtonLabel"); }
        }
        public string BindingButtonLabel { get { return IsBindingKey ? "Tecla..." : PauseBindingLabel; } }
        public string CopyButtonLabel { get { return copyButtonLabel; } set { SetProperty(ref copyButtonLabel, value); } }
        public string StartButtonLabel { get { return IsRunning ? (IsPaused ? "Retomar" : "Pausar") : "Iniciar"; } }
        public string MainButtonLabel { get { return StartButtonLabel; } }
        public bool HasEvents { get { return Events.Count > 0; } }
        public string StatusTitle { get { return statusTitle; } private set { SetProperty(ref statusTitle, value); } }
        public string StatusMessage { get { return statusMessage; } private set { SetProperty(ref statusMessage, value); } }
        public string StateLabel { get { return stateLabel; } private set { SetProperty(ref stateLabel, value); } }
        public string StatusTone { get { return statusTone; } private set { SetProperty(ref statusTone, value); } }
        public string Opened { get { return opened; } private set { SetProperty(ref opened, value); } }
        public string Remaining { get { return remaining; } private set { SetProperty(ref remaining, value); } }
        public string Clicks { get { return clicks; } private set { SetProperty(ref clicks, value); } }
        public string Items { get { return items; } private set { SetProperty(ref items, value); } }
        public string Recognition { get { return recognition; } private set { SetProperty(ref recognition, value); } }
        public string HotKeyHint { get { return hotKeyHint; } set { SetProperty(ref hotKeyHint, value); } }

        public void TargetChanged()
        {
            RaisePropertyChanged("CanStart");
            if (IsRunning)
                return;
            bool missing = SelectedWindow == null;
            StatusTitle = missing ? "Aguardando Roblox" : "Roblox conectado";
            StatusMessage = missing
                ? "Abra o Roblox para conectar automaticamente."
                : string.Empty;
            StateLabel = missing ? "SEM JANELA" : "AGUARDANDO";
            StatusTone = "idle";
        }

        public void BeginSession()
        {
            stopping = false;
            IsPaused = false;
            IsRunning = true;
            RaisePropertyChanged("CanStop");
            RaisePropertyChanged("CanPause");
            StatusTitle = "Preparando a sessão";
            StatusMessage = "A contagem regressiva dá tempo de voltar ao jogo.";
            StateLabel = "PREPARANDO";
            StatusTone = "active";
            Opened = "0";
            Remaining = "—";
            Clicks = "0";
            Items = "0";
            Recognition = "— ms";
        }

        public void MarkStopping()
        {
            stopping = true;
            RaisePropertyChanged("CanStop");
            RaisePropertyChanged("CanPause");
            StatusTitle = "Encerrando a sessão";
            StatusMessage = "Aguardando o motor liberar a janela. Nenhuma nova sessão será iniciada.";
            StateLabel = "PARANDO";
            StatusTone = "warning";
        }

        public void EndSession()
        {
            IsRunning = false;
            stopping = false;
            IsPaused = false;
            RaisePropertyChanged("CanStop");
            RaisePropertyChanged("CanPause");
        }

        public void ShowError(string message)
        {
            StatusTitle = "Precisamos de um ajuste";
            StatusMessage = message;
            StateLabel = "ATENÇÃO";
            StatusTone = "error";
        }

        public void ShowStopped()
        {
            StatusTitle = "Sessão interrompida";
            StatusMessage = "O controle voltou para você. Ajuste as opções para continuar.";
            StateLabel = "PARADO";
            StatusTone = "idle";
        }

        public void ApplySnapshot(AutomationSnapshot snapshot, string message)
        {
            Opened = snapshot.Opened.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"));
            Remaining = snapshot.Remaining.HasValue ? snapshot.Remaining.Value.ToString("N0", CultureInfo.GetCultureInfo("pt-BR")) : "—";
            Clicks = snapshot.Clicks.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"));
            Items = snapshot.ItemsAdvanced.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"));
            Recognition = snapshot.RecognitionMs > 0 ? snapshot.RecognitionMs.ToString("0", CultureInfo.GetCultureInfo("pt-BR")) + " ms" : "— ms";
            if (stopping && snapshot.IsRunning)
                return;

            StatusMessage = message;
            StatusTone = snapshot.IsRunning ? "active" : "idle";
            switch (snapshot.State)
            {
                case AutomationState.CountingDown:
                    StatusTitle = "Volte para a janela do jogo"; StateLabel = "CONTAGEM"; break;
                case AutomationState.Searching:
                    StatusTitle = "Lendo a tela do jogo"; StateLabel = "PROCURANDO"; break;
                case AutomationState.Opening:
                    StatusTitle = DetectCrateTitle(message); StateLabel = "ABRINDO"; break;
                case AutomationState.Tapping:
                    StatusTitle = "Abrindo a caixa"; StateLabel = "CLICANDO"; break;
                case AutomationState.Collecting:
                    StatusTitle = "Recolhendo os itens"; StateLabel = "COLETANDO"; break;
                case AutomationState.Paused:
                    StatusTitle = IsPaused ? "Pausa solicitada pelo usuário" : "Aguardando o jogo"; StateLabel = "EM ESPERA"; StatusTone = "warning"; break;
                case AutomationState.Completed:
                    StatusTitle = "Abertura concluída"; StateLabel = "CONCLUÍDO"; StatusTone = "success"; break;
                case AutomationState.Stopped:
                    StatusTitle = "Sessão interrompida"; StateLabel = "PARADO"; break;
                case AutomationState.Error:
                    StatusTitle = "Precisamos de um ajuste"; StateLabel = "ATENÇÃO"; StatusTone = "error"; break;
                default:
                    StatusTitle = "Pronto para a primeira caixa"; StateLabel = "AGUARDANDO"; break;
            }
        }

        private static string DetectCrateTitle(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "Caixa localizada";
            if (message.IndexOf("High Grade", StringComparison.OrdinalIgnoreCase) >= 0)
                return "High Grade Crate localizada";
            if (message.IndexOf("Mid Grade", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Mid Grade Crate localizada";
            if (message.IndexOf("Low Grade", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Low Grade Crate localizada";
            if (message.IndexOf("prata", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Caixa prata localizada";
            return "Caixa localizada";
        }

        public void AddEvent(string message, string tone)
        {
            Events.Insert(0, new ActivityEntry(message, tone));
            while (Events.Count > 500)
                Events.RemoveAt(Events.Count - 1);
        }

        public void AddLogLine(string formattedLine)
        {
            Events.Insert(0, ActivityEntry.FromFormattedLine(formattedLine));
            while (Events.Count > 500) Events.RemoveAt(Events.Count - 1);
        }
    }
}
