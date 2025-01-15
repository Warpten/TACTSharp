namespace TACTSharp {

    public static class ListExtensions
    {
        public delegate U Transform<T, U>(T value)
            where U : IComparable<U>, allows ref struct;

        public static void SortBy<T, U>(this List<T> list, Transform<T, U> transform)
            where U : IComparable<U>, allows ref struct
        {
            list.Sort((left, right) =>
            {
                var leftReference = transform(left);
                var rightReference = transform(right);

                return leftReference.CompareTo(rightReference);
            });
        }
    }
}