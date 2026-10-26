using System.Text.RegularExpressions;

namespace CratePilot.UI
{
    public static class LogPrivacy
    {
        private static readonly Regex LocalPath = new Regex(@"(?i)(?:file:///[a-z]:/|[a-z]:[\\/]|\\\\)[^\r\n\""<>|]*", RegexOptions.CultureInvariant);
        private static readonly Regex Secret = new Regex(@"(?i)\b(access[_-]?token|token|secret|password|authorization|cookie|api[-_]?key)[""']?\s*[:=]\s*[^\r\n,;}]+", RegexOptions.CultureInvariant);
        public static string ForClipboard(string value)
        {
            return Secret.Replace(LocalPath.Replace(value ?? string.Empty, "[caminho local]"), "$1=[redigido]");
        }
    }
}
