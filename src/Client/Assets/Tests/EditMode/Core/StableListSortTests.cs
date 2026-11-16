using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Core;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // The sort for code that sorts every frame: it orders a list as a stable sort does, equal items
    // in the order they were in, for short lists and long ones, and allocates nothing once its
    // buffer has grown, which List.Sort does on every call in the client's runtime.
    public sealed class StableListSortTests
    {
        private static readonly ByKey Ascending = new ByKey(false);
        private static readonly ByKey Descending = new ByKey(true);

        [Test]
        public void ItSortsAsAStableSortDoesAtEveryLength()
        {
            var random = new System.Random(11);
            var buffer = new List<Item>();
            foreach (var count in new[] { 0, 1, 2, 3, 15, 16, 17, 31, 32, 33, 100, 257 })
            {
                foreach (var keys in new[] { 4, 1000 })
                {
                    var items = Enumerable.Range(0, count).Select(index => new Item(random.Next(keys), index)).ToList();
                    var ascending = new List<Item>(items);
                    StableListSort.Sort(ascending, Ascending, buffer);
                    Assert.That(ascending, Is.EqualTo(items.OrderBy(item => item.Key).ToList()), count + " ascending");
                    Assert.That(buffer, Is.Empty, "the buffer keeps no items");

                    var descending = new List<Item>(items);
                    StableListSort.Sort(descending, Descending, buffer);
                    Assert.That(descending, Is.EqualTo(items.OrderByDescending(item => item.Key).ToList()), count + " descending");
                }
            }
        }

        [Test]
        public void ItAllocatesNothingOnceItsBufferHasGrown()
        {
            var random = new System.Random(5);
            var original = Enumerable.Range(0, 300).Select(index => new Item(random.Next(40), index)).ToList();
            var items = new List<Item>(original);
            var buffer = new List<Item>();
            Assert.That(
                ManagedAllocations.Of(() =>
                {
                    for (var index = 0; index < original.Count; index++)
                    {
                        items[index] = original[index];
                    }

                    StableListSort.Sort(items, Descending, buffer);
                }),
                Is.Zero);
            Assert.That(items, Is.EqualTo(original.OrderByDescending(item => item.Key).ToList()));
        }

        private readonly struct Item
        {
            public Item(int key, int index)
            {
                Key = key;
                Index = index;
            }

            public int Key { get; }

            public int Index { get; }

            public override string ToString()
            {
                return Key + "#" + Index;
            }
        }

        private sealed class ByKey : IComparer<Item>
        {
            private readonly bool descending;

            public ByKey(bool descending)
            {
                this.descending = descending;
            }

            public int Compare(Item first, Item second)
            {
                return descending ? second.Key.CompareTo(first.Key) : first.Key.CompareTo(second.Key);
            }
        }
    }
}
