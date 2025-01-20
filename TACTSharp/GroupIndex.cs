using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TACTSharp.Instance;

namespace TACTSharp
{
    // roughly based on schlumpf's implementation
    public static class GroupIndex
    {
        private struct IndexEntry
        {
            public byte[] EKey;
            public uint Size;
            public ushort ArchiveIndex;
            public uint Offset;
        }

        public static Resource Generate(string? hash, string[] archives, Settings settings, ResourceManager resourceManager)
        {
            var entries = new List<IndexEntry>();
            
            var accumulationLock = new Lock();

            var options = new ParallelOptions { MaxDegreeOfParallelism = -1 };
            Parallel.ForEach(
                Enumerable.Range(0, archives.Length)
                    .Select(archiveIndex => (Archive: archives[archiveIndex], Index: (short) archiveIndex)),
                options,
                () => new List<IndexEntry>(),
                (itr, parallelLoopState, indexEntries) => {
                    var resource = resourceManager.Resolve(ResourceType.Data, itr.Archive + ".index");
                    if (!resource.Exists)
                        return indexEntries;

                    var index = new IndexInstance(resource.Path, itr.Index);
                    foreach (var (eKey, offset, size) in index.Enumerate()) {
                        indexEntries.Add(new IndexEntry {
                            EKey = eKey.ToArray(),
                            Size = (uint) size,
                            Offset = (uint) offset,
                            ArchiveIndex = (ushort) itr.Index
                        });
                    }

                    return indexEntries;
                },
                indexEntries => {
                    lock (accumulationLock)
                        entries.AddRange(indexEntries);
                });

            entries.Sort((a, b) => a.EKey.AsSpan().SequenceCompareTo(b.EKey));
            var entriesSpan = CollectionsMarshal.AsSpan(entries);

            var outputFooter = new IndexFooter
            {
                formatRevision = 1,
                flags0 = 0,
                flags1 = 0,
                blockSizeKBytes = 4,
                offsetBytes = 6,
                sizeBytes = 4,
                keyBytes = 16,
                hashBytes = 8,
                numElements = (uint) entriesSpan.Length
            };

            var outputBlockSizeBytes = outputFooter.blockSizeKBytes << 10;
            var outputEntrySize = outputFooter.keyBytes + outputFooter.sizeBytes + outputFooter.offsetBytes;
            var outputEntriesPerBlock = outputBlockSizeBytes / outputEntrySize;
            var outputNumBlocks = (int)Math.Ceiling((double)outputFooter.numElements / outputEntriesPerBlock);
            var outputEntriesOfLastBlock = outputFooter.numElements - (outputNumBlocks - 1) * outputEntriesPerBlock;

            var totalSize = (outputNumBlocks * outputBlockSizeBytes) + ((outputFooter.keyBytes + outputFooter.hashBytes) * outputNumBlocks) + 28;

            using (var ms = new MemoryStream(totalSize))
            using (var br = new BinaryReader(ms))
            using (var bin = new BinaryWriter(ms))
            {
                var ofsStartOfTocEkeys = outputNumBlocks * outputBlockSizeBytes;
                var ofsStartOfTocBlockHashes = ofsStartOfTocEkeys + outputFooter.keyBytes * outputNumBlocks;

                for (var i = 0; i < outputNumBlocks; i++)
                {
                    var startOfBlock = i * outputBlockSizeBytes;
                    bin.BaseStream.Position = startOfBlock;

                    var blockEntries = i + 1 == outputNumBlocks
                        ? entriesSpan[(i * outputEntriesPerBlock) ..]
                        : entriesSpan.Slice(i * outputEntriesPerBlock, outputEntriesPerBlock);

                    foreach (ref var entry in blockEntries)
                    {
                        bin.Write(entry.EKey);
                        if (BitConverter.IsLittleEndian)
                        {
                            bin.Write(BinaryPrimitives.ReverseEndianness(entry.Size));
                            bin.Write(BinaryPrimitives.ReverseEndianness(entry.ArchiveIndex));
                            bin.Write(BinaryPrimitives.ReverseEndianness(entry.Offset));
                        }
                        else
                        {
                            bin.Write(entry.Size);
                            bin.Write(entry.ArchiveIndex);
                            bin.Write(entry.Offset);
                        }
                    }
                    
                    bin.BaseStream.Position = ofsStartOfTocEkeys + i * outputFooter.keyBytes;
                    bin.Write(blockEntries[^1].EKey);
                    bin.BaseStream.Position = ofsStartOfTocBlockHashes + i * outputFooter.hashBytes;
                    bin.Write(new byte[outputFooter.hashBytes]);
                }

                bin.BaseStream.Position = totalSize - 28;
                bin.Write(new byte[outputFooter.hashBytes]); // toc_hash
                bin.Write(outputFooter.formatRevision);
                bin.Write(outputFooter.flags0);
                bin.Write(outputFooter.flags1);
                bin.Write(outputFooter.blockSizeKBytes);
                bin.Write(outputFooter.offsetBytes);
                bin.Write(outputFooter.sizeBytes);
                bin.Write(outputFooter.keyBytes);
                bin.Write(outputFooter.hashBytes);
                if (BitConverter.IsLittleEndian)
                    bin.Write(outputFooter.numElements);
                else
                    bin.Write(BinaryPrimitives.ReverseEndianness(outputFooter.numElements));
                bin.Write(new byte[outputFooter.hashBytes]); // footerHash

                for (var i = 0; i < outputNumBlocks; i++)
                {
                    var startOfBlock = i * outputBlockSizeBytes;
                    bin.BaseStream.Position = startOfBlock;
                    var blockBytes = br.ReadBytes(outputBlockSizeBytes);
                    var md5Hash = MD5.HashData(blockBytes);

                    bin.BaseStream.Position = ofsStartOfTocBlockHashes + (i * 8);
                    bin.Write(md5Hash.AsSpan(0, 8).ToArray());
                }

                // Generate TOC hash
                bin.BaseStream.Position = ofsStartOfTocEkeys;
                var tocBytes = br.ReadBytes((int)bin.BaseStream.Length - ofsStartOfTocEkeys - 28);
                var tocMD5Hash = MD5.HashData(tocBytes);
                bin.BaseStream.Position = totalSize - 28;
                bin.Write(tocMD5Hash.AsSpan(0, 8).ToArray());

                // Generate footer hash
                bin.BaseStream.Position = totalSize - 20;
                var footerBytes = br.ReadBytes(20);
                var footerMD5Hash = MD5.HashData(footerBytes);
                bin.BaseStream.Position = totalSize - 8;
                bin.Write(footerMD5Hash.AsSpan(0, 8).ToArray());

                // Generate full footer hash (filename)
                bin.BaseStream.Position = totalSize - 28;
                var fullFooterBytes = br.ReadBytes(28);
                var fullFooterMD5Hash = Convert.ToHexStringLower(MD5.HashData(fullFooterBytes));

                var filePath = Path.Combine(settings.CacheDirectory, settings.Product, hash + ".index");
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

                if (!string.IsNullOrEmpty(hash))
                {
                    if (fullFooterMD5Hash != hash)
                        throw new Exception($"Footer MD5 of group index does not match group index filename; found {fullFooterMD5Hash}");
                }
                else
                {
                    hash = fullFooterMD5Hash;
                }

                File.WriteAllBytes(filePath, ms.GetBuffer().AsSpan()[.. (int) ms.Position]);
                return new Resource(new FileInfo(filePath));
            }
        }

        private unsafe struct IndexFooter
        {
            public byte formatRevision;
            public byte flags0;
            public byte flags1;
            public byte blockSizeKBytes;
            public byte offsetBytes;
            public byte sizeBytes;
            public byte keyBytes;
            public byte hashBytes;
            public uint numElements;
            public fixed byte bytefooterHash[8];
        }
    }
}
