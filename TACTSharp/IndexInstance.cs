using Microsoft.Win32.SafeHandles;
using System.Collections;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography.X509Certificates;

namespace TACTSharp
{
    // mostly based on schlumpf's implementation, but with some changes because i dont know how to port some c++ things to c# properly
    public sealed class IndexInstance
    {
        private readonly long indexSize;
        private IndexFooter footer;
        private readonly short archiveIndex = -1;
        private readonly bool isFileIndex;
        private readonly bool isGroupArchive;

        private readonly MemoryMappedFile indexFile;
        private readonly MemoryMappedViewAccessor accessor;
        private readonly SafeMemoryMappedViewHandle mmapViewHandle;

        private readonly int blockSizeBytes;
        private readonly int entrySize;
        private readonly int entriesPerBlock;
        private readonly int entriesInLastBlock;
        private readonly int numBlocks;
        private readonly int ofsStartOfToc;
        private readonly int ofsEndOfTocEkeys;

        public IndexInstance(string path, short archiveIndex = -1)
        {
            this.archiveIndex = archiveIndex;
            this.indexSize = new FileInfo(path).Length;

            this.indexFile = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            this.accessor = indexFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            this.mmapViewHandle = accessor.SafeMemoryMappedViewHandle;

            using (var accessor = this.indexFile.CreateViewAccessor(this.indexSize - 20, 20, MemoryMappedFileAccess.Read))
                accessor.Read(0, out footer);

            isFileIndex = footer.offsetBytes == 0;
            isGroupArchive = footer.offsetBytes == 6;

            this.blockSizeBytes = footer.blockSizeKBytes << 10;
            this.entrySize = footer.keyBytes + footer.sizeBytes + footer.offsetBytes;
            this.entriesPerBlock = this.blockSizeBytes / this.entrySize;
            this.numBlocks = (int)Math.Ceiling((double)footer.numElements / this.entriesPerBlock);
            this.entriesInLastBlock = (int)footer.numElements - (this.numBlocks - 1) * this.entriesPerBlock;

            this.ofsStartOfToc = this.numBlocks * this.blockSizeBytes;
            this.ofsEndOfTocEkeys = ofsStartOfToc + footer.keyBytes * this.numBlocks;
        }

        // Binary search pointing to the first element **not** comparing SequenceCompareTo < 0 anymore.
        // [1 3 4 6]: 0 -> 1; 1 -> 1; 2 -> 3; 3 -> 3; 4 -> 4; 5 -> 6; 6 -> 6; 7 -> end.
        unsafe static private byte* LowerBoundEkey(byte* begin, byte* end, long dataSize, ReadOnlySpan<byte> needle)
        {
            var count = (end - begin) / dataSize;

            while (count > 0)
            {
                var it = begin;
                var step = count / 2;
                it += step * dataSize;

                if (new ReadOnlySpan<byte>(it, needle.Length).SequenceCompareTo(needle) < 0)
                {
                    it += dataSize;
                    begin = it;
                    count -= step + 1;
                }
                else
                {
                    count = step;
                }
            }

            return begin;
        }

        public readonly ref struct Entry(ReadOnlySpan<byte> encodingKey, int offset, int size) {
            public readonly ReadOnlySpan<byte> EKey = encodingKey;
            public readonly int Offset = offset;
            public readonly int Size = size;

            internal void Deconstruct(out ReadOnlySpan<byte> eKey, out int offset, out int size)
            {
                eKey = EKey;
                offset = Offset;
                size = Size;
            }
        }

        private unsafe class Enumerator : IEnumerable<Entry>, IEnumerator<Entry> {
            private byte* rawData;

            // Block iteration
            private int blockIndex = 0;

            // Entries iteration per block
            private int entryIndex = -1;

            private IndexInstance index;
            
            public Enumerator(IndexInstance index)
            {
                this.index = index;
                this.index.mmapViewHandle.AcquirePointer(ref this.rawData);
            }

            public Entry Current => Find(this.blockIndex, this.entryIndex);

            private Entry Find(int blockIdx, int entryIdx) {
                ReadOnlySpan<byte> blockData = new (this.rawData + this.index.blockSizeBytes * blockIdx, this.index.blockSizeBytes);
                var entry = blockData.Slice(entryIdx * this.index.entrySize, this.index.entrySize);
                
                var eKey = entry[..this.index.footer.keyBytes];
                var offset = entry.Slice(this.index.footer.keyBytes + this.index.footer.sizeBytes, this.index.footer.offsetBytes).ReadInt32BE();
                var size = entry.Slice(this.index.footer.keyBytes, this.index.footer.sizeBytes).ReadInt32BE();

                // Debug.Assert(size != 0);
                return new Entry(eKey, offset, size);
            }

