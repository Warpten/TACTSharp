using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TACTSharp
{
    public static class SpanExtensions
    {
        public static unsafe nuint PointerDistance(this ReadOnlySpan<byte> src, ReadOnlySpan<byte> dst)
        {
            var lhs = (byte*) Unsafe.AsPointer(ref MemoryMarshal.GetReference(src));
            var rhs = (byte*) Unsafe.AsPointer(ref MemoryMarshal.GetReference(dst));

            return (nuint) (lhs - rhs);
        }

        public static unsafe T* AsPointer<T>(this ReadOnlySpan<T> self) where T : unmanaged {
            return (T*) Unsafe.AsPointer(ref MemoryMarshal.GetReference(self));
        }
    }
}