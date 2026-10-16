using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Mmorpg.WorldData.Editor.ContentTools
{
    // An exact copy of editable content, used for undo. DataContract JSON keeps null distinct
    // from empty, which JsonUtility does not, and writes floats with round-trip precision.
    public static class ContentSnapshot
    {
        public static string Capture<T>(T value)
            where T : class
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static T Restore<T>(string snapshot)
            where T : class
        {
            if (string.IsNullOrEmpty(snapshot))
            {
                throw new ArgumentException("A snapshot is required.", nameof(snapshot));
            }

            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(snapshot)))
            {
                return (T)serializer.ReadObject(stream)
                    ?? throw new InvalidDataException("The snapshot has no root object.");
            }
        }
    }
}
