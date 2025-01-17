using System.Runtime.CompilerServices;

namespace TACTSharp
{
    public readonly struct Configuration(Identifier buildConfig, Identifier cdnConfig)
    {
        public readonly Config Build = buildConfig.Open();
        public readonly Config CDN = cdnConfig.Open();

        public readonly BuildInstance Open(Settings settings) => new BuildInstance(settings, this);
    }

    public class BuildInstance
    {
        public readonly Configuration Configuration;
        public readonly Settings Settings;

        public readonly EncodingInstance Encoding;
        public readonly WarptenRoot Root;
        public readonly InstallInstance Install;
        public readonly IndexInstance? GroupIndex;
        public readonly IndexInstance FileIndex;

        public Config BuildConfig => Configuration.Build;
        public Config CDNConfig => Configuration.CDN;

        public BuildInstance(Settings settings, Configuration configuration)
        {
            Settings = settings;
            Configuration = configuration;

            var hasBaseDir = !string.IsNullOrEmpty(Settings.BaseDir);

            
            { // 1. Load (or generate from individual indices) archive-group.
                if (!CDNConfig.Values.TryGetValue("archive-group", out var groupArchiveIndex))
                {
                    var groupIndexHash = TACTSharp.GroupIndex.Generate(null, CDNConfig.Values["archives"]);
                    var groupIndexPath = Path.Combine("cache/wow/data", groupIndexHash + ".index");
                    GroupIndex = new IndexInstance(groupIndexPath);
                }
                else if (hasBaseDir)
                {
                    var filePath = Path.Combine(Settings.BaseDir!, "Data/indices", groupArchiveIndex[0] + ".index");
                    if (File.Exists(filePath))
                    {
                        GroupIndex = new IndexInstance(filePath);
                    }
                    else
                    {
                        if (!File.Exists(filePath))
                            TACTSharp.GroupIndex.Generate(groupArchiveIndex[0], CDNConfig.Values["archives"]);

                        GroupIndex = new IndexInstance(filePath);
                    }
                }
            }

            { // 2. Load file-index.
                if (!CDNConfig.Values.TryGetValue("file-index", out var fileIndex))
                    throw new Exception("No file index found in CDN config");

                if (hasBaseDir) {
                    var filePath = Path.Combine(Settings.BaseDir!, "Data/indices", fileIndex[0] + ".index");
                    if (File.Exists(filePath))
                    {
                        FileIndex = new IndexInstance(filePath);
                    }
                    else
                    {
                        var fileIndexPath = await CDN.GetFilePath("wow", "data", fileIndex[0] + ".index");
                        FileIndex = new IndexInstance(fileIndexPath);
                    }
                }
            }
        }

