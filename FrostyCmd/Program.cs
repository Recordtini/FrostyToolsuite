using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.Interfaces;
using FrostySdk.IO;
using FrostySdk.Managers;
using System.Reflection;
using System.Text;

namespace FrostyCmd
{
    public class ConsoleLogger : ILogger
    {
        public void Log(string text, params object[] vars)
        {
            Console.WriteLine("[" + DateTime.Now.ToLongTimeString() + "]: " + text, vars);
        }

        public void LogError(string text, params object[] vars)
        {
            throw new NotImplementedException();
        }

        public void LogWarning(string text, params object[] vars)
        {
            throw new NotImplementedException();
        }
    }

    class Program
    {
        public delegate void FunctionDelegate(FileSystem fs, AssetManager am, string[] args);
        public static readonly ConsoleLogger logger = new ConsoleLogger();

        internal static class Kernel32
        {
            [DllImport("kernel32.dll", EntryPoint = "LoadLibraryEx", SetLastError = true)]
            public static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hReservedNull, uint dwFlags);

            [DllImport("kernel32", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
            public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

            [DllImport("kernel32.dll", EntryPoint = "FreeLibrary", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool FreeLibrary(IntPtr hModule);
        }
        internal class LoadLibraryHandle
        {
            IntPtr handle;
            public LoadLibraryHandle(string lib)
            {
                handle = Kernel32.LoadLibraryEx(lib, IntPtr.Zero, 0);
            }
            public static implicit operator IntPtr(LoadLibraryHandle value) { return value.handle; }
            ~LoadLibraryHandle()
            {
                Kernel32.FreeLibrary(handle);
            }
        }
        internal static class Oodle
        {
            public delegate int DecompressFunc(IntPtr srcBuffer, long srcSize, IntPtr dstBuffer, long dstSize, int a5 = 0, int a6 = 0, long a7 = 0, long a8 = 0, long a9 = 0, long a10 = 0, long a11 = 0, long a12 = 0, long a13 = 0, int a14 = 3);
            public static DecompressFunc Decompress;

            public delegate long CompressFunc(int cmpCode, IntPtr srcBuffer, long srcSize, IntPtr cmpBuffer, long cmpSize, long dict = 0, long dictSize = 0);
            public static CompressFunc Compress;

            public delegate long MemorySizeNeededFunc(int a1, long a2);
            public static MemorySizeNeededFunc MemorySizeNeeded;

            internal static LoadLibraryHandle handle;
            internal static void Bind(string basePath)
            {
                string dllPath = basePath + "oo2core_6_win64.dll";

                handle = new LoadLibraryHandle(dllPath);
                if (handle == IntPtr.Zero)
                    return;

                Decompress = Marshal.GetDelegateForFunctionPointer<DecompressFunc>(Kernel32.GetProcAddress(handle, "OodleLZ_Decompress"));
                Compress = Marshal.GetDelegateForFunctionPointer<CompressFunc>(Kernel32.GetProcAddress(handle, "OodleLZ_Compress"));
                MemorySizeNeeded = Marshal.GetDelegateForFunctionPointer<MemorySizeNeededFunc>(Kernel32.GetProcAddress(handle, "OodleLZDecoder_MemorySizeNeeded"));
            }
        }
        internal static class LZ4
        {
            [DllImport("thirdparty/liblz4.so.1.8.0.dll", EntryPoint = "LZ4_decompress_fast")]
            public static extern int Decompress(IntPtr src, IntPtr dst, int outputSize);

            [DllImport("thirdparty/liblz4.so.1.8.0.dll", EntryPoint = "LZ4_compressBound")]
            public static extern int CompressBound(int inputSize);

            [DllImport("thirdparty/liblz4.so.1.8.0.dll", EntryPoint = "LZ4_compress_default")]
            public static extern int Compress(IntPtr src, IntPtr dst, int sourceSize, int maxDestSize);
        }

        internal class BitReader : IDisposable
        {
            public bool EndOfStream => atEnd;

            private Stream stream;
            private byte[] buffer = new byte[4];
            private int value;
            private int shift;
            private bool atEnd;

            public BitReader(Stream inStream)
            {
                stream = inStream;
            }

            ~BitReader()
            {
                Dispose(false);
            }

            public void Dispose()
            {
                Dispose(true);
            }

            public bool GetBit()
            {
                if (shift >= 32)
                {
                    if (stream.Position >= stream.Length)
                    {
                        atEnd = true;
                        return false;
                    }

                    shift = 0;
                    FillBuffer();
                }

                return ((value >> (shift++)) & 1) == 1;
            }

            public void SetPosition(int pos)
            {
                stream.Position = (pos >> 5) * 4;
                shift = pos & 0x1f;

                FillBuffer();
            }

            private void FillBuffer()
            {
                stream.Read(buffer, 0, 4);
                value = BitConverter.ToInt32(buffer, 0);
            }

            private void Dispose(bool disposing = false)
            {
                if (disposing)
                {
                    stream.Dispose();
                    stream = null;

                    GC.SuppressFinalize(this);
                }
            }
        }

        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

            if (args.Length == 0)
            {
                ProfileCreator profileCreator = new ProfileCreator();
                profileCreator.CreateProfiles();
                Console.WriteLine("Profiles.bin created.");
                return;
            }

            ProfilesLibrary.Initialize(new[]
            {
                new CollegeFootball27Profile().CreateProfile(),
                new CollegeFootball27TrialProfile().CreateProfile()
            });

            string command = args[0].ToLower();
            if (command == "export")
            {
                Export(args);
                return;
            }
            if (command == "inspect-ebx")
            {
                InspectEbx(args);
                return;
            }
            if (command == "inspect-asset")
            {
                InspectAsset(args);
                return;
            }
            Console.WriteLine("Unknown command.");
            Console.WriteLine("Usage: FrostyCmd inspect-ebx <file> [--profile CollegeFB27]");
            Console.WriteLine("       FrostyCmd inspect-asset <game exe or game dir> <asset name> [--profile CollegeFB27]");
            Console.WriteLine("       FrostyCmd export <game exe or game dir> <output dir> [--profile CollegeFB27] [--types ebx,res,chunk] [--filter text] [--limit count]");

            //string basePath = args[0];
            //string command = args[1].ToLower();

            //if (command == "profile")
            //{
            //    ProfileCreator profileCreator = new ProfileCreator();
            //    profileCreator.CreateProfiles();
            //    return;
            //}

            //FileInfo baseFile = new FileInfo(basePath);
            //ProfilesLibrary.Initialize(baseFile.Name.Replace(baseFile.Extension, ""));

            //byte[] keyData = null;
            //if (ProfilesLibrary.RequiresKey)
            //{
            //    using (NativeReader reader = new NativeReader(new FileStream(ProfilesLibrary.CacheName + ".key", FileMode.Open, FileAccess.Read)))
            //        keyData = reader.ReadToEnd();

            //    // add primary encryption key
            //    byte[] key = new byte[0x10];
            //    Array.Copy(keyData, key, 0x10);
            //    KeyManager.Instance.AddKey("Key1", key);

            //    if (keyData.Length > 0x10)
            //    {
            //        // add additional encryption keys
            //        key = new byte[0x10];
            //        Array.Copy(keyData, 0x10, key, 0, 0x10);
            //        KeyManager.Instance.AddKey("Key2", key);

            //        key = new byte[0x4000];
            //        Array.Copy(keyData, 0x20, key, 0, 0x4000);
            //        KeyManager.Instance.AddKey("Key3", key);
            //    }
            //}
            //{
            //    FileSystem fs = new FileSystem(baseFile.DirectoryName);
            //    foreach (FileSystemSource source in ProfilesLibrary.Sources)
            //        fs.AddSource(source.Path, source.SubDirs);
            //    fs.Initialize(KeyManager.Instance.GetKey("Key1"));

            //    ResourceManager rm = new ResourceManager(fs);
            //    rm.SetLogger(logger);
            //    rm.Initialize();

            //    AssetManager am = new AssetManager(fs, rm);
            //    am.SetLogger(logger);
            //    am.Initialize(false);
            //}
        }

        private static void InspectEbx(string[] args)
        {
            if (args.Length < 2 || !File.Exists(args[1]))
            {
                Console.WriteLine("Usage: FrostyCmd inspect-ebx <file> [--profile CollegeFB27]");
                return;
            }

            string profile = "CollegeFB27";
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i].Equals("--profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    profile = args[++i];
            }

            if (!ProfilesLibrary.Initialize(profile))
                throw new InvalidOperationException("Unable to initialize profile " + profile);

            TypeLibrary.Initialize();
            using (FileStream stream = new FileStream(args[1], FileMode.Open, FileAccess.Read))
            using (EbxReader reader = EbxReader.CreateReader(stream))
            {
                Console.WriteLine("Root type: " + reader.RootType);
                Console.WriteLine("File GUID: " + reader.FileGuid);
                Console.WriteLine("Dependencies: " + reader.Dependencies.Count);

                object root = reader.ReadObject();
                Console.WriteLine("Object type: " + root.GetType().FullName);
                foreach (PropertyInfo property in root.GetType().GetProperties())
                {
                    if (!property.CanRead || property.GetIndexParameters().Length != 0)
                        continue;

                    object value;
                    try
                    {
                        value = property.GetValue(root);
                    }
                    catch
                    {
                        continue;
                    }

                    if (value is System.Collections.ICollection collection)
                    {
                        Console.WriteLine(property.Name + ".Count: " + collection.Count);
                        int itemIndex = 0;
                        foreach (object item in collection)
                        {
                            if (itemIndex >= 8)
                                break;

                            Console.WriteLine("  [" + itemIndex + "] " + FormatObject(item));
                            itemIndex++;
                        }
                    }
                    else
                        Console.WriteLine(property.Name + ": " + (value ?? "(null)"));
                }
            }
        }

        private static string FormatObject(object value)
        {
            if (value == null)
                return "(null)";

            List<string> fields = new List<string>();
            foreach (PropertyInfo property in value.GetType().GetProperties())
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                    continue;

                object propertyValue;
                try
                {
                    propertyValue = property.GetValue(value);
                }
                catch
                {
                    continue;
                }
                fields.Add(property.Name + "=" + (propertyValue ?? "(null)"));
            }

            return fields.Count == 0 ? value.ToString() : string.Join(", ", fields);
        }

