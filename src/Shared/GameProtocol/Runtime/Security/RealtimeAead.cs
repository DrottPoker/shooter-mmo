using System;

namespace Mmorpg.GameProtocol
{
    // Creates the ChaCha20-Poly1305 implementation for a key: the platform's where the runtime has
    // a working one, and the managed one otherwise. Only the .NET build can see the platform's
    // type, so a Unity player always uses the managed one.
    public static class RealtimeAead
    {
        public const int KeySize = 32;
        public const int NonceSize = 12;
        public const int TagSize = 16;

        // Whether this runtime offers the platform implementation.
        public static bool IsPlatformSupported
        {
            get
            {
#if NET6_0_OR_GREATER
                return RealtimePlatformChaCha20Poly1305.IsSupported;
#else
                return false;
#endif
            }
        }

        // The name of the implementation that Create picks on this runtime.
        public static string DefaultImplementationName
        {
            get
            {
#if NET6_0_OR_GREATER
                return IsPlatformSupported ? RealtimePlatformChaCha20Poly1305.Name : RealtimeManagedChaCha20Poly1305.Name;
#else
                return RealtimeManagedChaCha20Poly1305.Name;
#endif
            }
        }

        public static IRealtimeAead Create(byte[] key)
        {
#if NET6_0_OR_GREATER
            if (IsPlatformSupported)
            {
                return new RealtimePlatformChaCha20Poly1305(key);
            }
#endif
            return new RealtimeManagedChaCha20Poly1305(key);
        }

        internal static void CheckArguments(ReadOnlySpan<byte> nonce, int plaintextLength, int ciphertextLength, int tagLength)
        {
            if (nonce.Length != NonceSize)
            {
                throw new ArgumentException($"The nonce must have {NonceSize} bytes.", nameof(nonce));
            }

            if (plaintextLength != ciphertextLength)
            {
                throw new ArgumentException("The plaintext and the ciphertext must have the same length.");
            }

            if (tagLength != TagSize)
            {
                throw new ArgumentException($"The tag must have {TagSize} bytes.", "tag");
            }
        }
    }
}
