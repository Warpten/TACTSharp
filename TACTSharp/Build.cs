using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

using Microsoft.Extensions.Logging;

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

    public class BuildInstance : LoggingEnabledBase<BuildInstance>
    {
        public readonly Configuration Configuration;
        public readonly Settings Settings;
        public readonly ResourceManager ResourceManager;

        public readonly EncodingInstance Encoding;
        public readonly RootInstance? Root;
        public readonly InstallInstance Install;
        public readonly IndexInstance? GroupIndex;
        public readonly IndexInstance FileIndex;

        public Config BuildConfig => Configuration.Build;
        public Config CDNConfig => Configuration.CDN;

        public BuildInstance(Settings settings, Configuration configuration, ResourceManager resourceManager) : base(settings.LoggerFactory)
        {
            Settings = settings;
            Configuration = configuration;
            ResourceManager = resourceManager;

            using (BeginTimedScope(time => Logger.LogInformation("(group-index) Loaded in {Elapsed:c}.", time)))
            {
                GroupIndex = LoadGroupIndex();
            }

            { // 2. Load file-index.
                if (!CDNConfig.TryGetValue("file-index", out var fileIndex))
                    Logger.LogError("No file index found in CDN configuration.");
                else
                {
                    using (BeginTimedScope(time => Logger.LogInformation("(file-index) `{FileIndex}` loaded in {Elapsed:c}.", fileIndex[0] + ".index", time)))
                    {
                        var resource = resourceManager.Resolve(ResourceType.Indice, fileIndex[0] + ".index");
                        Debug.Assert(resource.Exists);

                        FileIndex = new IndexInstance(resource.Path);
                    }
                }
            }

            using (BeginTimedScope(time => Logger.LogInformation("(encoding) `{Encoding}` loaded in {Elapsed:c}.", BuildConfig["encoding"][1], time)))
            {
                var encodingSizes = BuildConfig["encoding-size"];
                var encodingName = BuildConfig["encoding"][1];
                var resource = resourceManager.Resolve(ResourceType.Data, encodingName)
                    .Decompress(ulong.Parse(encodingSizes[0]));

                Debug.Assert(resource.Exists);
                Encoding = new EncodingInstance(resource.Path);
            }

            if (BuildConfig.TryGetValue("root", out var rootKey))
            {
                using var _ = BeginTimedScope(time => Logger.LogInformation("(root) Loaded {Count} entries in {Elapsed:c}.", Root!.Count, time));

                var contentKey = Convert.FromHexString(rootKey[0]);
                if (Encoding.TryFindEntry(Convert.FromHexString(rootKey[0]), out var rootEncodingKeys)
                    && rootEncodingKeys != null)
                {
                    var resource = resourceManager.Resolve(ResourceType.Data, rootEncodingKeys.Value[0], validate: false)
                        .Decompress(rootEncodingKeys.Value.DecodedFileSize);

                    Debug.Assert(resource.Exists);

                    byte[] checksum = resource.OpenMemoryMapped(MD5.HashData, () => []);
                    if (contentKey.SequenceEqual(checksum))
                        Root = resource.OpenMemoryMapped(rawData => new RootInstance(rawData, settings))!;
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

        private IndexInstance LoadGroupIndex()
        {
            // 1. Load (or generate from individual indices) archive-group.
            if (!CDNConfig.TryGetValue("archive-group", out var groupArchiveIndex))
            {
                Logger.LogInformation("No `archive-group` found in CDN configuration; generating.");

                var resource = TACTSharp.GroupIndex.Generate(null, CDNConfig["archives"], Settings, ResourceManager);
                Debug.Assert(resource.Exists);

                return new IndexInstance(resource.Path);
            }
            else
            {
                Logger.LogInformation("`archive-group` found in CDN configuration, resolving.");

                var localResource = ResourceManager.Resolve(ResourceType.Indice, groupArchiveIndex[0] + ".index");
                if (!localResource.Exists)
                    localResource = TACTSharp.GroupIndex.Generate(groupArchiveIndex[0], CDNConfig["archives"], Settings, ResourceManager);

                Debug.Assert(localResource.Exists);
                return new IndexInstance(localResource.Path);
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
                var compressedResource = ResourceManager.Resolve(ResourceType.Data, archiveName, offset, size);

                return compressedResource.Decompress(decodedSize)
                    .OpenMemoryMapped(data => data.ToArray())!;
            }
        }
    }
}