        private static void InspectAsset(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: FrostyCmd inspect-asset <game exe or game dir> <asset name> [--profile CollegeFB27]");
                return;
            }

            string profile = "CollegeFB27";
            for (int i = 3; i < args.Length; i++)
            {
                if (args[i].Equals("--profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    profile = args[++i];
            }

            FileInfo gameFile = ResolveGameFile(args[1]);
            if (gameFile == null)
                return;

            AssetManager assetManager = LoadAssetManager(gameFile, profile);
            string assetName = args[2].Replace('\\', '/');

            EbxAssetEntry ebxEntry = assetManager.GetEbxEntry(assetName);
            if (ebxEntry == null)
            {
                Console.WriteLine("EBX not found: " + assetName);
                return;
            }

            assetManager.ResolveEbxMetadata(ebxEntry);
            Console.WriteLine("EBX: " + ebxEntry.Name);
            Console.WriteLine("Type: " + ebxEntry.Type);
            Console.WriteLine("File GUID: " + ebxEntry.Guid);

            EbxAsset asset = assetManager.GetEbx(ebxEntry);
            if (asset?.RootObject != null)
            {
                foreach (PropertyInfo property in asset.RootObject.GetType().GetProperties())
                {
                    if (!property.CanRead)
                        continue;

                    object propertyValue = property.GetValue(asset.RootObject);
                    if (property.PropertyType == typeof(ResourceRef))
                    {
                        Console.WriteLine(property.Name + ": " + propertyValue);
                    }
                    else if (propertyValue is System.Collections.ICollection collection)
                    {
                        Console.WriteLine(property.Name + ".Count: " + collection.Count);
                        foreach (object item in collection)
                        {
                            PropertyInfo chunkIdProperty = item?.GetType().GetProperty("ChunkId");
                            if (chunkIdProperty == null)
                                continue;

                            Guid chunkId = (Guid)chunkIdProperty.GetValue(item);
                            Console.WriteLine("  Chunk " + chunkId + ": "
                                + (assetManager.GetChunkEntry(chunkId) != null ? "indexed" : "missing"));
                        }
                    }
                }
            }

            ResAssetEntry namedResource = assetManager.GetResEntry(assetName);
            if (namedResource == null)
            {
                Console.WriteLine("Same-name RES: not found");
                return;
            }

            Console.WriteLine("Same-name RES RID: " + namedResource.ResRid.ToString("X16"));
            Console.WriteLine("Same-name RES type: 0x" + namedResource.ResType.ToString("X8"));
            Console.WriteLine("Same-name RES size: " + namedResource.Size);

            if (namedResource.ResType == (uint)ResourceType.Texture)
            {
                FrostySdk.Resources.Texture texture = assetManager.GetResAs<FrostySdk.Resources.Texture>(namedResource);
                Console.WriteLine(texture == null
                    ? "Texture payload: unreadable"
                    : $"Texture payload: {texture.Width}x{texture.Height}, {texture.PixelFormat}");
                if (texture != null)
                {
                    Console.WriteLine("Texture type: " + texture.Type);
                    Console.WriteLine("Texture depth/slices: " + texture.Depth + "/" + texture.SliceCount);
                    Console.WriteLine("Texture mips: " + texture.MipCount + " (first " + texture.FirstMip + ")");
                    Console.WriteLine("Texture chunk: " + texture.ChunkId + " ("
                        + (assetManager.GetChunkEntry(texture.ChunkId) != null ? "indexed" : "missing") + ")");
                    Console.WriteLine("Texture data size: "
                        + (texture.Data?.Length.ToString() ?? "unavailable"));
                    using (Stream originalHeader = assetManager.GetRes(namedResource))
                    {
                        byte[] originalBytes = NativeReader.ReadInStream(originalHeader);
                        byte[] savedBytes = texture.SaveBytes();
                        Console.WriteLine("Texture header round-trip: "
                            + (originalBytes.SequenceEqual(savedBytes) ? "matching" : "different")
                            + " (" + originalBytes.Length + " bytes)");
                    }
                }
            }
            else if (namedResource.ResType == (uint)ResourceType.MeshSet)
            {
                string pluginPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Plugins",
                    "MeshSetPlugin.dll");
                if (File.Exists(pluginPath))
                {
                    Type meshSetType = Assembly.LoadFrom(pluginPath).GetType("MeshSetPlugin.Resources.MeshSet");
                    MethodInfo getResAs = typeof(AssetManager).GetMethods()
                        .First(method => method.Name == "GetResAs"
                            && method.IsGenericMethodDefinition
                            && method.GetParameters().Length == 2)
                        .MakeGenericMethod(meshSetType);
                    object meshSet = getResAs.Invoke(assetManager, new object[] { namedResource, null });
                    PropertyInfo lodsProperty = meshSetType.GetProperty("Lods");
                    System.Collections.ICollection lods =
                        lodsProperty?.GetValue(meshSet) as System.Collections.ICollection;
                    int lodCount = lods?.Count ?? 0;
                    Console.WriteLine("MeshSet payload LODs: " + lodCount);
                    if (lods != null)
                    {
                        foreach (object lod in lods)
                        {
                            Guid chunkId = (Guid)lod.GetType().GetProperty("ChunkId").GetValue(lod);
                            System.Collections.ICollection sections =
                                lod.GetType().GetProperty("Sections").GetValue(lod)
                                as System.Collections.ICollection;
                            Console.WriteLine("  LOD chunk " + chunkId + ": "
                                + (assetManager.GetChunkEntry(chunkId) != null ? "indexed" : "missing")
                                + ", sections=" + (sections?.Count ?? 0));
                        }
                    }
                }
            }
        }