        public async Task Load()
        {
            var timer = new System.Diagnostics.Stopwatch();

            timer.Start();
            if (!CDNConfig.Values.TryGetValue("archive-group", out var groupArchiveIndex))
            {
                Console.WriteLine("No group index found in CDN config, generating fresh group index...");
                var groupIndexHash = TACTSharp.GroupIndex.Generate("", CDNConfig.Values["archives"]);
                var groupIndexPath = Path.Combine("cache", "wow", "data", groupIndexHash + ".index");
                GroupIndex = new IndexInstance(groupIndexPath);
            }
            else
            {
                if (!string.IsNullOrEmpty(Settings.BaseDir) && File.Exists(Path.Combine(Settings.BaseDir, "Data", "indices", groupArchiveIndex[0] + ".index")))
                {
                    GroupIndex = new IndexInstance(Path.Combine(Settings.BaseDir, "Data", "indices", groupArchiveIndex[0] + ".index"));
                }
                else
                {
                    var groupIndexPath = Path.Combine("cache", "wow", "data", groupArchiveIndex[0] + ".index");
                    if (!File.Exists(groupIndexPath))
                        TACTSharp.GroupIndex.Generate(groupArchiveIndex[0], CDNConfig.Values["archives"]);
                    GroupIndex = new IndexInstance(groupIndexPath);
                }
            }
            timer.Stop();
            Console.WriteLine("Group index loaded in " + Math.Ceiling(timer.Elapsed.TotalMilliseconds) + "ms");

            timer.Restart();
            if (!CDNConfig.Values.TryGetValue("file-index", out var fileIndex))
                throw new Exception("No file index found in CDN config");

            if (!string.IsNullOrEmpty(Settings.BaseDir) && File.Exists(Path.Combine(Settings.BaseDir, "Data", "indices", fileIndex[0] + ".index")))
            {
                FileIndex = new IndexInstance(Path.Combine(Settings.BaseDir, "Data", "indices", fileIndex[0] + ".index"));
            }
            else
            {
                var fileIndexPath = await CDN.GetFilePath("wow", "data", fileIndex[0] + ".index");
                FileIndex = new IndexInstance(fileIndexPath);
            }

            timer.Stop();
            Console.WriteLine("File index loaded in " + Math.Ceiling(timer.Elapsed.TotalMilliseconds) + "ms");

            timer.Restart();
            Encoding = new EncodingInstance(await CDN.GetDecodedFilePath("wow", "data", BuildConfig.Values["encoding"][1], ulong.Parse(BuildConfig.Values["encoding-size"][1]), ulong.Parse(BuildConfig.Values["encoding-size"][0])));
            timer.Stop();
            Console.WriteLine("Encoding loaded in " + Math.Ceiling(timer.Elapsed.TotalMilliseconds) + "ms");

            timer.Restart();
            if (!BuildConfig.Values.TryGetValue("root", out var rootKey))
                throw new Exception("No root key found in build config");

            if (!Encoding.TryFindEntry(Convert.FromHexString(rootKey[0]), out var rootEKeys) || rootEKeys == null)
                throw new Exception("Root key not found in encoding");

            Root = new (await CDN.GetDecodedFilePath("wow", "data", Convert.ToHexStringLower(rootEKeys.Value[0]), 0, rootEKeys.Value.DecodedFileSize));
            timer.Stop();
            Console.WriteLine("Root loaded in " + Math.Ceiling(timer.Elapsed.TotalMilliseconds) + "ms");

            timer.Restart();
            if (!BuildConfig.Values.TryGetValue("install", out var installKey))
                throw new Exception("No root key found in build config");

            if (!Encoding.TryFindEntry(Convert.FromHexString(installKey[0]), out var installEKeys) || installEKeys == null)
                throw new Exception("Install key not found in encoding");

            Install = new InstallInstance(await CDN.GetDecodedFilePath("wow", "data", Convert.ToHexStringLower(installEKeys.Value[0]), 0, installEKeys.Value.DecodedFileSize));
            timer.Stop();
            Console.WriteLine("Install loaded in " + Math.Ceiling(timer.Elapsed.TotalMilliseconds) + "ms");
        }

        public byte[] OpenFileByFDID(uint fileDataID)
        {
            if (Root == null)
                throw new Exception("Root not loaded");

            ref readonly var fileData = ref Root.FindFileDataID(fileDataID);
            if (Unsafe.IsNullRef(in fileData))
                throw new Exception("File not found in root");

            return OpenFileByCKey(fileData.ContentKey);
        }

        public byte[] OpenFileByCKey(string cKey) => OpenFileByCKey(Convert.FromHexString(cKey));

        public byte[] OpenFileByCKey(ReadOnlySpan<byte> cKey)
        {
            if (Encoding == null)
                throw new Exception("Encoding not loaded");

            var encodingResult = Encoding.FindEntry(cKey) ?? throw new Exception("File not found in encoding");

            return OpenFileByEKey(encodingResult[0], encodingResult.DecodedFileSize);
        }

        public byte[] OpenFileByEKey(string eKey, ulong decodedSize = 0) => OpenFileByEKey(Convert.FromHexString(eKey), decodedSize);

        public byte[] OpenFileByEKey(ReadOnlySpan<byte> eKey, ulong decodedSize = 0)
        {
            if (GroupIndex == null || FileIndex == null)
                throw new Exception("Indexes not loaded");

            var (offset, size, archiveIndex) = GroupIndex.GetIndexInfo(eKey);
            byte[] fileBytes;

            if (offset == -1)
            {
                var fileIndexEntry = FileIndex.GetIndexInfo(eKey);
                if (fileIndexEntry.size == -1)
                {
                    Console.WriteLine("Warning: EKey " + Convert.ToHexStringLower(eKey) + " not found in group or file index and might not be available on CDN.");
                    fileBytes = CDN.GetFile("wow", "data", Convert.ToHexStringLower(eKey), 0, decodedSize, true).Result;
                }
                else
                {
                    fileBytes = CDN.GetFile("wow", "data", Convert.ToHexStringLower(eKey), (ulong)fileIndexEntry.size, decodedSize, true).Result;
                }
            }
            else
            {
                fileBytes = CDN.GetFileFromArchive(Convert.ToHexStringLower(eKey), "wow", CDNConfig.Values["archives"][archiveIndex], offset, size, decodedSize, true).Result;
            }

            return fileBytes;
        }
    }
}
