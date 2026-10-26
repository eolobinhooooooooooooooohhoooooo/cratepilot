using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace CratePilot.UI
{
    public interface IHotKeyRegistrar
    {
        bool Register(IntPtr handle, int id, uint modifiers, uint key);
        bool Unregister(IntPtr handle, int id);
    }

    public sealed class HotKeyService : IDisposable
    {
        private const int ToggleId = 0x4181;
        private const int FirstPauseId = 0x4182;
        private const int SecondPauseId = 0x4183;
        private const int HotKeyMessage = 0x0312;
        private const uint NoRepeat = 0x4000;
        private readonly IntPtr windowHandle;
        private readonly HwndSource source;
        private readonly IHotKeyRegistrar registrar;
        private readonly bool hookAvailable;
        private readonly HashSet<int> registrations = new HashSet<int>();
        private int activePauseId = FirstPauseId;
        private bool disposed;

        public HotKeyService(Window window) : this(new WindowInteropHelper(window).Handle, new NativeRegistrar(), true) { }

        // A registrar fixture can exercise atomic rebinding without registering real OS keys.
        public HotKeyService(IntPtr handle, IHotKeyRegistrar registrar, bool attachWindowHook)
        {
            windowHandle = handle;
            this.registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
            Binding = new PauseKeyBinding(Key.F9, ModifierKeys.None);
            hookAvailable = !attachWindowHook;
            if (attachWindowHook)
            {
                source = HwndSource.FromHwnd(handle);
                if (source == null) return;
                source.AddHook(HandleMessage);
                hookAvailable = true;
            }
            ToggleRegistered = false;
            PauseRegistered = Register(activePauseId, NoRepeat, 0x78);
            if (!PauseRegistered && attachWindowHook)
            {
                InstallLowLevelHook();
                PauseRegistered = (llHook != IntPtr.Zero);
            }
        }

        public event Action ToggleRequested;
        public event Action PauseRequested;
        public bool ToggleRegistered { get; private set; }
        public bool PauseRegistered { get; private set; }
        public PauseKeyBinding Binding { get; private set; }

        public bool TryRebind(PauseKeyBinding next, out string error)
        {
            error = string.Empty;
            if (disposed || !hookAvailable || next == null) { error = "Atalhos indisponíveis."; return false; }
            PauseKeyBinding validated;
            if (!PauseKeyBinding.TryCreate(next.Key, next.Modifiers, out validated, out error)) return false;
            if (Binding.Matches(next.Key, next.Modifiers)) { error = "Essa tecla já está selecionada."; return false; }
            int newId = activePauseId == FirstPauseId ? SecondPauseId : FirstPauseId;
            if (registrations.Contains(newId)) { error = "Não foi possível liberar o atalho anterior."; return false; }
            uint modifiers = NoRepeat | ToNativeModifiers(next.Modifiers);
            if (!Register(newId, modifiers, (uint)KeyInterop.VirtualKeyFromKey(next.Key)))
            { error = "Atalho ocupado; " + Binding.Display + " foi mantido."; return false; }
            int previousId = activePauseId;
            activePauseId = newId;
            Binding = next;
            PauseRegistered = true;
            // New registration is already live. A failed old unregister is tracked for disposal, never dispatched.
            if (registrations.Contains(previousId) && registrar.Unregister(windowHandle, previousId)) registrations.Remove(previousId);
            return true;
        }

        private bool Register(int id, uint modifiers, uint key)
        {
            if (registrar.Register(windowHandle, id, modifiers, key))
            {
                registrations.Add(id);
                return true;
            }
            if ((modifiers & NoRepeat) != 0 && registrar.Register(windowHandle, id, modifiers & ~NoRepeat, key))
            {
                registrations.Add(id);
                return true;
            }
            return false;
        }

        private static uint ToNativeModifiers(ModifierKeys modifiers)
        {
            uint result = 0;
            if ((modifiers & ModifierKeys.Alt) != 0) result |= 1;
            if ((modifiers & ModifierKeys.Control) != 0) result |= 2;
            if ((modifiers & ModifierKeys.Shift) != 0) result |= 4;
            return result;
        }

        private IntPtr HandleMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != HotKeyMessage || disposed) return IntPtr.Zero;
            int id = wParam.ToInt32();
            uint payload = unchecked((uint)lParam.ToInt64());
            uint modifiers = payload & 0xFFFF;
            uint key = payload >> 16;
            if (id == ToggleId && ToggleRegistered && key == 0x77 && modifiers == 0)
            {
                handled = true;
                var callback = ToggleRequested;
                if (callback != null) callback();
            }
            else if (id == activePauseId && PauseRegistered && key == (uint)KeyInterop.VirtualKeyFromKey(Binding.Key)
                && modifiers == ToNativeModifiers(Binding.Modifiers))
            {
                handled = true;
                var callback = PauseRequested;
                if (callback != null) callback();
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (llHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(llHook);
                llHook = IntPtr.Zero;
            }
            foreach (int id in registrations) registrar.Unregister(windowHandle, id);
            registrations.Clear();
            if (source != null) source.RemoveHook(HandleMessage);
            ToggleRegistered = false;
            PauseRegistered = false;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelKeyboardProc llProc;
        private IntPtr llHook = IntPtr.Zero;

        private void InstallLowLevelHook()
        {
            if (llHook != IntPtr.Zero) return;
            try
            {
                llProc = LowLevelHookCallback;
                using (var curProcess = Process.GetCurrentProcess())
                using (var curModule = curProcess.MainModule)
                {
                    llHook = SetWindowsHookEx(13, llProc, GetModuleHandle(curModule.ModuleName), 0);
                }
            }
            catch { }
        }

        private IntPtr LowLevelHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)0x0100 || wParam == (IntPtr)0x0104))
            {
                int vk = Marshal.ReadInt32(lParam);
                int targetVk = KeyInterop.VirtualKeyFromKey(Binding.Key);
                if (vk == targetVk)
                {
                    bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
                    bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                    bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                    ModifierKeys currentMods = (alt ? ModifierKeys.Alt : 0) | (ctrl ? ModifierKeys.Control : 0) | (shift ? ModifierKeys.Shift : 0);
                    if (currentMods == Binding.Modifiers)
                    {
                        var callback = PauseRequested;
                        if (callback != null)
                        {
                            if (Application.Current != null && Application.Current.Dispatcher != null)
                                Application.Current.Dispatcher.BeginInvoke(callback);
                            else
                                callback();
                        }
                    }
                }
            }
            return CallNextHookEx(llHook, nCode, wParam, lParam);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private sealed class NativeRegistrar : IHotKeyRegistrar
        {
            public bool Register(IntPtr handle, int id, uint modifiers, uint key) { return RegisterHotKey(handle, id, modifiers, key); }
            public bool Unregister(IntPtr handle, int id) { return UnregisterHotKey(handle, id); }
        }
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr handle, int id);
    }
}
