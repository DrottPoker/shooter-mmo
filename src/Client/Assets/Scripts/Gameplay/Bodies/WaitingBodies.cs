using System.Collections.Generic;
using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The players in view whose body, or whose new look, waits to be put on, each with the look to
    // show, the content of the look and the combined mesh it asked for, and the order their bodies
    // go on in a frame: the
    // nearest to the eye first. A frame measures every waiting player once and heaps them by
    // distance, and each body taken costs a step down the heap, so a crowd that comes into view at
    // once is not scanned again for every body that goes on. A body whose mesh is not built yet
    // goes back to wait without its place in the heap, so a frame takes it once. Neither ordering
    // nor taking allocates; a sort of a list would, for its comparison.
    internal sealed class WaitingBodies
    {
        private readonly Dictionary<ulong, Waiting> looks = new Dictionary<ulong, Waiting>();
        private readonly List<ulong> keys = new List<ulong>();
        private readonly List<Candidate> heap = new List<Candidate>();
        private readonly List<ulong> viewless = new List<ulong>();

        public int Count
        {
            get { return looks.Count; }
        }

        public bool Contains(ulong entityId)
        {
            return looks.ContainsKey(entityId);
        }

        // The player waits for its body, which then shows this look.
        public void Set(ulong entityId, RealtimeCharacterAppearance look)
        {
            looks[entityId] = new Waiting(look, null, null);
        }

        // The player waits for the content and the mesh of this look, which it asked for.
        public void Wait(ulong entityId, RealtimeCharacterAppearance look, PlayerBodyContent.Needs content,
            PlayerBodyMeshes.Combined wanted)
        {
            looks[entityId] = new Waiting(look, content, wanted);
        }

        // Every waiting player still asks for the content and the mesh it asked for, also those that
        // a frame did not reach. Allocates nothing.
        public void Touch(PlayerBodyMeshes meshes, PlayerBodyContent content)
        {
            foreach (var waiting in looks.Values)
            {
                meshes.Touch(waiting.Wanted);
                if (content != null)
                {
                    content.Touch(waiting.Content);
                }
            }
        }

        // Every waiting player asks for its content and mesh again, as when the looks wear other
        // garments now.
        public void ForgetWanted()
        {
            keys.Clear();
            keys.AddRange(looks.Keys);
            foreach (var key in keys)
            {
                looks[key] = new Waiting(looks[key].Look, null, null);
            }
        }

        public void Remove(ulong entityId)
        {
            looks.Remove(entityId);
        }

        public void Clear()
        {
            looks.Clear();
            heap.Clear();
        }

        // Orders the waiting players by their distance from the eye, nearest first, and of equals
        // the first found; a waiting player without a view can never get a body and is forgotten.
        public void OrderFrom(Dictionary<ulong, RemotePlayerView> views, Vector3 eye)
        {
            heap.Clear();
            viewless.Clear();
            foreach (var pair in looks)
            {
                if (views.TryGetValue(pair.Key, out var view) && view != null)
                {
                    heap.Add(new Candidate(pair.Key, view, (view.transform.position - eye).sqrMagnitude, heap.Count));
                }
                else
                {
                    viewless.Add(pair.Key);
                }
            }

            for (var index = 0; index < viewless.Count; index++)
            {
                looks.Remove(viewless[index]);
            }

            for (var index = (heap.Count / 2) - 1; index >= 0; index--)
            {
                SiftDown(index);
            }
        }

        // The nearest player in the order whose body still waits, with its look and the content and
        // mesh it asked for; it waits no longer. False once the order is used up.
        public bool TryTakeNext(out RemotePlayerView view, out RealtimeCharacterAppearance look,
            out PlayerBodyContent.Needs content, out PlayerBodyMeshes.Combined wanted)
        {
            while (heap.Count > 0)
            {
                var nearest = heap[0];
                var last = heap.Count - 1;
                heap[0] = heap[last];
                heap.RemoveAt(last);
                SiftDown(0);
                if (looks.TryGetValue(nearest.EntityId, out var waiting))
                {
                    looks.Remove(nearest.EntityId);
                    view = nearest.View;
                    look = waiting.Look;
                    content = waiting.Content;
                    wanted = waiting.Wanted;
                    return true;
                }
            }

            view = null;
            look = null;
            content = null;
            wanted = null;
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

        private readonly struct Waiting
        {
            public Waiting(RealtimeCharacterAppearance look, PlayerBodyContent.Needs content, PlayerBodyMeshes.Combined wanted)
            {
                Look = look;
                Content = content;
                Wanted = wanted;
            }

            public RealtimeCharacterAppearance Look { get; }

            public PlayerBodyContent.Needs Content { get; }

            public PlayerBodyMeshes.Combined Wanted { get; }
        }

        private readonly struct Candidate
        {
            public Candidate(ulong entityId, RemotePlayerView view, float sqrDistance, int found)
            {
                EntityId = entityId;
                View = view;
                SqrDistance = sqrDistance;
                Found = found;
            }

            public ulong EntityId { get; }

            public RemotePlayerView View { get; }

            public float SqrDistance { get; }

            // The place among the waiting players where it was found, which breaks a tie.
            public int Found { get; }
        }
    }
}
