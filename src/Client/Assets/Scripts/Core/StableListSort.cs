using System;
using System.Collections.Generic;

namespace Mmorpg.Client.Core
{
    // Sorts a list in place, keeping equal items in the order they were in, without allocating,
    // for code that sorts every frame. List.Sort allocates a comparison on every call in the
    // client's runtime, also when it is given an IComparer, and it does not keep the order of
    // equal items. Runs of a few items sort by insertion, and a longer list merges its runs through
    // a buffer that the caller keeps, which grows once to the list's size.
    public static class StableListSort
    {
        // Runs of at most this many items sort by insertion; a list of at most this many needs
        // no buffer.
        public const int RunLength = 16;

        public static void Sort<T>(List<T> items, IComparer<T> comparer, List<T> buffer)
        {
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            if (comparer == null)
            {
                throw new ArgumentNullException(nameof(comparer));
            }

            var count = items.Count;
            for (var start = 0; start < count; start += RunLength)
            {
                InsertionSort(items, start, Math.Min(start + RunLength, count), comparer);
            }

            if (count <= RunLength)
            {
                return;
            }

            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            buffer.Clear();
            for (var index = 0; index < count; index++)
            {
                buffer.Add(items[index]);
            }

            var source = items;
            var target = buffer;
            for (var width = RunLength; width < count; width *= 2)
            {
                for (var left = 0; left < count; left += 2 * width)
                {
                    Merge(source, target, left, Math.Min(left + width, count), Math.Min(left + (2 * width), count), comparer);
                }

                var merged = target;
                target = source;
                source = merged;
            }

            if (!ReferenceEquals(source, items))
            {
                for (var index = 0; index < count; index++)
                {
                    items[index] = source[index];
                }
            }

            // The buffer keeps its size but no references to the items.
            buffer.Clear();
        }

        private static void InsertionSort<T>(List<T> items, int start, int end, IComparer<T> comparer)
        {
            for (var index = start + 1; index < end; index++)
            {
                var item = items[index];
                var at = index - 1;
                while (at >= start && comparer.Compare(items[at], item) > 0)
                {
                    items[at + 1] = items[at];
                    at--;
                }

                items[at + 1] = item;
            }
        }

        // Merges the sorted runs [left, middle) and [middle, right) of the source into the target;
        // of equal items the one from the left run comes first.
        private static void Merge<T>(List<T> source, List<T> target, int left, int middle, int right, IComparer<T> comparer)
        {
            var fromLeft = left;
            var fromRight = middle;
            for (var index = left; index < right; index++)
            {
                if (fromLeft < middle
                    && (fromRight >= right || comparer.Compare(source[fromLeft], source[fromRight]) <= 0))
                {
                    target[index] = source[fromLeft++];
                }
                else
                {
                    target[index] = source[fromRight++];
                }
            }
        }
    }
}
