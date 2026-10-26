using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CratePilot.PortableOcr
{
    internal sealed class RuntimeAsset
    {
        internal readonly string Resource;
        internal readonly string RelativePath;
        internal readonly long Length;
        internal readonly string Sha256;

        internal RuntimeAsset(string resource, string relativePath, long length, string sha256)
        {
            Resource = "CratePilot.PortableOcr." + resource;
            RelativePath = relativePath;
            Length = length;
            Sha256 = sha256;
        }
    }

    internal sealed class PortableRuntime
    {
        // Bump this directory version whenever any runtime asset/hash changes.
        internal const string BundleVersion = "tess5.2.0-fast4.1.0-vc14.44.35211-r1";
        private static readonly object Sync = new object();
        private static PortableRuntime current;
        private readonly List<FileStream> pinnedFiles = new List<FileStream>();
        private readonly List<IntPtr> libraries = new List<IntPtr>();
        internal readonly string DirectoryPath;
        internal readonly string Architecture;
        internal NativeApi Api;

        private PortableRuntime(string directory, string architecture)
        {
            DirectoryPath = directory;
            Architecture = architecture;
        }

        internal static PortableRuntime GetOrCreate()
        {
            lock (Sync)
            {
                if (current != null) return current;
                if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                    throw new PlatformNotSupportedException("Portable OCR requires Windows.");
                string architecture = IntPtr.Size == 8 ? "x64" : "x86";
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(local))
                    throw new IOException("The user-local application data directory is unavailable.");
                string app = Path.Combine(local, "CratePilot");
                string root = Path.Combine(app, "runtime");
                string directory = Path.Combine(root, BundleVersion + "-" + architecture);
                EnsureDirectory(app);
                EnsureDirectory(root);
                EnsureDirectory(directory);
                PortableRuntime runtime = new PortableRuntime(directory, architecture);
                bool entered = false;
                using (Mutex mutex = new Mutex(false, @"Local\CratePilot.PortableOcr." + HashName(directory)))
                {
                    try
                    {
                        try { entered = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                        catch (AbandonedMutexException) { entered = true; }
                        if (!entered) throw new IOException("Timed out waiting for portable OCR extraction.");
                        RuntimeAsset[] assets = Assets(architecture);
                        foreach (RuntimeAsset asset in assets) runtime.Extract(asset);
                        // Keep verified images/model read-locked for the process lifetime.
                        // This also closes the verification-to-load file replacement window.
                        foreach (RuntimeAsset asset in assets)
                        {
                            if (asset.Resource.IndexOf(".Licenses.", StringComparison.Ordinal) >= 0) continue;
                            FileStream stream = runtime.OpenVerified(asset);
                            runtime.pinnedFiles.Add(stream);
                        }
                        NativeLibraries.RejectForeignModule(Path.Combine(directory, "leptonica-1.82.0.dll"));
                        NativeLibraries.RejectForeignModule(Path.Combine(directory, "tesseract50.dll"));
                        string[] order = architecture == "x64"
                            ? new string[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll",
                                "leptonica-1.82.0.dll", "tesseract50.dll" }
                            : new string[] { "vcruntime140.dll", "msvcp140.dll",
                                "leptonica-1.82.0.dll", "tesseract50.dll" };
                        foreach (string file in order)
                            runtime.libraries.Add(NativeLibraries.LoadAbsolute(Path.Combine(directory, file)));
                        runtime.Api = new NativeApi(runtime.libraries[runtime.libraries.Count - 1]);
                        // Native delegates remain valid until process exit. Individual engines,
                        // iterators and strings are freed promptly by TesseractRecognizer.
                        current = runtime;
                        return current;
                    }
                    catch
                    {
                        for (int i = runtime.libraries.Count - 1; i >= 0; i--)
                            NativeLibraries.FreeLibrary(runtime.libraries[i]);
                        foreach (FileStream stream in runtime.pinnedFiles) stream.Dispose();
                        throw;
                    }
                    finally { if (entered) mutex.ReleaseMutex(); }
                }
            }
        }

        internal string DataDirectory
        {
            get { return Path.Combine(DirectoryPath, "tessdata"); }
        }

        private static void EnsureDirectory(string path)
        {
            if (File.Exists(path)) throw new IOException("A file blocks the portable OCR directory: " + path);
            System.IO.Directory.CreateDirectory(path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Portable OCR refuses a redirected/reparse-point directory: " + path);
        }

        private string AssetPath(RuntimeAsset asset)
        {
            string path = Path.GetFullPath(Path.Combine(DirectoryPath, asset.RelativePath));
            if (!path.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Portable OCR asset path escaped its private directory.");
            EnsureDirectory(Path.GetDirectoryName(path));
            if (System.IO.Directory.Exists(path))
                throw new IOException("A directory blocks a portable OCR asset: " + path);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Portable OCR refuses a linked/reparse-point asset: " + path);
            return path;
        }

        private void Extract(RuntimeAsset asset)
        {
            string destination = AssetPath(asset);
            if (Matches(destination, asset)) return;
            string temporary = destination + ".new-" + Guid.NewGuid().ToString("N");
            try
            {
                using (Stream source = typeof(PortableRuntime).Assembly.GetManifestResourceStream(asset.Resource))
                {
                    if (source == null)
                        throw new InvalidOperationException("Missing embedded portable OCR resource: " + asset.Resource);
                    using (FileStream output = new FileStream(temporary, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None))
                    {
                        source.CopyTo(output);
                        output.Flush(true);
                    }
                }
                if (!Matches(temporary, asset))
                    throw new InvalidDataException("Embedded portable OCR integrity check failed: " + asset.Resource);
                // Atomic operations, same directory/volume. Never delete an existing asset
                // before replacement, and never enumerate or purge unrelated cached files.
                AssetPath(asset);
                if (File.Exists(destination)) File.Replace(temporary, destination, null, true);
                else File.Move(temporary, destination);
                if (!Matches(destination, asset))
                    throw new InvalidDataException("Extracted portable OCR integrity check failed: " + asset.Resource);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private FileStream OpenVerified(RuntimeAsset asset)
        {
            FileStream stream = new FileStream(AssetPath(asset), FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (stream.Length != asset.Length || !string.Equals(Hash(stream), asset.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Portable OCR asset changed before loading: " + asset.Resource);
                stream.Position = 0;
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }

        private static bool Matches(string path, RuntimeAsset asset)
        {
            if (!File.Exists(path)) return false;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return stream.Length == asset.Length && string.Equals(Hash(stream), asset.Sha256,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string Hash(Stream stream)
        {
            using (SHA256 algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
        }

        private static string HashName(string value)
        {
            using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(value.ToUpperInvariant())))
                return Hash(stream).Substring(0, 24);
        }

        internal static RuntimeAsset[] Assets(string architecture)
        {
            List<RuntimeAsset> assets = new List<RuntimeAsset>();
            if (architecture == "x64")
            {
                assets.Add(new RuntimeAsset("Native.x64.vcruntime140.dll", "vcruntime140.dll", 124544,
                    "D5E4D9A3E835FA679450145D6A7D94E36573A509317111904D9B3712C30D9066"));
                assets.Add(new RuntimeAsset("Native.x64.vcruntime140_1.dll", "vcruntime140_1.dll", 49792,
                    "1F2D41C4AA5DB0BC33EBF7B66D72943A817D7CE6CBE880502A9403823633093F"));
                assets.Add(new RuntimeAsset("Native.x64.msvcp140.dll", "msvcp140.dll", 557728,
                    "0F885B509A685D2BBFA652FED26B5FB31D88FBDAB0A978C641D1C7B8AA460AA9"));
                assets.Add(new RuntimeAsset("Native.x64.leptonica-1.82.0.dll", "leptonica-1.82.0.dll", 4168192,
                    "DFCB3E6ED0B16BC55BFDBCF53543CFE42A354B87C3E35BD3A95EEBF005D73E76"));
                assets.Add(new RuntimeAsset("Native.x64.tesseract50.dll", "tesseract50.dll", 2788352,
                    "DE4D04EC75095374D98F5DD7A60D14D7E2E0F76589DB693ECCF7AE658BE8CB2B"));
            }
            else if (architecture == "x86")
            {
                assets.Add(new RuntimeAsset("Native.x86.vcruntime140.dll", "vcruntime140.dll", 91216,
                    "A9D7732A3C3DE09F0601A7FA2555093DCB49F08E2FC62A86091461F0F38393ED"));
                assets.Add(new RuntimeAsset("Native.x86.msvcp140.dll", "msvcp140.dll", 447616,
                    "97E71A3C697FB1AB3AF777DB39009E4602525E8C3AFF0837ECC7031CBA188165"));
                assets.Add(new RuntimeAsset("Native.x86.leptonica-1.82.0.dll", "leptonica-1.82.0.dll", 3379712,
                    "1700330110ADA8E4F07FB063915E60E2B585AD87D9B1948093945E4645B66D08"));
                assets.Add(new RuntimeAsset("Native.x86.tesseract50.dll", "tesseract50.dll", 2336768,
                    "7F4873CDB78B9CD18C069EAE434D38DD14E987531866463357CF51C016241820"));
            }
            else throw new PlatformNotSupportedException("Unsupported portable OCR architecture.");
            assets.Add(new RuntimeAsset("Data.eng.traineddata", Path.Combine("tessdata", "eng.traineddata"), 4113088,
                "7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2"));
            AddLicense(assets, "Tesseract-Apache-2.0.txt", 11358,
                "CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30");
            AddLicense(assets, "TessdataFast-Apache-2.0.txt", 11358,
                "CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30");
            AddLicense(assets, "Leptonica-BSD-2-Clause.txt", 1521,
                "87829ABB5BBB00B55A107365DA89E9A33F86C4250169E5A1E5588505BE7D5806");
            AddLicense(assets, "Microsoft-VisualStudio-2022-Community-License-EN.txt", 19043,
                "A38D10AFC3EF8FB2E64AF972A9C64D998EF964DE8101B9F7A59703F72FA3A5AB");
            AddLicense(assets, "Microsoft-VisualStudio-2022-Redist.txt", 187,
                "DA53B097E02B08E0FC69706102A60BC384FE756426AE4DC4A855E96F95CB2B9C");
            return assets.ToArray();
        }

        private static void AddLicense(List<RuntimeAsset> assets, string name, long length, string sha256)
        {
            assets.Add(new RuntimeAsset("Licenses." + name, Path.Combine("licenses", name), length, sha256));
        }
    }
}
