using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShooterMmo.GameSimulation;
using UnityEngine;

namespace ShooterMmo.Collision
{
    public sealed class UnityWorldCollisionStream
    {
        private readonly string resourceDirectory;
        private readonly Dictionary<CollisionChunkCoordinate, CollisionChunkManifestEntry> entries;
        private readonly HashSet<CollisionChunkCoordinate> loadedCoordinates =
            new HashSet<CollisionChunkCoordinate>();
        private readonly CollisionChunkStreamingPlanner planner;

        public UnityWorldCollisionStream(
            string resourceDirectory,
            CollisionWorldManifest manifest,
            int loadRadiusChunks,
            int unloadRadiusChunks)
        {
            this.resourceDirectory = resourceDirectory;
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

        public bool TryRefresh(IEnumerable<SimulationVector3> anchors, out string error)
        {
            error = string.Empty;
            try
            {
                var anchorSnapshot = anchors.ToArray();
                var required = planner.GetRequiredChunks(anchorSnapshot);
                var retained = planner.GetRetainedChunks(anchorSnapshot);
                foreach (var coordinate in required)
                {
                    LoadChunk(coordinate);
                }

                foreach (var coordinate in loadedCoordinates.ToArray())
                {
                    if (!retained.Contains(coordinate))
                    {
                        CollisionWorld.UnloadChunk(coordinate.X, coordinate.Z);
                        loadedCoordinates.Remove(coordinate);
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "Collision chunk streaming failed: " + exception.Message;
                return false;
            }
        }

        public bool TryEnsureLoaded(IEnumerable<SimulationVector3> anchors, out string error)
        {
            error = string.Empty;
            try
            {
                foreach (var coordinate in planner.GetRequiredChunks(anchors))
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

            var chunkAsset = Resources.Load<TextAsset>(resourceDirectory + entry.ResourceName);
            if (chunkAsset == null)
            {
                throw new InvalidDataException("Collision chunk is missing: " + entry.ResourceName);
            }

            try
            {
                var bytes = chunkAsset.bytes;
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
            }
            finally
            {
                Resources.UnloadAsset(chunkAsset);
            }
        }
    }
}
