#if NET6_0_OR_GREATER
using System;
using System.Security.Cryptography;

namespace Mmorpg.GameProtocol
{
    // The platform's ChaCha20-Poly1305 of System.Security.Cryptography, which the worker and the
    // tools use where IsSupported says the operating system offers it. Only the .NET build
    // compiles it; Unity never defines NET6_0_OR_GREATER.
    public sealed class RealtimePlatformChaCha20Poly1305 : IRealtimeAead
    {
        public const string Name = "platform";

        private readonly ChaCha20Poly1305 cipher;

        public RealtimePlatformChaCha20Poly1305(byte[] key)
        {
            if (key == null || key.Length != RealtimeAead.KeySize)
            {
                throw new ArgumentException($"The key must have {RealtimeAead.KeySize} bytes.", nameof(key));
            }

            cipher = new ChaCha20Poly1305(key);
        }

        public static bool IsSupported
        {
            get { return ChaCha20Poly1305.IsSupported; }
        }

        public string ImplementationName
        {
            get { return Name; }
        }

        public void Seal(
            ReadOnlySpan<byte> nonce,
            ReadOnlySpan<byte> plaintext,
            Span<byte> ciphertext,
            Span<byte> tag,
            ReadOnlySpan<byte> associatedData)
        {
            RealtimeAead.CheckArguments(nonce, plaintext.Length, ciphertext.Length, tag.Length);
            cipher.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }

        public bool TryOpen(
            ReadOnlySpan<byte> nonce,
            ReadOnlySpan<byte> ciphertext,
            ReadOnlySpan<byte> tag,
            Span<byte> plaintext,
            ReadOnlySpan<byte> associatedData)
        {
            RealtimeAead.CheckArguments(nonce, plaintext.Length, ciphertext.Length, tag.Length);
            try
            {
                cipher.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
                return true;
            }
            catch (CryptographicException)
            {
                // A tag that does not authenticate; the platform cleared the plaintext.
                return false;
            }
        }

        public void Dispose()
        {
            cipher.Dispose();
        }
    }
}
#endif
