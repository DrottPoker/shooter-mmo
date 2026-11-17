using System;
using System.Security.Cryptography;

namespace Mmorpg.GameProtocol
{
    // HKDF with HMAC-SHA256 as RFC 5869 defines it. .NET Standard 2.1 has HMACSHA256 but no HKDF.
    public static class RealtimeHkdf
    {
        public const int HashSize = 32;

        // Extract, then Expand. A missing salt is a string of 32 zero bytes, as RFC 5869 says.
        public static byte[] DeriveKey(byte[] inputKeyMaterial, int length, byte[] salt, byte[] info)
        {
            var pseudoRandomKey = Extract(salt, inputKeyMaterial);
            try
            {
                return Expand(pseudoRandomKey, length, info);
            }
            finally
            {
                Array.Clear(pseudoRandomKey, 0, pseudoRandomKey.Length);
            }
        }

        public static byte[] Extract(byte[] salt, byte[] inputKeyMaterial)
        {
            if (inputKeyMaterial == null)
            {
                throw new ArgumentNullException(nameof(inputKeyMaterial));
            }

            using (var hmac = new HMACSHA256(salt == null || salt.Length == 0 ? new byte[HashSize] : salt))
            {
                return hmac.ComputeHash(inputKeyMaterial);
            }
        }

        public static byte[] Expand(byte[] pseudoRandomKey, int length, byte[] info)
        {
            if (pseudoRandomKey == null || pseudoRandomKey.Length < HashSize)
            {
                throw new ArgumentException($"The pseudorandom key must have at least {HashSize} bytes.", nameof(pseudoRandomKey));
            }

            if (length < 1 || length > 255 * HashSize)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            info = info ?? Array.Empty<byte>();
            var output = new byte[length];
            var previous = Array.Empty<byte>();
            var input = new byte[HashSize + info.Length + 1];
            using (var hmac = new HMACSHA256(pseudoRandomKey))
            {
                var written = 0;
                for (var counter = 1; written < length; counter++)
                {
                    // T(n) = HMAC(PRK, T(n - 1) | info | n)
                    Buffer.BlockCopy(previous, 0, input, 0, previous.Length);
                    Buffer.BlockCopy(info, 0, input, previous.Length, info.Length);
                    input[previous.Length + info.Length] = (byte)counter;
                    var block = hmac.ComputeHash(input, 0, previous.Length + info.Length + 1);
                    var count = Math.Min(block.Length, length - written);
                    Buffer.BlockCopy(block, 0, output, written, count);
                    written += count;
                    Array.Clear(previous, 0, previous.Length);
                    previous = block;
                }
            }

            Array.Clear(previous, 0, previous.Length);
            Array.Clear(input, 0, input.Length);
            return output;
        }
    }
}
