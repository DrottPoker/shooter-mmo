using System;
using System.Collections.Generic;
using System.IO;
using ShooterMmo.GameSimulation;
using UnityEngine;

namespace ShooterMmo.Collision
{
    public static class UnityWorldCollisionLoader
    {
        private const string ResourceRoot = "ShooterMmo/WorldCollision/";

        public static bool TryLoad(
            string worldId,
            out ChunkedStaticCollisionWorld collisionWorld,
            out string error)
        {
            collisionWorld = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(worldId))
            {
                error = "World id is required to load collision data.";
                return false;
            }

            var resourceDirectory = ResourceRoot + worldId.Trim() + "/";
            var manifestAsset = Resources.Load<TextAsset>(resourceDirectory + "manifest");
            if (manifestAsset == null)
            {
                error = "Collision manifest is missing for world '" + worldId + "'.";
                return false;
            }

            try
            {
                var manifest = JsonUtility.FromJson<CollisionWorldManifest>(manifestAsset.text);
                ValidateManifest(manifest, worldId);
                var chunks = new List<CollisionChunk>(manifest.Chunks.Length);
                for (var index = 0; index < manifest.Chunks.Length; index++)
                {
                    var entry = manifest.Chunks[index];
                    var chunkAsset = Resources.Load<TextAsset>(resourceDirectory + entry.ResourceName);
                    if (chunkAsset == null)
                    {
                        throw new InvalidDataException(
                            "Collision chunk is missing: " + entry.ResourceName);
                    }

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

                    chunks.Add(chunk);
                }

                collisionWorld = new ChunkedStaticCollisionWorld(
                    manifest.WorldId,
                    manifest.Revision,
                    manifest.ChunkSize,
                    chunks);
                return true;
            }
            catch (Exception exception)
            {
                error = "Collision data is invalid for world '" + worldId + "': "
                    + exception.Message;
                return false;
            }
        }

        private static void ValidateManifest(CollisionWorldManifest manifest, string expectedWorldId)
        {
            if (manifest == null
                || manifest.FormatVersion != CollisionDataFormat.Version
                || !string.Equals(
                    manifest.WorldId,
                    expectedWorldId,
                    StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(manifest.Revision)
                || float.IsNaN(manifest.ChunkSize)
                || float.IsInfinity(manifest.ChunkSize)
                || manifest.ChunkSize <= 0f
                || manifest.Chunks == null
                || manifest.Chunks.Length == 0)
            {
                throw new InvalidDataException("Collision manifest is invalid.");
            }

            var coordinates = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < manifest.Chunks.Length; index++)
            {
                var chunk = manifest.Chunks[index];
                if (chunk == null
                    || string.IsNullOrWhiteSpace(chunk.ResourceName)
                    || string.IsNullOrWhiteSpace(chunk.Sha256)
                    || !coordinates.Add(chunk.X + ":" + chunk.Z))
                {
                    throw new InvalidDataException("Collision manifest contains an invalid chunk.");
                }
            }
        }
    }
}
