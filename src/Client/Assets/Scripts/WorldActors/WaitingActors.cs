using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    // The world actors whose view waits to be made, and the order their views are made in a frame:
    // the nearest to the eye first. A frame measures every waiting actor once and heaps them by
    // distance, and each actor taken costs a step down the heap, so hundreds that come into view at
    // once are not scanned again for every view. An actor whose prefab still loads goes back to wait
    // without its place in the heap, so a frame takes it once. Neither ordering nor taking allocates.
    internal sealed class WaitingActors
    {
        private readonly HashSet<ulong> waiting = new HashSet<ulong>();
        private readonly List<Candidate> heap = new List<Candidate>();
        private readonly List<ulong> gone = new List<ulong>();

        public int Count
        {
            get { return waiting.Count; }
        }

        public bool Contains(ulong entityId)
        {
            return waiting.Contains(entityId);
        }

        // The actor waits for its view.
        public void Wait(ulong entityId)
        {
            waiting.Add(entityId);
        }

        public void Remove(ulong entityId)
        {
            waiting.Remove(entityId);
        }

        public void Clear()
        {
            waiting.Clear();
            heap.Clear();
        }

        // Orders the waiting actors by their distance from the eye, nearest first, and of equals the
        // first found; an actor that the state no longer has is forgotten.
        public void OrderFrom(WorldActorClientState state, Vector3 eye)
        {
            heap.Clear();
            gone.Clear();
            foreach (var entityId in waiting)
            {
                if (state.TryGet(entityId, out var actor))
                {
                    var offset = new Vector3(actor.PositionX, actor.PositionY, actor.PositionZ) - eye;
                    heap.Add(new Candidate(actor, offset.sqrMagnitude, heap.Count));
                }
                else
                {
                    gone.Add(entityId);
                }
            }

            for (var index = 0; index < gone.Count; index++)
            {
                waiting.Remove(gone[index]);
            }

            for (var index = (heap.Count / 2) - 1; index >= 0; index--)
            {
                SiftDown(index);
            }
        }

        // The nearest actor in the order that still waits; it waits no longer. False once the
        // order is used up.
        public bool TryTakeNext(out WorldActorClientEntry actor)
        {
            while (heap.Count > 0)
            {
                var nearest = heap[0];
                var last = heap.Count - 1;
                heap[0] = heap[last];
                heap.RemoveAt(last);
                SiftDown(0);
                if (waiting.Remove(nearest.Actor.EntityId))
                {
                    actor = nearest.Actor;
                    return true;
                }
            }

            actor = null;
            return false;
        }

        private void SiftDown(int index)
        {
            var count = heap.Count;
            while (true)
            {
                var nearest = index;
                var left = (2 * index) + 1;
                var right = left + 1;
                if (left < count && IsNearer(heap[left], heap[nearest]))
                {
                    nearest = left;
                }

                if (right < count && IsNearer(heap[right], heap[nearest]))
                {
                    nearest = right;
                }

                if (nearest == index)
                {
                    return;
                }

                var swapped = heap[index];
                heap[index] = heap[nearest];
                heap[nearest] = swapped;
                index = nearest;
            }
        }

        private static bool IsNearer(Candidate first, Candidate second)
        {
            return first.SqrDistance < second.SqrDistance
                || (first.SqrDistance == second.SqrDistance && first.Found < second.Found);
        }

        private readonly struct Candidate
        {
            public Candidate(WorldActorClientEntry actor, float sqrDistance, int found)
            {
                Actor = actor;
                SqrDistance = sqrDistance;
                Found = found;
            }

            public WorldActorClientEntry Actor { get; }

            public float SqrDistance { get; }

            // The place among the waiting actors where it was found, which breaks a tie.
            public int Found { get; }
        }
    }
}
