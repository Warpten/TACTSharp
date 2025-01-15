using System.Diagnostics;

namespace TACTSharp
{
    public static class ArrayExtensions 
    {
        public enum Ordering
        {
            Less,
            Equal,
            Greater,
        }

        public delegate Ordering BinarySearchPredicate<T>(T entry);

        public static Ordering ToOrdering(this int comparison)
            => comparison switch
            {
                > 0 => Ordering.Greater,
                < 0 => Ordering.Less,
                0 => Ordering.Equal,
            };

        /// <summary>
        /// Performs a binary search with the given predicate.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="U">An argument to carry around to the predicate.</typeparam>
        /// <param name="cmp">A predicate to use to determine ordering.</param>
        /// <returns>The index of a corresponding entry or -1 if none was found.</returns>
        public static int BinarySearchBy<T>(this T[] array, BinarySearchPredicate<T> cmp)
        {
            var size = array.Length;
            var left = 0;
            var right = size;

            while (left < right)
            {
                var mid = left + size / 2;
                var ordering = cmp(array[mid]);

                left = ordering switch
                {
                    Ordering.Less => mid + 1,
                    _ => left
                };

                right = ordering switch
                {
                    Ordering.Greater => mid,
                    _ => right
                };

                if (ordering == Ordering.Equal)
                {
                    Debug.Assert(mid < array.Length);
                    return mid;
                }

                size = right - left;
            }

            Debug.Assert(left < array.Length);
            return -1;
        }
    }
}
