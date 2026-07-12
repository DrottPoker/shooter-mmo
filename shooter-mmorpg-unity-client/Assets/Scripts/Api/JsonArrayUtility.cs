using System;
using UnityEngine;

namespace ShooterMmo.Api
{
    public static class JsonArrayUtility
    {
        public static T[] FromJson<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<T>();
            }

            var wrapper = JsonUtility.FromJson<ArrayWrapper<T>>("{\"items\":" + json + "}");
            return wrapper != null && wrapper.items != null ? wrapper.items : Array.Empty<T>();
        }

        public static bool TryFromJson<T>(string json, out T[] result)
        {
            result = Array.Empty<T>();
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            var trimmed = json.Trim();
            if (!trimmed.StartsWith("[", StringComparison.Ordinal)
                || !trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                var wrapper = JsonUtility.FromJson<ArrayWrapper<T>>("{\"items\":" + trimmed + "}");
                if (wrapper == null || wrapper.items == null)
                {
                    return false;
                }

                result = wrapper.items;
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        [Serializable]
        private sealed class ArrayWrapper<T>
        {
            public T[] items;
        }
    }
}
