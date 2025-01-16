﻿using System.Buffers.Binary;
using System.Security.Cryptography;

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

        private static readonly List<IndexEntry> Entries = [];

        public static string Generate(string? hash, string[] archives)
        {
            if (string.IsNullOrEmpty(hash))
                Console.WriteLine("Generating group index for unknown group-index");
            else
                Console.WriteLine("Generating group index for " + hash);

            Console.WriteLine("Loading " + archives.Length + " index files");

            Lock accumulationLock = new();
            Parallel.ForEach(
                Enumerable.Range(0, archives.Length)
                    .Select(archiveIndex => (archive: archives[archiveIndex], idx: (short) archiveIndex)),
                () => new List<IndexEntry>(),
                (itr, state, subtotal) => {
                    string indexPath = "";
                    if (!string.IsNullOrEmpty(Settings.BaseDir) && File.Exists(Path.Combine(Settings.BaseDir, "Data", "indices", itr.archive + ".index")))
                    {
                        indexPath = Path.Combine(Settings.BaseDir, "Data", "indices", itr.archive + ".index");
                    }
                    else
                    {
                        _ = CDN.GetFile("wow", "data", itr.archive + ".index").Result;
                        indexPath = Path.Combine("cache", "wow", "data", itr.archive + ".index");
                    }

                    var index = new IndexInstance(indexPath, itr.idx);
                    foreach (var (eKey, offset, size) in index.Enumerate()) {
                        subtotal.Add(new IndexEntry {
                            EKey = eKey.ToArray(),
                            Size = (uint) size,
                            Offset = (uint) offset,
                            ArchiveIndex = (ushort) itr.idx
                        });
                    }

                    return subtotal;
                },
                subtotal => {
                    lock (accumulationLock)
                        Entries.AddRange(subtotal);
                });

            Console.WriteLine("Done loading index files, got " + Entries.Count + " entries");

            Console.WriteLine("Sorting entries by EKey");
            Entries.Sort((a, b) => a.EKey.AsSpan().SequenceCompareTo(b.EKey));
            Console.WriteLine("Done sorting entries");

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
                numElements = (uint)Entries.Count
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

                    var blockEntries = Entries.Skip(i * outputEntriesPerBlock).Take(outputEntriesPerBlock).ToArray();
                    for (var j = 0; j < blockEntries.Length; j++)
                    {
                        var entry = blockEntries[j];
                        bin.Write(entry.EKey);
                        bin.Write(BinaryPrimitives.ReverseEndianness(entry.Size));
                        bin.Write(BinaryPrimitives.ReverseEndianness((short)entry.ArchiveIndex));
                        bin.Write(BinaryPrimitives.ReverseEndianness(entry.Offset));
                    }
                    bin.BaseStream.Position = ofsStartOfTocEkeys + i * outputFooter.keyBytes;
                    bin.Write(blockEntries.Last().EKey);
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
                bin.Write(outputFooter.numElements);
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

                Directory.CreateDirectory(Path.Combine("cache", "wow", "data"));

                if (!string.IsNullOrEmpty(hash))
                {
                    if (fullFooterMD5Hash != hash)
                        throw new Exception($"Footer MD5 of group index does not match group index filename; found {fullFooterMD5Hash}");

                    File.WriteAllBytes(Path.Combine("cache", "wow", "data", hash + ".index"), ms.ToArray());
                }
                else
                {
                    hash = fullFooterMD5Hash;
                    File.WriteAllBytes(Path.Combine("cache", "wow", "data", fullFooterMD5Hash + ".index"), ms.ToArray());
                }

                return hash;
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
