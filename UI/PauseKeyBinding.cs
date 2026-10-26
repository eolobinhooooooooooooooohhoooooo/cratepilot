using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace CratePilot.UI
{
    public sealed class PauseKeyBinding
    {
        public PauseKeyBinding(Key key, ModifierKeys modifiers) { Key = key; Modifiers = modifiers; }
        public Key Key { get; private set; }
        public ModifierKeys Modifiers { get; private set; }
        public string Display
        {
            get
            {
                var parts = new List<string>();
                if ((Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
                if ((Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
                if ((Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
                parts.Add(Key.ToString());
                return string.Join("+", parts);
            }
        }
        public bool Matches(Key key, ModifierKeys modifiers) { return Key == key && Modifiers == modifiers; }

        public static bool TryCreate(Key key, ModifierKeys modifiers, out PauseKeyBinding binding, out string error)
        {
            binding = null;
            error = string.Empty;
            if (!Enum.IsDefined(typeof(Key), key) || (modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) != 0)
            { error = "Tecla ou modificador não suportado."; return false; }
            if ((modifiers & ModifierKeys.Windows) != 0 || key == Key.LWin || key == Key.RWin
                || key == Key.System || key == Key.None || key == Key.ImeProcessed || key == Key.DeadCharProcessed
                || key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt
                || key == Key.LeftShift || key == Key.RightShift || key == Key.Escape || key == Key.Tab
                || key == Key.Enter || key == Key.Delete || key == Key.PrintScreen || key == Key.Sleep)
            { error = "Escolha uma tecla de função ou Ctrl/Alt + tecla."; return false; }
            if ((modifiers & ModifierKeys.Alt) != 0 && key == Key.F4) { error = "Esse atalho é reservado pelo Windows."; return false; }
            bool functionKey = key >= Key.F1 && key <= Key.F24;
            if (!functionKey && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
            { error = "Use F1–F24 ou Ctrl/Alt + tecla."; return false; }
            if ((modifiers & ModifierKeys.Control) != 0 && (modifiers & ModifierKeys.Alt) == 0
                && (key == Key.C || key == Key.V || key == Key.X || key == Key.A || key == Key.Z || key == Key.Y))
            { error = "Esse atalho é usado para editar texto."; return false; }
            if (KeyInterop.VirtualKeyFromKey(key) == 0) { error = "Tecla não suportada."; return false; }
            binding = new PauseKeyBinding(key, modifiers);
            return true;
        }
    }
}
