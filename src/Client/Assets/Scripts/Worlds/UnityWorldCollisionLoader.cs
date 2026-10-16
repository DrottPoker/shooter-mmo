using System;
using System.Collections.Generic;
using System.IO;
using Mmorpg.Client.Core;
using Mmorpg.GameSimulation;
using Mmorpg.WorldData.Content;
using UnityEngine;

namespace Mmorpg.Client.Worlds
{
    public static class UnityWorldCollisionLoader
    {
        private const int DefaultLoadRadiusChunks = 2;
        private const int DefaultUnloadRadiusChunks = 3;

        public static bool TryLoad(
            string worldId,
            out ChunkedStaticCollisionWorld collisionWorld,
            out string error)
        {
            collisionWorld = null;
            if (!TryCreateStream(worldId, out var stream, out error)
                || !stream.TryLoadAll(out error))
            {
                return false;
            }

            collisionWorld = stream.CollisionWorld;
            return true;
        }

        public static bool TryCreateStream(
            string worldId,
            out UnityWorldCollisionStream collisionStream,
            out string error)
        {
            collisionStream = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(worldId))
            {
                error = "World id is required to load collision data.";
                return false;
            }

            var addressDirectory = ClientAddresses.WorldCollisionPrefix + worldId.Trim() + "/";
            if (!ClientAssets.TryReadText(
                    addressDirectory + ContentLayout.CollisionManifestFileName,
                    out var manifestJson,
                    out var readError))
            {
                error = "Collision manifest is missing for world '" + worldId + "'. " + readError;
                return false;
            }

            try
            {
                var manifest = JsonUtility.FromJson<CollisionWorldManifest>(manifestJson);
                ValidateManifest(manifest, worldId);
                collisionStream = new UnityWorldCollisionStream(
                    addressDirectory,
                    manifest,
                    DefaultLoadRadiusChunks,
                    DefaultUnloadRadiusChunks);
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
                || !CollisionDataFormat.IsValidChunkSize(manifest.ChunkSize)
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

            if (!string.Equals(
                    CollisionWorldCompiler.ComputeManifestRevision(manifest),
                    manifest.Revision,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Collision manifest revision does not match its chunk content.");
            }
        }
    }
}