            object IEnumerator.Current => throw new InvalidOperationException();

            public bool MoveNext() {
                // Someone optimize this to eliminate branches as much as possible
                if (this.blockIndex >= this.index.numBlocks)
                    return false;

                if (this.entryIndex >= this.index.entriesPerBlock)
                    return false;

                while (this.blockIndex < this.index.numBlocks) {
                    ++this.entryIndex;
                    if (this.entryIndex < this.index.entriesPerBlock) {
                        if (Current.Size != 0)
                            return true;
                    }

                    this.entryIndex = -1;
                    ++this.blockIndex;
                }

                // Expended all blocks
                return false;
            }

            public void Reset() {
                this.entryIndex = 0;
                this.blockIndex = 0;
            }

            public IEnumerator<Entry> GetEnumerator() => this;
            IEnumerator IEnumerable.GetEnumerator() => this;

            public void Dispose() {
                if (this.rawData != null)
                    this.index.mmapViewHandle.ReleasePointer();

                this.rawData = null;
            }
        }

        public IEnumerable<Entry> Enumerate() {
            return new Enumerator(this);
        }

        unsafe public (int offset, int size, int archiveIndex) GetIndexInfo(ReadOnlySpan<byte> eKeyTarget)
        {
            byte* fileData = null;
            try
            {
                mmapViewHandle.AcquirePointer(ref fileData);

                byte* startOfToc = fileData + this.ofsStartOfToc;
                byte* endOfTocEkeys = fileData + this.ofsEndOfTocEkeys;

                byte* lastEkey = LowerBoundEkey(startOfToc, endOfTocEkeys, footer.keyBytes, eKeyTarget);
                if (lastEkey == endOfTocEkeys)
                    return (-1, -1, -1);

                var blockIndexMaybeContainingEkey = (lastEkey - startOfToc) / footer.keyBytes;

                var ofsStartOfCandidateBlock = blockIndexMaybeContainingEkey * this.blockSizeBytes;
                var entriesOfCandidateBlock = blockIndexMaybeContainingEkey != this.numBlocks - 1 ? this.entriesPerBlock : this.entriesInLastBlock;
                var ofsEndOfCandidateBlock = ofsStartOfCandidateBlock + this.entrySize * entriesOfCandidateBlock;

                byte* startOfCandidateBlock = fileData + ofsStartOfCandidateBlock;
                byte* endOfCandidateBlock = fileData + ofsEndOfCandidateBlock;

                byte* candidate = LowerBoundEkey(startOfCandidateBlock, endOfCandidateBlock, this.entrySize, eKeyTarget);

                if (candidate == endOfCandidateBlock)
                    return (-1, -1, -1);

                var entry = new ReadOnlySpan<byte>(candidate, this.entrySize);
                if (entry[..footer.keyBytes].SequenceCompareTo(eKeyTarget) != 0)
                    return (-1, -1, -1);

                if (isGroupArchive)
                {
                    var encodedSize = entry.Slice(footer.keyBytes, footer.sizeBytes).ReadInt32BE();
                    var fileArchiveIndex = entry.Slice(footer.keyBytes + footer.sizeBytes, 2).ReadInt16BE();
                    var offset = entry.Slice(footer.keyBytes + footer.sizeBytes + 2, 4).ReadInt32BE();
                    return (offset, encodedSize, fileArchiveIndex);
                }
                else if (isFileIndex)
                {
                    var encodedSize = entry.Slice(footer.keyBytes, footer.sizeBytes).ReadInt32BE();
                    return (-1, encodedSize, archiveIndex);
                }
                else
                {
                    var encodedSize = entry.Slice(footer.keyBytes, footer.sizeBytes).ReadInt32BE();
                    var offset = entry.Slice(footer.keyBytes + footer.sizeBytes, footer.offsetBytes).ReadInt32BE();
                    return (offset, encodedSize, archiveIndex);
                }
            }
            finally
            {
                if (fileData != null)
                    mmapViewHandle.ReleasePointer();
            }
        }

        //  Field '...' is never assigned to, and will always have its default value ...
        #pragma warning disable 0649
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
        #pragma warning restore 0649
    }
}