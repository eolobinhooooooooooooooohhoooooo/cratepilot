using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CratePilot.UI
{
    public sealed class ActivityEntry
    {
        private static readonly Regex Prefix = new Regex(@"^(?<date>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?: [+-]\d{2}:\d{2})?)\s+(?<message>[\s\S]*)$", RegexOptions.CultureInvariant);
        public ActivityEntry(string message, string tone)
        {
            Time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            Message = message;
            Tone = tone;
            FormattedLine = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " [" + tone + "] " + message;
        }

        public static ActivityEntry FromFormattedLine(string line)
        {
            string original = line ?? string.Empty;
            Match match = Prefix.Match(original);
            string message = match.Success ? match.Groups["message"].Value : original;
            string tone = "info";
            foreach (string value in new[] { "error", "warning", "success", "diagnostic", "info" })
                if (message.StartsWith("[" + value + "] ", StringComparison.OrdinalIgnoreCase))
                { tone = value; message = message.Substring(value.Length + 3); break; }
            var entry = new ActivityEntry(message, tone);
            entry.Time = match.Success ? match.Groups["date"].Value.Substring(11, 8) : "—";
            entry.FormattedLine = original;
            return entry;
        }

        public string Time { get; private set; }
        public string Message { get; private set; }
        public string Tone { get; private set; }
        public string FormattedLine { get; private set; }
    }
}
