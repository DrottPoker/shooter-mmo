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

        [Serializable]
        private sealed class ArrayWrapper<T>
        {
            public T[] items;
        }
    }
}

