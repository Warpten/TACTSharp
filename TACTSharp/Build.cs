using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using TACTSharp.Instance;

namespace TACTSharp
{
    /// <summary>
    /// Represents a non-opened configuration state.
    /// </summary>
    /// <param name="buildConfig"></param>
    /// <param name="cdnConfig"></param>
    public class Configuration(Config buildConfig, Config cdnConfig)
    {
        public readonly Config Build = buildConfig;
        public readonly Config CDN = cdnConfig;

        public BuildInstance Open(Settings settings, ResourceManager resourceManager) => new(settings, this, resourceManager);
    }

    public class BuildInstance
    {
        public readonly Configuration Configuration;
        public readonly Settings Settings;
        public readonly ResourceManager ResourceManager;

        public readonly EncodingInstance Encoding;
        public readonly WarptenRoot Root;
        public readonly InstallInstance Install;
        public readonly IndexInstance? GroupIndex;
        public readonly IndexInstance FileIndex;

        public Config BuildConfig => Configuration.Build;
        public Config CDNConfig => Configuration.CDN;

        public BuildInstance(Settings settings, Configuration configuration, ResourceManager resourceManager)
        {
            Settings = settings;
            Configuration = configuration;
            ResourceManager = resourceManager;

            // 1. Load (or generate from individual indices) archive-group.
            if (!CDNConfig.TryGetValue("archive-group", out var groupArchiveIndex))
            {
                var resource = TACTSharp.GroupIndex.Generate(null, CDNConfig["archives"], settings, resourceManager);
                Debug.Assert(resource.Exists);

                GroupIndex = new IndexInstance(resource.Path);
            }
            else
            {
                var localResource = resourceManager.Resolve(ResourceType.Indice, groupArchiveIndex[0] + ".index");

                if (localResource.Exists)
                    GroupIndex = new IndexInstance(localResource.Path);
                else
                {
                    var resource = TACTSharp.GroupIndex.Generate(groupArchiveIndex[0], CDNConfig["archives"], settings, resourceManager);
                    Debug.Assert(resource.Exists);

                    GroupIndex = new IndexInstance(resource.Path);
                }
            }

            { // 2. Load file-index.
                if (!CDNConfig.TryGetValue("file-index", out var fileIndex))
                    throw new Exception("No file index found in CDN config");

                var resource = resourceManager.Resolve(ResourceType.Indice, fileIndex[0] + ".index");
                if (resource.Exists)
                    FileIndex = new IndexInstance(resource.Path);
            }

            { // 3. Load Encoding
                var encodingSizes = BuildConfig["encoding-size"];
                var encodingName = BuildConfig["encoding"][1];
                var resource = resourceManager.Resolve(ResourceType.Data, encodingName)
                    .Decompress(ulong.Parse(encodingSizes[0]));

                Debug.Assert(resource.Exists);
                Encoding = new EncodingInstance(resource.Path);
            }

            { // 4. Try to load root
                if (BuildConfig.TryGetValue("root", out var rootKey)
                    && Encoding.TryFindEntry(Convert.FromHexString(rootKey[0]), out var rootEncodingKeys)
                    && rootEncodingKeys != null)
                {
                    var resource = resourceManager.Resolve(ResourceType.Data, rootEncodingKeys.Value[0])
                        .Decompress(rootEncodingKeys.Value.DecodedFileSize);

                    Debug.Assert(resource.Exists);
                    Root = resource.OpenMemoryMapped(rawData => new WarptenRoot(rawData, settings))!;
                }
            }

            { // 5. Try to load install
                if (BuildConfig.TryGetValue("install", out var installKey)
                    && Encoding.TryFindEntry(Convert.FromHexString(installKey[0]), out var installEncodingKeys)
                    && installEncodingKeys != null)
                {
                    var resource = resourceManager.Resolve(ResourceType.Data, installEncodingKeys.Value[0])
                        .Decompress(installEncodingKeys.Value.DecodedFileSize);

                    Debug.Assert(resource.Exists);
                    Install = new InstallInstance(resource.Path);
                }
            }
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

        public byte[] OpenFileByCKey(string contentKey)
            => OpenFileByCKey(Convert.FromHexString(contentKey));

        public byte[] OpenFileByCKey(ReadOnlySpan<byte> contentKey)
        {
            if (Encoding == null)
                throw new Exception("Encoding not loaded");

            var encodingResult = Encoding.FindEntry(contentKey) ?? throw new Exception("File not found in encoding");

            return OpenFileByEKey(encodingResult[0], encodingResult.DecodedFileSize);
        }

        public byte[] OpenFileByEKey(string eKey, ulong decodedSize = 0) => OpenFileByEKey(Convert.FromHexString(eKey), decodedSize);

        public byte[] OpenFileByEKey(ReadOnlySpan<byte> eKey, ulong decodedSize = 0)
        {
            if (GroupIndex == null || FileIndex == null)
                throw new Exception("Indexes not loaded");

            var (offset, size, archiveIndex) = GroupIndex.GetIndexInfo(eKey);

            if (offset == -1)
            {
                var fileIndexEntry = FileIndex.GetIndexInfo(eKey);
                if (fileIndexEntry.size == -1)
                {
                    Console.WriteLine("Warning: EKey " + Convert.ToHexStringLower(eKey) + " not found in group or file index and might not be available on CDN.");

                    return ResourceManager.Resolve(ResourceType.Data, Convert.ToHexStringLower(eKey))
                        .Decompress(decodedSize)
                        .OpenMemoryMapped(data => data.ToArray())!;
                }
                else
                {
                    return ResourceManager.Resolve(ResourceType.Data, Convert.ToHexStringLower(eKey))
                        .Decompress(decodedSize)
                        .OpenMemoryMapped(data => data.ToArray())!;
                }
            }
            else
            {
                var archiveName = Convert.FromHexString(CDNConfig["archives"][archiveIndex]);
                return ResourceManager.Resolve(ResourceType.Data, archiveName, offset, size)
                    .Decompress(decodedSize)
                    .OpenMemoryMapped(data => data.ToArray())!;
            }
        }
    }
}