        private static void Export(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: FrostyCmd export <game exe or game dir> <output dir> [--profile CollegeFB27] [--types ebx,res,chunk] [--filter text] [--limit count]");
                return;
            }

            string gameArg = args[1];
            string outDir = args[2];
            string profile = null;
            string filter = null;
            string types = "ebx,res,chunk";
            int limit = 0;

            for (int i = 3; i < args.Length; i++)
            {
                string arg = args[i].ToLower();
                if (arg == "--profile" && i + 1 < args.Length)
                    profile = args[++i];
                else if (arg == "--filter" && i + 1 < args.Length)
                    filter = args[++i];
                else if (arg == "--types" && i + 1 < args.Length)
                    types = args[++i].ToLower();
                else if (arg == "--limit" && i + 1 < args.Length)
                    int.TryParse(args[++i], out limit);
            }

            FileInfo gameFile = ResolveGameFile(gameArg);
            if (gameFile == null)
                return;

            if (string.IsNullOrEmpty(profile))
                profile = Path.GetFileNameWithoutExtension(gameFile.Name);

            Directory.CreateDirectory(outDir);
            AssetManager am = LoadAssetManager(gameFile, profile);

            int exported = 0;
            if (types.Contains("ebx"))
            {
                foreach (EbxAssetEntry entry in am.EnumerateEbx())
                {
                    if (!MatchesFilter(entry, filter))
                        continue;
                    if (ExportStream(am.GetEbxStream(entry), Path.Combine(outDir, "ebx", SafePath(entry.Name) + ".ebx")))
                        exported++;
                    if (limit > 0 && exported >= limit)
                        break;
                }
            }

