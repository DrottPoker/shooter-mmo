using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.GameSimulation;

namespace Mmorpg.Client.Worlds
{
    public sealed class UnityWorldCollisionStream
    {
        private readonly string addressDirectory;
        private readonly Dictionary<CollisionChunkCoordinate, CollisionChunkManifestEntry> entries;
        private readonly HashSet<CollisionChunkCoordinate> loadedCoordinates =
            new HashSet<CollisionChunkCoordinate>();
        private readonly CollisionChunkStreamingPlanner planner;

        // The chunks of a refresh, kept so a refresh allocates nothing.
        private readonly List<CollisionChunkCoordinate> anchorChunks = new List<CollisionChunkCoordinate>();
        private readonly HashSet<CollisionChunkCoordinate> required = new HashSet<CollisionChunkCoordinate>();
        private readonly HashSet<CollisionChunkCoordinate> retained = new HashSet<CollisionChunkCoordinate>();
        private readonly List<CollisionChunkCoordinate> unloading = new List<CollisionChunkCoordinate>();

        public UnityWorldCollisionStream(
            string addressDirectory,
            CollisionWorldManifest manifest,
            int loadRadiusChunks,
            int unloadRadiusChunks)
        {
            this.addressDirectory = addressDirectory;
            entries = manifest.Chunks.ToDictionary(
                entry => new CollisionChunkCoordinate(entry.X, entry.Z));
            planner = new CollisionChunkStreamingPlanner(
                manifest.ChunkSize,
                loadRadiusChunks,
                unloadRadiusChunks);
            CollisionWorld = new ChunkedStaticCollisionWorld(
                manifest.WorldId,
                manifest.Revision,
                manifest.ChunkSize);
        }

        public ChunkedStaticCollisionWorld CollisionWorld { get; private set; }

        // How many times a chunk loaded or unloaded, so presentation that rests on the ground,
        // such as a standing remote player, can tell that the ground under it may have changed.
        public int Revision { get; private set; }

        // Loads the chunks within the load radius of the anchors and unloads those beyond the
        // unload radius. It runs on every owner snapshot, so it keeps its sets and lists and
        // allocates nothing unless a chunk loads.
        public bool TryRefresh(IReadOnlyList<SimulationVector3> anchors, out string error)
        {
            error = string.Empty;
            try
            {
                FillAround(anchors);
                foreach (var coordinate in required)
                {
                    LoadChunk(coordinate);
                }

                unloading.Clear();
                foreach (var coordinate in loadedCoordinates)
                {
                    if (!retained.Contains(coordinate))
                    {
                        unloading.Add(coordinate);
                    }
                }

                for (var index = 0; index < unloading.Count; index++)
                {
                    var coordinate = unloading[index];
                    CollisionWorld.UnloadChunk(coordinate.X, coordinate.Z);
                    loadedCoordinates.Remove(coordinate);
                    Revision++;
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "Collision chunk streaming failed: " + exception.Message;
                return false;
            }
        }

        // Loads the chunks within the load radius of the anchors and unloads nothing. It runs
        // before every predicted tick, so like a refresh it allocates nothing unless a chunk
        // loads.
        public bool TryEnsureLoaded(IReadOnlyList<SimulationVector3> anchors, out string error)
        {
            error = string.Empty;
            try
            {
                FillAround(anchors);
                foreach (var coordinate in required)
                {
                    LoadChunk(coordinate);
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "Collision chunk streaming failed: " + exception.Message;
                return false;
            }
        }

        // Fills the kept sets with the chunks within the load and the unload radius of the
        // anchors, each set in the order the anchors and their chunks come; an anchor that is not
        // finite throws before any chunk loads.
        private void FillAround(IReadOnlyList<SimulationVector3> anchors)
        {
            if (anchors == null)
            {
                throw new ArgumentNullException(nameof(anchors));
            }

            anchorChunks.Clear();
            for (var index = 0; index < anchors.Count; index++)
            {
                if (!SimulationVector3.IsFinite(anchors[index]))
                {
                    throw new ArgumentException("Collision streaming anchors must be finite.", nameof(anchors));
                }

                anchorChunks.Add(planner.ToChunk(anchors[index]));
            }

            planner.FillChunks(anchorChunks, required, retained);
        }

        public bool TryLoadAll(out string error)
        {
            error = string.Empty;
            try
            {
                foreach (var coordinate in entries.Keys)
                {
                    LoadChunk(coordinate);
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "Collision data is invalid: " + exception.Message;
                return false;
            }
        }

        private void LoadChunk(CollisionChunkCoordinate coordinate)
        {
            if (loadedCoordinates.Contains(coordinate)
                || !entries.TryGetValue(coordinate, out var entry))
            {
                return;
            }

            if (!ClientAssets.TryReadBytes(
                    addressDirectory + entry.ResourceName + CollisionDataFormat.ChunkFileExtension,
                    out var bytes,
                    out var readError))
            {
                throw new InvalidDataException(
                    "Collision chunk is missing: " + entry.ResourceName + ". " + readError);
            }

            var hash = CollisionWorldCompiler.ComputeSha256Hex(bytes);
            if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Collision chunk checksum does not match: " + entry.ResourceName);
            }

            var chunk = CollisionChunkCodec.Decode(bytes);
            if (chunk.X != entry.X || chunk.Z != entry.Z)
            {
                throw new InvalidDataException(
                    "Collision chunk coordinates do not match the manifest: "
                    + entry.ResourceName);
            }

            if (!CollisionWorld.TryLoadChunk(chunk))
            {
                throw new InvalidDataException(
                    "Collision chunk was already loaded unexpectedly: " + entry.ResourceName);
            }

            loadedCoordinates.Add(coordinate);
            Revision++;
        }
    }
}
