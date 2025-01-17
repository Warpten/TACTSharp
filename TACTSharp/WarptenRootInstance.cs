using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static TACTSharp.ArrayExtensions;

namespace TACTSharp
{
    /// <summary>
    /// Warpten's excessive Root parser.
    /// </summary>
    public class WarptenRoot
    {
        private readonly Page[] _pages;

        [Flags]
        public enum LocaleFlags : uint
        {
            All = 0xFFFFFFFF,
            None = 0,
            Unk_1 = 0x1,
            enUS = 0x2,
            koKR = 0x4,
            Unk_8 = 0x8,
            frFR = 0x10,
            deDE = 0x20,
            zhCN = 0x40,
            esES = 0x80,
            zhTW = 0x100,
            enGB = 0x200,
            enCN = 0x400,
            enTW = 0x800,
            esMX = 0x1000,
            ruRU = 0x2000,
            ptBR = 0x4000,
            itIT = 0x8000,
            ptPT = 0x10000,
            enSG = 0x20000000, // custom
            plPL = 0x40000000, // custom
            All_WoW = enUS | koKR | frFR | deDE | zhCN | esES | zhTW | enGB | esMX | ruRU | ptBR | itIT | ptPT
        }

        [Flags]
        public enum ContentFlags : uint
        {
            None = 0,
            LoadOnWindows = 0x8,        // added in 7.2.0.23436
            LoadOnMacOS = 0x10,         // added in 7.2.0.23436
            LowViolence = 0x80,         // many models have this flag
            DoNotLoad = 0x100,          // unused in 9.0.5
            UpdatePlugin = 0x800,       // UpdatePlugin.dll / UpdatePlugin.dylib only
            Encrypted = 0x8000000,      // File is encrypted
            NoNames = 0x10000000,       // No lookup hash
            UncommonRes = 0x20000000,   // added in 7.0.3.21737
            Bundle = 0x40000000,        // unused in 9.0.5
            NoCompression = 0x80000000  // sounds have this flag
        }

        public unsafe WarptenRoot(string filePath)
        {
            var rootFile = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            var accessor = rootFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var mmapViewHandle = accessor.SafeMemoryMappedViewHandle;

            byte* rawData = null;
            mmapViewHandle.AcquirePointer(ref rawData);

            var fileSize = new FileInfo(filePath).Length;
            var fileData = new ReadOnlySpan<byte>(rawData, (int) fileSize);

            var magic = fileData.ReadUInt32LE();
            var (format, version, headerSize, totalFileCount, namedFileCount) = magic switch {
                0x4D465354 => ParseMFST(fileData),
                _ => (Format.Legacy, 0, 12, 0, 0)
            };

            // Skip the header.
            fileData = fileData[headerSize..];

            var allowUnnamedFiles = format == Format.MFST && totalFileCount != namedFileCount;

            var pages = new List<Page>();
            while (fileData.Length != 0) {
                var recordCount = fileData.Consume(4).ReadInt32LE();
                var pageHeader = ParseManifestPageHeader(ref fileData, version);

                // No records in this file.
                if (recordCount == 0)
                    continue;

                // Determine conditions related to keeping this page.
                var localeSkip = !pageHeader.HasFlag(LocaleFlags.All_WoW) && !pageHeader.HasFlag(Settings.Locale);
                var contentSkip = pageHeader.HasFlag(ContentFlags.LowViolence);

                // Calculate block size
                var blockSize = 4 * recordCount;
                blockSize += MD5.Length * recordCount;
                if (format == Format.Legacy || !(allowUnnamedFiles && !pageHeader.HasNames))
                    blockSize += 8 * recordCount;

                var blockData = fileData.Consume(blockSize);
                if (localeSkip || contentSkip)
                    continue;
                
                // Read a FDID delta array from the file (+1 implied) and adjust instantly.
                var fdids = blockData.ReadInt32LE(recordCount);
                for (var i = 1; i < fdids.Length; ++i) {
                    Debug.Assert(fdids[i] >= 0);

                    fdids[i] += fdids[i - 1] + 1;
                }

                blockData = blockData[(4 * recordCount) ..];

                // Parse records according to their specification.
                var records = format switch {
                    Format.Legacy => ParseLegacy(ref blockData, recordCount, fdids, pageHeader),
                    Format.MFST => ParseManifest(ref blockData, recordCount, allowUnnamedFiles, fdids, pageHeader),
                    _ => throw new UnreachableException()
                };

                var page = new Page(pageHeader, records);
                pages.Add(page);
            }

            _pages = [.. pages];
        }

        private static PageHeader ParseManifestPageHeader(ref ReadOnlySpan<byte> fileData, int version) {
            switch (version)
            {
                case 0:
                case 1:
                {
                    var contentFlags = (ContentFlags) fileData.ReadUInt32LE();
                    var localeFlags = (LocaleFlags) fileData[4..].ReadUInt32LE();

                    fileData.Consume(4 + 4);

                    return new(contentFlags, localeFlags);
                }
                case 2:
                {
                    var localeFlags = (LocaleFlags) fileData.ReadUInt32LE();

                    var unk1 = fileData[4..].ReadUInt32LE();
                    var unk2 = fileData[8..].ReadUInt32LE();
                    var unk3 = ((uint) fileData[12]) << 17;

                    fileData = fileData[13..];

                    var contentFlags = (ContentFlags) (unk1 | unk2 | unk3);

                    return new(contentFlags, localeFlags);
                }
                default:
                    throw new NotImplementedException($"MFST version {version} is not supported");
            }
        }

