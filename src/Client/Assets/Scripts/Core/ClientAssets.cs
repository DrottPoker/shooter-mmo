using System;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace Mmorpg.Client.Core
{
    // Synchronous Addressables access for small content the client needs at once:
    // configuration, catalogs, icons, and collision chunks. Scenes and prefabs load
    // asynchronously where they are used. A missing address is reported as an error value
    // instead of an Addressables exception, so callers keep their structured failure paths.
    public static class ClientAssets
    {
        public static bool Exists(string address, Type type)
        {
            return TryLocate(address, type, out _, out _);
        }

        // The caller owns the handle and releases it with Release once the asset is unused.
        public static bool TryLoadNow<T>(
            string address,
            out AsyncOperationHandle<T> handle,
            out string error)
            where T : UnityEngine.Object
        {
            handle = default;
            if (!TryLocate(address, typeof(T), out var location, out error))
            {
                return false;
            }

            handle = Addressables.LoadAssetAsync<T>(location);
            handle.WaitForCompletion();
            if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null)
            {
                return true;
            }

            error = "Addressables asset '" + address + "' could not be loaded"
                + (handle.OperationException != null
                    ? ": " + handle.OperationException.Message
                    : ".");
            Release(handle);
            handle = default;
            return false;
        }

        // Starts an asynchronous load. The caller owns the handle and releases it with Release.
        public static bool TryLoadAsync<T>(
            string address,
            out AsyncOperationHandle<T> handle,
            out string error)
            where T : UnityEngine.Object
        {
            handle = default;
            if (!TryLocate(address, typeof(T), out var location, out error))
            {
                return false;
            }

            handle = Addressables.LoadAssetAsync<T>(location);
            return true;
        }

        public static bool TryReadText(string address, out string text, out string error)
        {
            text = null;
            if (!TryLoadNow<TextAsset>(address, out var handle, out error))
            {
                return false;
            }

            text = handle.Result.text;
            Release(handle);
            return true;
        }

        public static bool TryReadBytes(string address, out byte[] bytes, out string error)
        {
            bytes = null;
            if (!TryLoadNow<TextAsset>(address, out var handle, out error))
            {
                return false;
            }

            bytes = handle.Result.bytes;
            Release(handle);
            return true;
        }

        public static void Release<T>(AsyncOperationHandle<T> handle)
        {
            if (handle.IsValid())
            {
                Addressables.Release(handle);
            }
        }

        private static bool TryLocate(
            string address,
            Type type,
            out IResourceLocation location,
            out string error)
        {
            location = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(address))
            {
                error = "An Addressables address is required.";
                return false;
            }

            Addressables.InitializeAsync().WaitForCompletion();
            foreach (var locator in Addressables.ResourceLocators)
            {
                if (locator.Locate(address, type, out var locations) && locations.Count > 0)
                {
                    location = locations[0];
                    return true;
                }
            }

            error = "No Addressables entry of type " + type.Name + " has the address '"
                + address + "'.";
            return false;
        }
    }
}
