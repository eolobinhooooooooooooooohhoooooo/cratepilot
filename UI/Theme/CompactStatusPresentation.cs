using System;
using System.Globalization;
using System.Windows.Data;

namespace CratePilot.UI.Theme
{
    // Presentation-only projections. The original snapshot, message, and user-pause flag stay unchanged.
    public sealed class CompactStatusTitleConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            string state = values.Length > 0 ? values[0] as string : null;
            string original = values.Length > 1 ? values[1] as string : null;
            bool userPaused = values.Length > 2 && values[2] is bool && (bool)values[2];
            string connection = values.Length > 3 ? values[3] as string : null;
            switch (state)
            {
                case "SEM JANELA":
                case "AGUARDANDO": return connection ?? "Aguardando Roblox";
                case "PREPARANDO":
                case "CONTAGEM": return "Preparando";
                case "PROCURANDO": return "Lendo a janela";
                case "ABRINDO":
                case "CLICANDO": return "Abrindo caixa";
                case "COLETANDO": return "Coletando";
                case "EM ESPERA": return userPaused ? "Pausado" : "Aguardando Roblox";
                case "CONCLUÍDO": return "Concluído";
                case "PARADO": return "Parado";
                case "PARANDO": return "Parando";
                case "ATENÇÃO": return "Atenção";
                default: return original ?? "Pronto";
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public sealed class CompactStatusDetailConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            string message = values.Length > 0 ? values[0] as string ?? string.Empty : string.Empty;
            string tone = values.Length > 1 ? values[1] as string : null;
            string state = values.Length > 2 ? values[2] as string : null;
            bool userPaused = values.Length > 3 && values[3] is bool && (bool)values[3];
            message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            // Unknown errors remain complete and scrollable; the unabridged source is also the tooltip.
            if (tone == "error" || state == "CONTAGEM") return message;
            if (tone != "warning" || state == "PARANDO" || (state == "EM ESPERA" && userPaused)) return string.Empty;
            if (Contains(message, "foco")) return "Roblox sem foco.";
            if (Contains(message, "minimiz")) return "Restaure a janela do Roblox.";
            if (Contains(message, "mouse") || Contains(message, "botão esquerdo")) return "Solte o botão do mouse.";
            if (Contains(message, "cobre") || Contains(message, "oclus") || Contains(message, "sobrepos")) return "Há uma janela sobre o Roblox.";
            return message;
        }

        private static bool Contains(string source, string text)
        {
            return source.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
