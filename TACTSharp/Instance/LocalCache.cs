using TACTSharp.Instance;

namespace TACTSharp
{
    public sealed class LocalCache
    {
        private readonly DirectoryInfo _baseDirectory;
        private readonly CASCIndexInstance[] _localIndices;

        public LocalCache(DirectoryInfo baseDirectory)
        {
            _baseDirectory = baseDirectory;

            var dataFiles = _baseDirectory.EnumerateFiles("Data/data/*.idx", SearchOption.AllDirectories).ToArray();
            var indices = new List<CASCIndexInstance>(dataFiles.Length);

            foreach (var indexFile in dataFiles)
            {
                if (indexFile.FullName.Contains("tempfile"))
                    continue;

                var indexBucket = Convert.FromHexString(indexFile.Name.AsSpan()[0..2])[0];
                indices[indexBucket] = new CASCIndexInstance(indexFile.FullName);
            }

            _localIndices = [..indices];
        }

        public Resource? OpenResource(ResourceType resourceType, string fileName)
        {
            var localPath = Path.Combine(_baseDirectory.FullName, "Data", resourceType.LocalPath, fileName);
            var fileInfo = new FileInfo(localPath);

            if (fileInfo.Exists)
                return new Resource(fileInfo);
            
            return null;
        }

        public Resource? OpenResource(ReadOnlySpan<byte> encodingKey)
        {
            // Identify bucket.
            var indexBucket = encodingKey[0];
            for (var i = 1; i < encodingKey.Length / 2 + 1; ++i)
                indexBucket ^= encodingKey[i];

            indexBucket = (byte)((indexBucket & 0xF) ^ (indexBucket >> 4));

            var targetIndex = _localIndices[indexBucket];
            var (archiveOffset, archiveSize, archiveIndex) = targetIndex.GetIndexInfo(encodingKey);

            if (archiveOffset == -1)
            {
                var archivePath = Path.Combine(_baseDirectory.FullName, "Data/data", $"data.{archiveIndex:03}");
                return new Resource(new FileInfo(archivePath), archiveOffset, archiveSize);
            }

            return null;
        }
    }
}