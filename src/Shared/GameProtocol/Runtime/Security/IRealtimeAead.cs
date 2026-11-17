using System;

namespace Mmorpg.GameProtocol
{
    // ChaCha20-Poly1305 as RFC 8439 defines it, with one key. RealtimeAead creates the platform's
    // implementation where the runtime has one, and the managed one otherwise; both produce the
    // same bytes. An instance is not thread-safe, so its owner serializes the calls.
    public interface IRealtimeAead : IDisposable
    {
        // The name of the implementation, for the worker's start log and the benchmark.
        string ImplementationName { get; }

        // Encrypts the plaintext into the ciphertext, which has its length and may be the same
        // span, and writes the 16-byte tag over the associated data and the ciphertext.
        void Seal(
            ReadOnlySpan<byte> nonce,
            ReadOnlySpan<byte> plaintext,
            Span<byte> ciphertext,
            Span<byte> tag,
            ReadOnlySpan<byte> associatedData);

        // Checks the tag in constant time and decrypts the ciphertext into the plaintext, which
        // has its length and may be the same span. False when the tag does not authenticate; the
        // plaintext then holds nothing usable.
        bool TryOpen(
            ReadOnlySpan<byte> nonce,
            ReadOnlySpan<byte> ciphertext,
            ReadOnlySpan<byte> tag,
            Span<byte> plaintext,
            ReadOnlySpan<byte> associatedData);
    }
}