            if (limit == 0 || exported < limit)
            {
                if (types.Contains("res"))
                {
                    foreach (ResAssetEntry entry in am.EnumerateRes())
                    {
                        if (!MatchesFilter(entry, filter))
                            continue;
                        string path = Path.Combine(outDir, "res", entry.Type, SafePath(entry.Name) + ".res");
                        if (ExportStream(am.GetRes(entry), path))
                            exported++;
                        if (limit > 0 && exported >= limit)
                            break;
                    }
                }
            }

            if (limit == 0 || exported < limit)
            {
                if (types.Contains("chunk"))
                {
                    foreach (ChunkAssetEntry entry in am.EnumerateChunks())
                    {
                        if (!MatchesFilter(entry, filter))
                            continue;
                        if (ExportStream(am.GetChunk(entry), Path.Combine(outDir, "chunks", entry.Id + ".chunk")))
                            exported++;
                        if (limit > 0 && exported >= limit)
                            break;
                    }
                }
            }

            Console.WriteLine("Exported {0} files to {1}", exported, outDir);
        }

        private static FileInfo ResolveGameFile(string gameArg)
        {
            FileInfo gameFile;
            if (Directory.Exists(gameArg))
            {
                string exe = Path.Combine(gameArg, "CollegeFB27.exe");
                if (!File.Exists(exe))
                    exe = Directory.EnumerateFiles(gameArg, "*.exe").FirstOrDefault();
                gameFile = new FileInfo(exe ?? gameArg);
            }
            else
            {
                gameFile = new FileInfo(gameArg);
            }

            if (!gameFile.Exists)
            {
                Console.WriteLine("Could not find game executable: " + gameArg);
                return null;
            }
            return gameFile;
        }

