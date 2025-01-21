using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TACTSharp.Instance
{
#pragma warning disable CS0660, CS0661
    public readonly struct Resource
    {
        private readonly FileInfo _fileInfo;
        private readonly long _offset;
        private readonly long _length;

        public readonly string Path => _fileInfo.FullName;
        public readonly bool Exists => _fileInfo.Exists;

        internal FileInfo FileInfo => _fileInfo;

        internal Resource(FileInfo fileInfo, long offset = 0, long length = 0)
        {
            _fileInfo = fileInfo;
            _offset = offset;
            _length = length == 0 && _fileInfo.Exists ? _fileInfo.Length : length;
        }

        public readonly Resource Decompress(ulong decompressedSize = 0)
        {
            if (!_fileInfo.Exists || _fileInfo.Length == 0)
                return this;

            var decompressedFile = new FileInfo(Path + ".decompressed");
            if (decompressedFile.Exists)
                return new Resource(decompressedFile);

            return OpenMemoryMapped(compressedData =>
            {
                var decompressedData = BLTE.Decode(compressedData, decompressedSize);
                File.WriteAllBytes(decompressedFile.FullName, decompressedData);
                decompressedFile.Refresh();

                return new Resource(decompressedFile);
            });
        }

        public delegate T Parser<T>(ReadOnlySpan<byte> data);

        public readonly T? OpenMemoryMapped<T>(Parser<T?> parser)
            => OpenMemoryMapped(parser, static () => default(T));

        public readonly T OpenMemoryMapped<T>(Parser<T> parser, Func<T> defaultSupplier)
        {
            if (_fileInfo.Length == 0)
                return defaultSupplier();

            using var file = MemoryMappedFile.CreateFromFile(Path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = file.CreateViewAccessor(0, _fileInfo.Length, MemoryMappedFileAccess.Read);

            unsafe
            {
                byte* pointer = null;

                try
                {
                    accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);

                    var dataSpan = new ReadOnlySpan<byte>(pointer, (int) _fileInfo.Length);
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
