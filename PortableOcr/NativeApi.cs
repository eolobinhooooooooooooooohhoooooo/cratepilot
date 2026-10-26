using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CratePilot.PortableOcr
{
    // Direct cdecl C API bindings, resolved only from our verified absolute DLL.
    // No managed Tesseract wrapper or process-wide DLL search-path changes.
    internal sealed class NativeApi
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr CreateDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void HandleDelegate(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int InitDelegate(IntPtr handle, [In] byte[] dataPath,
            [In] byte[] language, int engineMode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void SetIntegerDelegate(IntPtr handle, int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int SetVariableDelegate(IntPtr handle, [In] byte[] name,
            [In] byte[] value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void SetImageDelegate(IntPtr handle, IntPtr image,
            int width, int height, int bytesPerPixel, int bytesPerLine);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int RecognizeDelegate(IntPtr handle, IntPtr monitor);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr GetPointerDelegate(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr GetTextDelegate(IntPtr handle, int level);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int NextDelegate(IntPtr handle, int level);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int BoundsDelegate(IntPtr handle, int level,
            out int left, out int top, out int right, out int bottom);

        internal readonly CreateDelegate Create;
        internal readonly HandleDelegate Delete;
        internal readonly InitDelegate Init;
        internal readonly SetIntegerDelegate SetPageSegMode;
        internal readonly SetIntegerDelegate SetSourceResolution;
        internal readonly SetVariableDelegate SetVariable;
        internal readonly SetImageDelegate SetImage;
        internal readonly RecognizeDelegate Recognize;
        internal readonly GetPointerDelegate GetIterator;
        internal readonly GetPointerDelegate GetPageIterator;
        internal readonly GetTextDelegate GetWordText;
        internal readonly NextDelegate NextWord;
        internal readonly BoundsDelegate GetBounds;
        internal readonly HandleDelegate DeleteText;
        internal readonly HandleDelegate DeleteIterator;
        internal readonly HandleDelegate Clear;
        internal readonly HandleDelegate End;
        internal readonly CreateDelegate CreateMonitor;
        internal readonly HandleDelegate DeleteMonitor;
        internal readonly SetIntegerDelegate SetDeadline;
        internal readonly string Version;

        internal NativeApi(IntPtr module)
        {
            Create = Export<CreateDelegate>(module, "TessBaseAPICreate");
            Delete = Export<HandleDelegate>(module, "TessBaseAPIDelete");
            Init = Export<InitDelegate>(module, "TessBaseAPIInit2");
            SetPageSegMode = Export<SetIntegerDelegate>(module, "TessBaseAPISetPageSegMode");
            SetSourceResolution = Export<SetIntegerDelegate>(module, "TessBaseAPISetSourceResolution");
            SetVariable = Export<SetVariableDelegate>(module, "TessBaseAPISetVariable");
            SetImage = Export<SetImageDelegate>(module, "TessBaseAPISetImage");
            Recognize = Export<RecognizeDelegate>(module, "TessBaseAPIRecognize");
            GetIterator = Export<GetPointerDelegate>(module, "TessBaseAPIGetIterator");
            GetPageIterator = Export<GetPointerDelegate>(module, "TessResultIteratorGetPageIterator");
            GetWordText = Export<GetTextDelegate>(module, "TessResultIteratorGetUTF8Text");
            NextWord = Export<NextDelegate>(module, "TessResultIteratorNext");
            GetBounds = Export<BoundsDelegate>(module, "TessPageIteratorBoundingBox");
            DeleteText = Export<HandleDelegate>(module, "TessDeleteText");
            DeleteIterator = Export<HandleDelegate>(module, "TessResultIteratorDelete");
            Clear = Export<HandleDelegate>(module, "TessBaseAPIClear");
            End = Export<HandleDelegate>(module, "TessBaseAPIEnd");
            CreateMonitor = Export<CreateDelegate>(module, "TessMonitorCreate");
            DeleteMonitor = Export<HandleDelegate>(module, "TessMonitorDelete");
            SetDeadline = Export<SetIntegerDelegate>(module, "TessMonitorSetDeadlineMSecs");
            Version = ReadUtf8(Export<CreateDelegate>(module, "TessVersion")());
        }

        private static T Export<T>(IntPtr module, string name) where T : class
        {
            IntPtr address = NativeLibraries.GetProcAddress(module, name);
            if (address == IntPtr.Zero)
                throw new EntryPointNotFoundException("Portable OCR export missing: " + name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        internal static byte[] Utf8(string value)
        {
            return Encoding.UTF8.GetBytes(value + "\0");
        }

        internal static byte[] FileSystemPath(string path)
        {
            // This native 5.0 build opens models with the Windows narrow CRT.
            // File-system paths therefore use the active ANSI code page; OCR text
            // itself is always UTF-8. Short paths cover non-ACP profile names.
            Encoding fileEncoding = Encoding.GetEncoding(Encoding.Default.CodePage,
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            try { return fileEncoding.GetBytes(path + "\0"); }
            catch (EncoderFallbackException)
            {
                StringBuilder shortPath = new StringBuilder(32768);
                uint count = NativeLibraries.GetShortPathName(path, shortPath, shortPath.Capacity);
                if (count == 0 || count >= shortPath.Capacity)
                    throw new InvalidOperationException("The native OCR model path cannot be encoded by Windows and no short path is available.");
                try { return fileEncoding.GetBytes(shortPath.ToString() + "\0"); }
                catch (EncoderFallbackException)
                {
                    throw new InvalidOperationException("The native OCR model path requires a Windows-compatible short path.");
                }
            }
        }

        internal static string ReadUtf8(IntPtr text)
        {
            if (text == IntPtr.Zero) return string.Empty;
            const int maximumBytes = 1024 * 1024;
            int length = 0;
            while (length < maximumBytes && Marshal.ReadByte(text, length) != 0) length++;
            if (length == maximumBytes)
                throw new InvalidOperationException("Portable OCR returned an oversized UTF-8 string.");
            byte[] bytes = new byte[length];
            Marshal.Copy(text, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }
    }

    internal static class NativeLibraries
    {
        internal const uint LoadLibrarySearchDllLoadDir = 0x00000100;
        internal const uint LoadLibrarySearchDefaultDirs = 0x00001000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "LoadLibraryExW", ExactSpelling = true)]
        internal static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true,
            ExactSpelling = true)]
        internal static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
            EntryPoint = "GetModuleFileNameW", SetLastError = true)]
        internal static extern uint GetModuleFileName(IntPtr module, StringBuilder file, int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
            EntryPoint = "GetModuleHandleW")]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
            EntryPoint = "GetShortPathNameW", SetLastError = true)]
        internal static extern uint GetShortPathName(string path, StringBuilder shortPath, int size);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetThreadErrorMode(uint mode, out uint previous);

        internal static void RejectForeignModule(string absolutePath)
        {
            IntPtr module = GetModuleHandle(System.IO.Path.GetFileName(absolutePath));
            if (module == IntPtr.Zero) return;
            StringBuilder loaded = new StringBuilder(32768);
            uint count = GetModuleFileName(module, loaded, loaded.Capacity);
            if (count == 0 || count >= loaded.Capacity || !string.Equals(
                System.IO.Path.GetFullPath(absolutePath), System.IO.Path.GetFullPath(loaded.ToString()),
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A different native OCR library is already loaded: " + loaded);
        }

        internal static IntPtr LoadAbsolute(string file)
        {
            // Suppress loader dialogs on this thread only; never mutate process policy.
            uint previous;
            bool changed = SetThreadErrorMode(0x0001 | 0x8000, out previous);
            try
            {
                IntPtr module = LoadLibraryEx(file, IntPtr.Zero,
                    LoadLibrarySearchDllLoadDir | LoadLibrarySearchDefaultDirs);
                if (module == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Cannot load the app-local portable OCR library: " + file);
                StringBuilder loaded = new StringBuilder(32768);
                uint count = GetModuleFileName(module, loaded, loaded.Capacity);
                if (count == 0 || count >= loaded.Capacity ||
                    !string.Equals(System.IO.Path.GetFullPath(file),
                        System.IO.Path.GetFullPath(loaded.ToString()), StringComparison.OrdinalIgnoreCase))
                {
                    FreeLibrary(module);
                    throw new InvalidOperationException("Portable OCR library path verification failed: " + file);
                }
                return module;
            }
            finally
            {
                if (changed)
                {
                    uint ignored;
                    SetThreadErrorMode(previous, out ignored);
                }
            }
        }
    }
}