        private static AssetManager LoadAssetManager(FileInfo gameFile, string profile)
        {
            if (!ProfilesLibrary.Initialize(profile))
                throw new InvalidOperationException("Could not initialize profile: " + profile);

            TypeLibrary.Initialize();

            FileSystem fs = new FileSystem(gameFile.DirectoryName);
            foreach (FileSystemSource source in ProfilesLibrary.Sources)
                fs.AddSource(source.Path, source.SubDirs);
            fs.Initialize(KeyManager.Instance.GetKey("Key1"));

            ResourceManager rm = new ResourceManager(fs);
            rm.SetLogger(logger);
            rm.Initialize();

            AssetManager assetManager = new AssetManager(fs, rm);
            assetManager.SetLogger(logger);
            assetManager.Initialize(false);
            return assetManager;
        }

        private static bool MatchesFilter(AssetEntry entry, string filter)
        {
            if (string.IsNullOrEmpty(filter))
                return true;

            return (entry.Name != null && entry.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) != -1)
                || (entry.Type != null && entry.Type.IndexOf(filter, StringComparison.OrdinalIgnoreCase) != -1)
                || (entry.AssetType != null && entry.AssetType.IndexOf(filter, StringComparison.OrdinalIgnoreCase) != -1);
        }

        private static bool ExportStream(Stream stream, string path)
        {
            if (stream == null)
                return false;

            using (stream)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (FileStream outStream = new FileStream(path, FileMode.Create, FileAccess.Write))
                    stream.CopyTo(outStream);
            }
            return true;
        }

        private static string SafePath(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "_unnamed";

            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar))
            {
                if (c == Path.DirectorySeparatorChar)
                {
                    sb.Append(c);
                    continue;
                }

                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            }
            return sb.ToString();
        }

        private static System.Reflection.Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            string dllname = args.Name.Contains(",") ? args.Name.Substring(0, args.Name.IndexOf(',')) : args.Name;
            if (dllname.Equals("EbxClasses"))
            {
                FileInfo fi = new FileInfo(Assembly.GetExecutingAssembly().FullName);
                return Assembly.LoadFile(fi.DirectoryName + "/Profiles/" + ProfilesLibrary.SDKFilename + ".dll");
            }
            return null;
        }
    }
}
