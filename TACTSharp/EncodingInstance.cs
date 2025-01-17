using Microsoft.Win32.SafeHandles;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace TACTSharp
{
    public class EncodingInstance
    {
        private readonly MemoryMappedFile encodingFile;
        private readonly MemoryMappedViewAccessor accessor;
        private readonly SafeMemoryMappedViewHandle mmapViewHandle;

        private EncodingHeader header;
        private string[] ESpecs = [];
        private readonly Lock ESpecLock = new();

        public EncodingInstance(string path)
        {
            this.encodingFile = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            this.accessor = encodingFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            this.mmapViewHandle = accessor.SafeMemoryMappedViewHandle;

            this.header = ReadHeader();

            if (this.header.version != 1)
                throw new Exception("Unsupported encoding version");

            if (this.header.hashSizeCKey != 0x10)
                throw new Exception("Unsupported CKey hash size");

            if (this.header.hashSizeEKey != 0x10)
                throw new Exception("Unsupported EKey hash size");
        }

        unsafe private EncodingHeader ReadHeader()
        {
            byte* headerData = null;
            try {
                mmapViewHandle.AcquirePointer(ref headerData);

                var header = new ReadOnlySpan<byte>(headerData, 22);
                if (header[0] != 0x45 || header[1] != 0x4E)
                    throw new Exception("Invalid encoding file magic");

                return new EncodingHeader
                {
                    version = header[2],
                    hashSizeCKey = header[3],
                    hashSizeEKey = header[4],
                    CKeyPageSizeKB = (ushort)((header[5] << 8) | header[6]),
                    EKeySpecPageSizeKB = (ushort)((header[7] << 8) | header[8]),
                    CEKeyPageTablePageCount = (uint)((header[9] << 24) | (header[10] << 16) | (header[11] << 8) | header[12]),
                    EKeySpecPageTablePageCount = (uint)((header[13] << 24) | (header[14] << 16) | (header[15] << 8) | header[16]),
                    unk11 = header[17],
                    ESpecBlockSize = (uint)((header[18] << 24) | (header[19] << 16) | (header[20] << 8) | header[21])
                };
            } finally {
                if (headerData != null)
                    mmapViewHandle.ReleasePointer();
            }
        }

        unsafe static private ReadOnlySpan<byte> LowerBoundKey(ReadOnlySpan<byte> data, int stride, ReadOnlySpan<byte> key)
        {
            var count = data.Length / stride;
            var begin = 0;

            while (count > 0)
            {
                var it = begin;
                var step = count / 2;
                it += step * stride;

                if (data.Slice(it, key.Length).SequenceCompareTo(key) <= 0)
                {
                    it += stride;
                    begin = it;
                    count -= step + 1;
                }
                else
                {
                    count = step;
                }
            }

            return data.Slice(begin, key.Length);
        }

        public bool TryFindEntry(ReadOnlySpan<byte> cKeyTarget, out Entry? result)
            => (result = FindEntry(cKeyTarget)) != null;

        public unsafe Entry? FindEntry(ReadOnlySpan<byte> cKeyTarget)
        {
            byte* pageData = null;
            try {
                mmapViewHandle.AcquirePointer(ref pageData);
                if (pageData == null)
                    return null;

                var eKeyPageSize = header.EKeySpecPageSizeKB * 1024;
                
                byte* startOfPageKeys = pageData + 22 + header.ESpecBlockSize;

                // This span encompasses the page index table.
                ReadOnlySpan<byte> pageKeys = new(startOfPageKeys, (int) header.CEKeyPageTablePageCount * 32);
                
                // Find the last page that doesn't match.
                ReadOnlySpan<byte> lastPageKey = LowerBoundKey(pageKeys, header.hashSizeCKey + 0x10, cKeyTarget);
                if (lastPageKey.AsPointer() == startOfPageKeys)
                    return null;

                var pageIndex = (lastPageKey.AsPointer() - startOfPageKeys) / (header.hashSizeCKey + 0x10) - 1;

                var itr = new ReadOnlySpan<byte>(startOfPageKeys + pageKeys.Length + pageIndex * eKeyPageSize, eKeyPageSize);
                while (itr.Length != 0)
                {
                    var eKeyCount = itr[0];

                    var itrFileSize = itr.Slice(1, 5);
                    var itrContentKey = itr.Slice(6, header.hashSizeCKey);
                    var itrKeys = itr.Slice(6 + header.hashSizeCKey, header.hashSizeEKey * eKeyCount);

                    if (itrContentKey.SequenceEqual(cKeyTarget))
                        return new Entry(itrKeys.ToArray(), eKeyCount, (ulong) itrFileSize.ReadInt40BE());
                    
                    // Skip this entry and move on to the next one
                    itr = itr[(6 + header.hashSizeCKey + header.hashSizeEKey * eKeyCount) ..];
                }

                return null;
            } finally {
                if (pageData != null)
                    mmapViewHandle.ReleasePointer();
            }
        }

        public void ClearSpecs()
        {
            lock (ESpecLock)
                ESpecs = [];
        }

        public unsafe (string eSpec, ulong encodedFileSize)? FindSpec(ReadOnlySpan<byte> eKeyTarget)
        {
            byte* pageData = null;
            try {
                mmapViewHandle.AcquirePointer(ref pageData);
                if (pageData == null)
                    return null;

                lock (ESpecLock)
                {
                    if (ESpecs.Length == 0)
                    {
                        var timer = new System.Diagnostics.Stopwatch();
                        timer.Start();
                        var eSpecs = new List<string>();

                        var eSpecTable = new ReadOnlySpan<byte>(pageData + 22, (int)header.ESpecBlockSize);
                        var eSpecOffs = 0;
                        while (true)
                        {
                            if (eSpecOffs >= header.ESpecBlockSize)
                                break;

                            var eSpecString = eSpecTable[eSpecOffs..].ReadNullTermString();
                            eSpecOffs += eSpecString.Length + 1;
                            eSpecs.Add(eSpecString);
                        }

                        ESpecs = [.. eSpecs];

                        timer.Stop();
                        Console.WriteLine("Loaded " + ESpecs.Length + " ESpecs in " + timer.Elapsed.TotalMilliseconds + "ms");
                    }
                }

                var eKeyPageSize = header.EKeySpecPageSizeKB * 1024;

                byte* startOfESpecPageKeys = pageData + 22 + header.ESpecBlockSize + (header.CEKeyPageTablePageCount * 32) + (header.CEKeyPageTablePageCount * (header.CKeyPageSizeKB * 1024));
                byte* endOfESpecPageKeys = startOfESpecPageKeys + (header.EKeySpecPageTablePageCount * 32);

                var especPageKeys = new ReadOnlySpan<byte>(startOfESpecPageKeys, (int) header.EKeySpecPageTablePageCount * 32);

                byte* firstESpecPageKey = LowerBoundKey(especPageKeys, header.hashSizeEKey + 0x10, eKeyTarget).AsPointer();
                if (firstESpecPageKey == startOfESpecPageKeys)
                    return null;

                var pageIndex = ((firstESpecPageKey - startOfESpecPageKeys) / (header.hashSizeEKey + 0x10)) - 1;
                var startOfPageESpec = endOfESpecPageKeys + ((int)pageIndex * eKeyPageSize);
                var targetPage = new ReadOnlySpan<byte>(startOfPageESpec, eKeyPageSize);
                var offs = 0;
                while (true)
                {
                    if (offs >= eKeyPageSize)
                        break;
                    if (targetPage.Slice(offs, header.hashSizeEKey).SequenceEqual(eKeyTarget))
                    {
                        offs += header.hashSizeEKey; // +ekey

                        var eSpecIndex = targetPage.Slice(offs, 4).ReadInt32BE();
                        offs += 4;

                        var encodedFileSize = (ulong)targetPage.Slice(offs, 5).ReadInt40BE();

                        return (ESpecs[eSpecIndex], encodedFileSize);
                    }
                    else
                    {
                        offs += header.hashSizeEKey; // +ekey
                        offs += 4; // +encodedFileSize
                        offs += 5; // +encodedFileSize
                    }
                }

                return null;
            } finally {
                if (pageData != null)
                    mmapViewHandle.ReleasePointer();
            }
        }

        private unsafe struct EncodingHeader
        {
            public fixed byte magic[2];
            public byte version;
            public byte hashSizeEKey;
            public byte hashSizeCKey;
            public ushort CKeyPageSizeKB;
            public ushort EKeySpecPageSizeKB;
            public uint CEKeyPageTablePageCount;
            public uint EKeySpecPageTablePageCount;
            public byte unk11;
            public uint ESpecBlockSize;
        }

        public readonly struct Entry(byte[] keys, byte keyCount, ulong decodedFileSize)
        {
            private readonly byte[] _keys = keys;
            private readonly byte _keyCount = keyCount;

            private int KeySize => _keys.Length / _keyCount;

            public readonly ulong DecodedFileSize = decodedFileSize;

            public ReadOnlySpan<byte> this[int index] {
                get => _keys.AsSpan().Slice(index * KeySize, KeySize);
            }

            public readonly int Length = keyCount;
        }
    }
}
