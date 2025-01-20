using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using static TACTSharp.CDN;

namespace TACTSharp
{
    // CS0660: Type defines operator == or operator != but does not override Object.Equals(object o)
    // CS0661: Type defines operator == or operator != but does not override Object.GetHashCode()
#pragma warning disable CS0660, CS0661
    public readonly struct Resource
    {
        public readonly string Path;
        public readonly long Length;
        public readonly bool Exists;
        private readonly long _offset;

        public static readonly Resource Empty = new(string.Empty, 0, 0);

        internal Resource(string path, long offset, long length)
        {
            Path = path;
            _offset = offset;
            Length = length;
            Exists = File.Exists(path);
        }

        public readonly Resource Decompress(ulong decompressedSize = 0)
        {
            if (Length == 0)
                return new Resource(Path + ".decompressed", 0, 0);

            var decompressedFile = new FileInfo(Path + ".decompressed");
            if (decompressedFile.Exists)
                return new Resource(decompressedFile.FullName, 0, decompressedFile.Length);

            return OpenMap(compressedData =>
            {
                var decompressedData = BLTE.Decode(compressedData, decompressedSize);
                File.WriteAllBytes(decompressedFile.FullName, decompressedData);
                return new Resource(decompressedFile.FullName, 0, decompressedData.LongLength);
            });
        }

        public readonly T? OpenMap<T>(Parser<T> parser)
        {
            if (Length == 0) return default;

            using var file = MemoryMappedFile.CreateFromFile(Path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = file.CreateViewAccessor(_offset, Length, MemoryMappedFileAccess.Read);

            unsafe
            {
                byte* pointer = null;

                try
                {
                    accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);

                    var dataSpan = new ReadOnlySpan<byte>(pointer, (int)Length);
                    return parser(dataSpan);
                }
                finally
                {
                    accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
    }
#pragma warning restore CS0661, CS0661
}
