using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // A world click that a higher feature can take before it becomes a walk, such as a
    // click on a Mob, an NPC, or a corpse.
    public interface IWorldClickHandler
    {
        bool TryHandleWorldClick(Ray ray);
    }

    // The order in which features see a world click; a lower value goes first. Combat goes
    // before world interaction, because it ignores every click while the character is dead
    // and takes clicks on attackable targets.
    public static class WorldClickOrder
    {
        public const int Combat = 100;
        public const int WorldInteraction = 200;
    }

    // World click handlers sorted by their order, so the order never depends on which
    // feature registered first. Handlers of equal order keep their registration order. The
    // first handler that takes a click ends it.
    public sealed class WorldClickHandlerList
    {
        private readonly List<Entry> entries = new List<Entry>();

        public int Count
        {
            get { return entries.Count; }
        }

        // Adding a handler again moves it to its new order.
        public void Add(IWorldClickHandler handler, int order)
        {
            if (handler == null)
            {
                return;
            }

            Remove(handler);
            var index = entries.Count;
            while (index > 0 && entries[index - 1].Order > order)
            {
                index--;
            }

            entries.Insert(index, new Entry(handler, order));
        }

        public bool Remove(IWorldClickHandler handler)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                if (ReferenceEquals(entries[index].Handler, handler))
                {
                    entries.RemoveAt(index);
                    return true;
                }
            }

            return false;
        }

        public IWorldClickHandler HandlerAt(int index)
        {
            return entries[index].Handler;
        }

        public bool TryHandle(Ray ray)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                if (entries[index].Handler.TryHandleWorldClick(ray))
                {
                    return true;
                }
            }

            return false;
        }

        private readonly struct Entry
        {
            public Entry(IWorldClickHandler handler, int order)
            {
                Handler = handler;
                Order = order;
            }

            public IWorldClickHandler Handler { get; }

            public int Order { get; }
        }
    }
}
