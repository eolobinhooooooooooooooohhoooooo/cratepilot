using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CratePilot.Core
{
    internal static class LocalLog
    {
        private static readonly object Sync = new object();
        private const int Capacity = 500;
        private const int MaximumMessageLength = 1200;
        private static readonly Queue<string> Entries = new Queue<string>(Capacity);
        private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        internal static event Action<string> MessageWritten;

        internal static string[] Snapshot() { lock (Sync) return Entries.ToArray(); }

        internal static void Write(string message)
        {
            string entry;
            Action<string> subscribers;
            lock (Sync)
            {
                entry = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) + " " + Compact(message);
                if (Entries.Count == Capacity) Entries.Dequeue();
                Entries.Enqueue(entry);
                subscribers = MessageWritten;
            }
            // No I/O, background tasks, dispatcher work, or callbacks under the storage lock.
            if (subscribers == null) return;
            foreach (Action<string> subscriber in subscribers.GetInvocationList())
                try { subscriber(entry); }
                catch (Exception) { /* Logging must never stop the automation or other observers. */ }
        }

        private static string Compact(string message)
        {
            var source = message ?? String.Empty;
            var result = new StringBuilder(Math.Min(source.Length, MaximumMessageLength));
            bool whitespace = false;
            for (int index = 0; index < source.Length && index < MaximumMessageLength * 4; index++)
            {
                char character = source[index];
                if (Char.IsWhiteSpace(character) || Char.IsControl(character))
                {
                    if (result.Length > 0) whitespace = true;
                    continue;
                }
                if (whitespace && result.Length < MaximumMessageLength) result.Append(' ');
                whitespace = false;
                if (result.Length >= MaximumMessageLength) break;
                result.Append(character);
            }
            if (result.Length == 0) result.Append("[info] Sem detalhes adicionais.");
            else if (result[0] != '[') result.Insert(0, "[info] ");
            string text = result.ToString();
            if (String.IsNullOrEmpty(UserProfile)) return text;
            int offset, start = 0;
            while ((offset = text.IndexOf(UserProfile, start, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                text = text.Substring(0, offset) + "%USERPROFILE%" + text.Substring(offset + UserProfile.Length);
                start = offset + "%USERPROFILE%".Length;
            }
            return text.Length > MaximumMessageLength + 8 ? text.Substring(0, MaximumMessageLength + 8) : text;
        }
    }
}
