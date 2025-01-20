using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Threading.Tasks;

namespace TACTSharp.Instance
{
    public sealed class ConfigurationFile<T>
    {
        public delegate T? Handler((Range Name, Range Value)[] data, ReadOnlySpan<byte> fileData);
        public delegate T[] ArrayHandler((Range Name, Range Value)[] data, ReadOnlySpan<byte> fileData);

        public static T? ParseOne(ReadOnlySpan<byte> fileData, Handler handler)
        {
            var lineEnumerator = fileData.Split((byte)'\n');

            List<Range> headerTokens = [];
            List<T> records = [];

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

        public static T[] Parse(ReadOnlySpan<byte> fileData, Handler handler)
        {
            var lineEnumerator = fileData.Split((byte) '\n');

            List<Range> headerTokens = [];
            List<T> records = [];

            while (lineEnumerator.MoveNext())
            {
                var lineRange = lineEnumerator.Current;
                var line = fileData[lineRange];
                if (line.Length == 0 || (line.Length >= 2 && Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(line)) == 0x2323)) // ##
                    continue;

                foreach (var propRange in line.Split((byte) '|'))
                {
                    var typeMarker = line[propRange].IndexOf((byte) '!');
                    Debug.Assert(typeMarker != -1);

                    // Rebase the range to the start of the file blob
                    var tokenStart = lineRange.Start.Value + propRange.Start.Value;
                    headerTokens.Add(new Range(tokenStart, tokenStart + typeMarker));
                }

                break;
            }

            if (headerTokens.Count == 0)
                return [];

            while (lineEnumerator.MoveNext())
            {
                Debug.Assert(headerTokens.Count != 0);

                var lineRange = lineEnumerator.Current;
                var line = fileData[lineRange];
                if (line.Length == 0 || (line.Length >= 2 && Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(line)) == 0x2323)) // ##
                    continue;

                var recordData = GC.AllocateUninitializedArray<(Range Name, Range Value)>(headerTokens.Count);
                var i = 0;
                foreach (var valueRange in line.Split((byte) '|'))
                {
                    var tokenInfo = headerTokens[i];

                    var valueStart = lineRange.Start.Value + valueRange.Start.Value;
                    var valueEnd = lineRange.Start.Value + valueRange.End.Value;

                    recordData[i] = (tokenInfo, new Range(valueStart, valueEnd));
                    ++i;
                }

                var record = handler(recordData, fileData);
                if (record != null)
                    records.Add(record!);
            }
            
            return [.. records];
        }

        public static T[] Parse(ReadOnlySpan<byte> fileData, ArrayHandler handler)
        {
            var lineEnumerator = fileData.Split((byte)'\n');

            List<Range> headerTokens = [];
            List<T> records = [];

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
                return [];

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
                    records.AddRange(record!);
            }

            return [.. records];
        }
    }
}
