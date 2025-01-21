using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TACTSharp.Instance
{
    /// <summary>
    /// Provides utility methods to parse configuration files retrieve from the Battle.net patch servers.
    /// </summary>
    public sealed class ConfigurationFile
    {
        private delegate T Merge<T, U>(T lhs, U rhs) where U : allows ref struct;
        private delegate U Transform<T, U>(T source) where T : allows ref struct;

        public delegate T Handler<T>((Range Name, Range Value)[] data, ReadOnlySpan<byte> fileData);
        public delegate T[] ArrayHandler<T>((Range Name, Range Value)[] data, ReadOnlySpan<byte> fileData);

        public static T? ParseOne<T>(ReadOnlySpan<byte> fileData, Handler<T> handler)
        {
            var lineEnumerator = fileData.Split((byte)'\n');

            List<Range> headerTokens = [];

            while (lineEnumerator.MoveNext())
            {
                var lineRange = lineEnumerator.Current;
                var line = fileData[lineRange];
                if (line.Length == 0 || (line.Length >= 2 && Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(line)) == 0x2323)) // ##
                    continue;

                foreach (var propRange in line.Split((byte)'|'))
                {
                    var typeMarker = line[propRange].IndexOf((byte)'!');
                    Debug.Assert(typeMarker != -1);

                    // Rebase the range to the start of the file blob
                    var tokenStart = lineRange.Start.Value + propRange.Start.Value;
                    headerTokens.Add(new Range(tokenStart, tokenStart + typeMarker));
                }

                break;
            }

            if (headerTokens.Count == 0)
                return default;

            while (lineEnumerator.MoveNext())
            {
                Debug.Assert(headerTokens.Count != 0);

                var lineRange = lineEnumerator.Current;
                var line = fileData[lineRange];
                if (line.Length == 0 || (line.Length >= 2 && Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(line)) == 0x2323)) // ##
                    continue;

                var recordData = GC.AllocateUninitializedArray<(Range Name, Range Value)>(headerTokens.Count);
                var i = 0;
                foreach (var valueRange in line.Split((byte)'|'))
                {
                    var tokenInfo = headerTokens[i];

                    var valueStart = lineRange.Start.Value + valueRange.Start.Value;
                    var valueEnd = lineRange.Start.Value + valueRange.End.Value;

                    recordData[i] = (tokenInfo, new Range(valueStart, valueEnd));
                    ++i;
                }

                var record = handler(recordData, fileData);
                if (record != null)
                    return record;
            }

            return default;
        }

        // [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Aggregate ParseInternal<Result, Aggregate, Accumulator>(ReadOnlySpan<byte> fileData, Handler<Result> handler,
            Merge<Accumulator, Result> merge, Transform<Accumulator, Aggregate> transform, Accumulator accumulator, Aggregate defaultValue)
        {
            List<Range> headerTokens = [];

            var lineEnumerator = fileData.Split((byte)'\n');
            while (lineEnumerator.MoveNext())
            {
                var lineRange = lineEnumerator.Current;
                var line = fileData[lineRange];
                if (line.Length == 0 || (line.Length >= 2 && Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(line)) == 0x2323)) // ##
                    continue;

                foreach (var propRange in line.Split((byte)'|'))
                {
                    var typeMarker = line[propRange].IndexOf((byte)'!');
                    Debug.Assert(typeMarker != -1);

                    // Rebase the range to the start of the file blob
                    var tokenStart = lineRange.Start.Value + propRange.Start.Value;
                    headerTokens.Add(new Range(tokenStart, tokenStart + typeMarker));
                }

                break;
            }

            if (headerTokens.Count == 0)
                return defaultValue;

            while (lineEnumerator.MoveNext())
            {
                Debug.Assert(headerTokens.Count != 0);

                var lineRange = lineEnumerator.Current;
                var line = fileData[lineRange];
                // Skip empty lines and lines starting with ##
                if (line.Length == 0 || (line.Length >= 2 && Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(line)) == 0x2323))
                    continue;

                // Preallocate ranges
                var recordData = GC.AllocateUninitializedArray<(Range Name, Range Value)>(headerTokens.Count);
                // Traverse value ranges and store
                var i = 0;
                foreach (var valueRange in line.Split((byte)'|'))
                {
                    var tokenInfo = headerTokens[i];

                    var valueStart = lineRange.Start.Value + valueRange.Start.Value;
                    var valueEnd = lineRange.Start.Value + valueRange.End.Value;

                    recordData[i] = (tokenInfo, new Range(valueStart, valueEnd));
                    ++i;
                }

                // Forward to the handler
                var lineData = handler(recordData, fileData);
                accumulator = merge(accumulator, lineData);
            }

            return transform(accumulator);
        }

        public static T[] Parse<T>(ReadOnlySpan<byte> fileData, Handler<T[]> handler)
        {
            return ParseInternal<T[], T[], List<T>>(fileData, handler,
                static (collection, block) =>
                {
                    collection.AddRange(block);
                    return collection;
                },
                static (collection) => [.. collection],
                [],
                []
            );
        }

        public static T? ParseFirst<T>(ReadOnlySpan<byte> fileData, Handler<T?> handler)
        {
            return ParseInternal<T?, T?, T?>(fileData, handler, (prev, current) => prev ?? current, x => x, default, default);
        }

        public static T[] Parse<T>(ReadOnlySpan<byte> fileData, Handler<T?> handler)
        {
            return ParseInternal<T?, T[], List<T>>(fileData, handler,
                static (collection, item) => {
                    if (item != null)
                        collection.Add(item!);
                    return collection;
                },
                static acc => [.. acc],
                [], []
            );
        }
    }
}
