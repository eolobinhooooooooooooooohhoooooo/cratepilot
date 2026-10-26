using System;
using CratePilot.Core;

namespace CratePilot.UI
{
    public sealed class LocalActivityLog : IDisposable
    {
        private bool disposed;
        public LocalActivityLog(bool subscribe = true)
        {
            if (subscribe) LocalLog.MessageWritten += OnMessage;
            IsSubscribed = subscribe;
        }
        public event Action<string> MessageWritten;
        public bool IsSubscribed { get; private set; }
        public string[] Snapshot() { return LocalLog.Snapshot(); }
        public void Write(string message, string tone)
        {
            if (!disposed && !string.IsNullOrWhiteSpace(message)) LocalLog.Write("[" + (tone ?? "info") + "] " + message);
        }
        private void OnMessage(string line)
        {
            var callback = MessageWritten;
            if (!disposed && callback != null) callback(line);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (IsSubscribed) LocalLog.MessageWritten -= OnMessage;
            IsSubscribed = false;
        }
    }
}