        /// <summary>
        /// Finds a file given a file data ID.
        /// </summary>
        /// <param name="fileDataID">The file data ID to look for.</param>
        /// <returns>An optional record as well as the associated content and locale flags.</returns>
        public ref readonly Record FindFileDataID(uint fileDataID)
        {
            foreach (ref readonly var page in _pages.AsSpan()) {
                var fdidIndex = page.Records.BinarySearchBy((ref Record record) => (record.FileDataID - (int) fileDataID).ToOrdering());
                if (fdidIndex == -1)
                    continue;

                return ref page.Records.UnsafeIndex(fdidIndex);
            }
            
            return ref Unsafe.NullRef<Record>();
        }

        /// <summary>
        /// Finds a record as identified by its name hash (also known as lookup).
        /// </summary>
        /// <param name="nameHash">The hash of the file's complete path in the game's file structure.</param>
        /// <returns>An optional record as well as the associated content and locale flags.</returns>
        public ref readonly Record FindHash(ulong nameHash)
        {
            foreach (ref readonly var page in _pages.AsSpan()) {
                if (!page.Header.HasNames)
                    continue;

                for (var i = 0; i < page.Records.Length; ++i) {
                    ref readonly var record = ref page.Records.UnsafeIndex(i);
                    if (record.NameHash == nameHash)
                        return ref record;
                }
            }

            return ref Unsafe.NullRef<Record>();
        }

        private Record[] ParseLegacy(ref ReadOnlySpan<byte> dataStream, int recordCount, int[] fdids, PageHeader header)
        {
            var records = GC.AllocateUninitializedArray<Record>(recordCount);
            for (var i = 0; i < records.Length; ++i) {
                var contentKey = new MD5(dataStream.Consume(MD5.Length));
                var nameHash = dataStream.Consume(8).ReadUInt64LE();

                records[i] = new(contentKey, nameHash, fdids[i]);
            }

            return records;
        }

        private static Record[] ParseManifest(ref ReadOnlySpan<byte> dataStream, int recordCount,
            bool allowUnnamedFiles, int[] fdids, PageHeader pageHeader)
        {
            var nameHashSize = (!(allowUnnamedFiles && !pageHeader.HasNames)).UnsafePromote() << 3;
            nameHashSize *= recordCount;

            var ckr = new Range(0, recordCount * MD5.Length); // Content key range
            var nhr = new Range(ckr.End.Value, ckr.End.Value + nameHashSize); // Name hash range

            var sectionContents = dataStream.Consume(nhr.End.Value);
            var contentKeys = MemoryMarshal.Cast<byte, MD5>(sectionContents[ckr]);
            // TODO: This becomes tied to platform endianness.
            var nameHashes = MemoryMarshal.Cast<byte, ulong>(sectionContents[nhr]);

            var records = GC.AllocateUninitializedArray<Record>(recordCount);
            for (var i = 0; i < recordCount; ++i)
            {
                var nameHash = nameHashes.Length switch {
                    >= 8 => nameHashes[i],
                    _ => 0uL
                };

                records[i] = new(contentKeys[i], nameHash, fdids[i]);
            }

            return records;
        }

        private static (Format, int Version, int HeaderSize, int TotalFileCount, int NamedFileCount) ParseMFST(ReadOnlySpan<byte> dataStream)
        {
            // Skip over magic at dataStream[0]
            Debug.Assert(dataStream.ReadUInt32LE() == 0x4D465354);

            var headerSize = dataStream[4..].ReadInt32LE();
            var version = dataStream[8..].ReadInt32LE();
            if (headerSize > 1000)
                return (Format.MFST, 0, 4 * 4, headerSize, version);

            var totalFileCount = dataStream[12..].ReadInt32LE();
            var namedFileCount = dataStream[16..].ReadInt32LE();

            return (Format.MFST, version, headerSize, totalFileCount, namedFileCount);
        }

        [InlineArray(16)]
        public struct MD5
        {
            public const int Length = 16;

            private byte _element;

            public MD5(ReadOnlySpan<byte> sourceData) => sourceData.CopyTo(MemoryMarshal.CreateSpan(ref _element, Length));

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Span<byte> AsSpan() => MemoryMarshal.CreateSpan(ref _element, Length);
        }

        public struct Record(MD5 contentKey, ulong nameHash, int fileDataID) {
            public readonly MD5 ContentKey = contentKey;
            public readonly ulong NameHash = nameHash;
            public readonly int FileDataID = fileDataID;
        }
        
        private record struct PageHeader(ContentFlags ContentFlags, LocaleFlags LocaleFlags) {
            public bool HasNames = !ContentFlags.HasFlag(ContentFlags.NoNames);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool HasFlag(ContentFlags contentFlags) => ContentFlags.HasFlag(contentFlags);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool HasFlag(LocaleFlags localeFlags) => LocaleFlags.HasFlag(localeFlags);
        }

        private record struct Page(PageHeader Header, Record[] Records);

        private enum Format
        {
            Legacy,
            MFST
        }
    }
}