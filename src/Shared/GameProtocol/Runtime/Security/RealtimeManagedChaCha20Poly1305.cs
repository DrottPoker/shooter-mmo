using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Mmorpg.GameProtocol
{
    // ChaCha20-Poly1305 written from RFC 8439 in managed code, for a runtime whose platform has
    // no implementation, such as the Unity client. ChaCha20 runs 20 rounds over a 32-bit block
    // counter and a 96-bit nonce; Poly1305 uses five 26-bit limbs. The tag is compared in
    // constant time, and every key-dependent step runs in the same time for every key and input.
    public sealed class RealtimeManagedChaCha20Poly1305 : IRealtimeAead
    {
        public const string Name = "managed";

        private const int BlockSize = 64;
        private const uint Sigma0 = 0x61707865;
        private const uint Sigma1 = 0x3320646e;
        private const uint Sigma2 = 0x79622d32;
        private const uint Sigma3 = 0x6b206574;

        private readonly uint[] key = new uint[8];
        private readonly byte[] block = new byte[BlockSize];
        private bool disposed;

        public RealtimeManagedChaCha20Poly1305(byte[] key)
        {
            if (key == null || key.Length != RealtimeAead.KeySize)
            {
                throw new ArgumentException($"The key must have {RealtimeAead.KeySize} bytes.", nameof(key));
            }

            for (var index = 0; index < this.key.Length; index++)
            {
                this.key[index] = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(key, index * 4, 4));
            }
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
            ThrowIfDisposed();
            RealtimeAead.CheckArguments(nonce, plaintext.Length, ciphertext.Length, tag.Length);
            ReadNonce(nonce, out var nonce0, out var nonce1, out var nonce2);
            Span<byte> polyKey = stackalloc byte[32];
            CreatePolyKey(nonce0, nonce1, nonce2, polyKey);
            Xor(nonce0, nonce1, nonce2, plaintext, ciphertext);
            ComputeTag(polyKey, associatedData, ciphertext, tag);
            polyKey.Clear();
        }

        public bool TryOpen(
            ReadOnlySpan<byte> nonce,
            ReadOnlySpan<byte> ciphertext,
            ReadOnlySpan<byte> tag,
            Span<byte> plaintext,
            ReadOnlySpan<byte> associatedData)
        {
            ThrowIfDisposed();
            RealtimeAead.CheckArguments(nonce, plaintext.Length, ciphertext.Length, tag.Length);
            ReadNonce(nonce, out var nonce0, out var nonce1, out var nonce2);
            Span<byte> polyKey = stackalloc byte[32];
            Span<byte> expectedTag = stackalloc byte[RealtimeAead.TagSize];
            CreatePolyKey(nonce0, nonce1, nonce2, polyKey);

            // The tag covers the ciphertext, so it is checked before an in-place decryption.
            ComputeTag(polyKey, associatedData, ciphertext, expectedTag);
            polyKey.Clear();
            if (!RealtimeConstantTime.AreEqual(expectedTag, tag))
            {
                plaintext.Clear();
                return false;
            }

            Xor(nonce0, nonce1, nonce2, ciphertext, plaintext);
            return true;
        }

        public void Dispose()
        {
            Array.Clear(key, 0, key.Length);
            Array.Clear(block, 0, block.Length);
            disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(RealtimeManagedChaCha20Poly1305));
            }
        }

        private static void ReadNonce(ReadOnlySpan<byte> nonce, out uint nonce0, out uint nonce1, out uint nonce2)
        {
            nonce0 = BinaryPrimitives.ReadUInt32LittleEndian(nonce);
            nonce1 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4));
            nonce2 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(8));
        }

        // The one-time Poly1305 key is the first half of the ChaCha20 block with counter 0.
        private void CreatePolyKey(uint nonce0, uint nonce1, uint nonce2, Span<byte> polyKey)
        {
            ComputeBlock(0, nonce0, nonce1, nonce2, block);
            new ReadOnlySpan<byte>(block, 0, 32).CopyTo(polyKey);
            Array.Clear(block, 0, block.Length);
        }

        // XORs the key stream from block counter 1 on into the output. Input and output may be the
        // same span, because every byte is read before it is written.
        private void Xor(uint nonce0, uint nonce1, uint nonce2, ReadOnlySpan<byte> input, Span<byte> output)
        {
            var counter = 1u;
            for (var offset = 0; offset < input.Length; offset += BlockSize)
            {
                ComputeBlock(counter, nonce0, nonce1, nonce2, block);
                counter++;
                var count = Math.Min(BlockSize, input.Length - offset);
                for (var index = 0; index < count; index++)
                {
                    output[offset + index] = (byte)(input[offset + index] ^ block[index]);
                }
            }

            Array.Clear(block, 0, block.Length);
        }

        // The ChaCha20 block function of RFC 8439 section 2.3: 20 rounds, then the input added.
        private void ComputeBlock(uint counter, uint nonce0, uint nonce1, uint nonce2, byte[] output)
        {
            uint x0 = Sigma0, x1 = Sigma1, x2 = Sigma2, x3 = Sigma3;
            uint x4 = key[0], x5 = key[1], x6 = key[2], x7 = key[3];
            uint x8 = key[4], x9 = key[5], x10 = key[6], x11 = key[7];
            uint x12 = counter, x13 = nonce0, x14 = nonce1, x15 = nonce2;

            for (var round = 0; round < 10; round++)
            {
                QuarterRound(ref x0, ref x4, ref x8, ref x12);
                QuarterRound(ref x1, ref x5, ref x9, ref x13);
                QuarterRound(ref x2, ref x6, ref x10, ref x14);
                QuarterRound(ref x3, ref x7, ref x11, ref x15);
                QuarterRound(ref x0, ref x5, ref x10, ref x15);
                QuarterRound(ref x1, ref x6, ref x11, ref x12);
                QuarterRound(ref x2, ref x7, ref x8, ref x13);
                QuarterRound(ref x3, ref x4, ref x9, ref x14);
            }

            var span = new Span<byte>(output);
            BinaryPrimitives.WriteUInt32LittleEndian(span, x0 + Sigma0);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), x1 + Sigma1);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), x2 + Sigma2);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), x3 + Sigma3);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16), x4 + key[0]);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20), x5 + key[1]);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24), x6 + key[2]);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(28), x7 + key[3]);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(32), x8 + key[4]);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(36), x9 + key[5]);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(40), x10 + key[6]);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(44), x11 + key[7]);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(48), x12 + counter);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(52), x13 + nonce0);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(56), x14 + nonce1);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(60), x15 + nonce2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void QuarterRound(ref uint a, ref uint b, ref uint c, ref uint d)
        {
            a += b;
            d = RotateLeft(d ^ a, 16);
            c += d;
            b = RotateLeft(b ^ c, 12);
            a += b;
            d = RotateLeft(d ^ a, 8);
            c += d;
            b = RotateLeft(b ^ c, 7);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint RotateLeft(uint value, int count)
        {
            return (value << count) | (value >> (32 - count));
        }

        // The tag of RFC 8439 section 2.8: Poly1305 over the associated data and the ciphertext,
        // each padded with zeros to 16 bytes, and their lengths as 64-bit little-endian numbers.
        private static void ComputeTag(
            ReadOnlySpan<byte> polyKey,
            ReadOnlySpan<byte> associatedData,
            ReadOnlySpan<byte> ciphertext,
            Span<byte> tag)
        {
            var poly = new Poly1305(polyKey);
            poly.UpdatePadded(associatedData);
            poly.UpdatePadded(ciphertext);
            Span<byte> lengths = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(lengths, (ulong)associatedData.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(lengths.Slice(8), (ulong)ciphertext.Length);
            poly.UpdatePadded(lengths);
            poly.Finish(tag);
        }

        // Poly1305 of RFC 8439 section 2.5 over messages that the AEAD pads to whole 16-byte
        // blocks, so every block carries the high bit.
        private struct Poly1305
        {
            private const uint HighBit = 1u << 24;

            private readonly uint r0;
            private readonly uint r1;
            private readonly uint r2;
            private readonly uint r3;
            private readonly uint r4;
            private readonly uint s1;
            private readonly uint s2;
            private readonly uint s3;
            private readonly uint s4;
            private readonly uint pad0;
            private readonly uint pad1;
            private readonly uint pad2;
            private readonly uint pad3;
            private uint h0;
            private uint h1;
            private uint h2;
            private uint h3;
            private uint h4;

            public Poly1305(ReadOnlySpan<byte> key)
            {
                var t0 = BinaryPrimitives.ReadUInt32LittleEndian(key);
                var t1 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(4));
                var t2 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(8));
                var t3 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(12));

                // r with the bits that RFC 8439 clamps cleared.
                r0 = t0 & 0x3ffffff;
                r1 = ((t0 >> 26) | (t1 << 6)) & 0x3ffff03;
                r2 = ((t1 >> 20) | (t2 << 12)) & 0x3ffc0ff;
                r3 = ((t2 >> 14) | (t3 << 18)) & 0x3f03fff;
                r4 = (t3 >> 8) & 0x00fffff;
                s1 = r1 * 5;
                s2 = r2 * 5;
                s3 = r3 * 5;
                s4 = r4 * 5;
                pad0 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(16));
                pad1 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(20));
                pad2 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(24));
                pad3 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(28));
                h0 = 0;
                h1 = 0;
                h2 = 0;
                h3 = 0;
                h4 = 0;
            }

            // Processes the data as whole blocks, the last one padded with zeros.
            public void UpdatePadded(ReadOnlySpan<byte> data)
            {
                var offset = 0;
                for (; offset + 16 <= data.Length; offset += 16)
                {
                    ProcessBlock(data.Slice(offset, 16));
                }

                if (offset < data.Length)
                {
                    Span<byte> last = stackalloc byte[16];
                    data.Slice(offset).CopyTo(last);
                    ProcessBlock(last);
                }
            }

            private void ProcessBlock(ReadOnlySpan<byte> blockBytes)
            {
                var t0 = BinaryPrimitives.ReadUInt32LittleEndian(blockBytes);
                var t1 = BinaryPrimitives.ReadUInt32LittleEndian(blockBytes.Slice(4));
                var t2 = BinaryPrimitives.ReadUInt32LittleEndian(blockBytes.Slice(8));
                var t3 = BinaryPrimitives.ReadUInt32LittleEndian(blockBytes.Slice(12));

                h0 += t0 & 0x3ffffff;
                h1 += ((t0 >> 26) | (t1 << 6)) & 0x3ffffff;
                h2 += ((t1 >> 20) | (t2 << 12)) & 0x3ffffff;
                h3 += ((t2 >> 14) | (t3 << 18)) & 0x3ffffff;
                h4 += (t3 >> 8) | HighBit;

                var d0 = ((ulong)h0 * r0) + ((ulong)h1 * s4) + ((ulong)h2 * s3) + ((ulong)h3 * s2) + ((ulong)h4 * s1);
                var d1 = ((ulong)h0 * r1) + ((ulong)h1 * r0) + ((ulong)h2 * s4) + ((ulong)h3 * s3) + ((ulong)h4 * s2);
                var d2 = ((ulong)h0 * r2) + ((ulong)h1 * r1) + ((ulong)h2 * r0) + ((ulong)h3 * s4) + ((ulong)h4 * s3);
                var d3 = ((ulong)h0 * r3) + ((ulong)h1 * r2) + ((ulong)h2 * r1) + ((ulong)h3 * r0) + ((ulong)h4 * s4);
                var d4 = ((ulong)h0 * r4) + ((ulong)h1 * r3) + ((ulong)h2 * r2) + ((ulong)h3 * r1) + ((ulong)h4 * r0);

                var carry = (uint)(d0 >> 26);
                h0 = (uint)d0 & 0x3ffffff;
                d1 += carry;
                carry = (uint)(d1 >> 26);
                h1 = (uint)d1 & 0x3ffffff;
                d2 += carry;
                carry = (uint)(d2 >> 26);
                h2 = (uint)d2 & 0x3ffffff;
                d3 += carry;
                carry = (uint)(d3 >> 26);
                h3 = (uint)d3 & 0x3ffffff;
                d4 += carry;
                carry = (uint)(d4 >> 26);
                h4 = (uint)d4 & 0x3ffffff;
                h0 += carry * 5;
                carry = h0 >> 26;
                h0 &= 0x3ffffff;
                h1 += carry;
            }

            // Reduces the accumulator modulo 2^130 - 5 and adds the pad modulo 2^128.
            public void Finish(Span<byte> tag)
            {
                var carry = h1 >> 26;
                h1 &= 0x3ffffff;
                h2 += carry;
                carry = h2 >> 26;
                h2 &= 0x3ffffff;
                h3 += carry;
                carry = h3 >> 26;
                h3 &= 0x3ffffff;
                h4 += carry;
                carry = h4 >> 26;
                h4 &= 0x3ffffff;
                h0 += carry * 5;
                carry = h0 >> 26;
                h0 &= 0x3ffffff;
                h1 += carry;

                // g = h - p; take it when h >= p, chosen by a mask instead of a branch.
                var g0 = h0 + 5;
                carry = g0 >> 26;
                g0 &= 0x3ffffff;
                var g1 = h1 + carry;
                carry = g1 >> 26;
                g1 &= 0x3ffffff;
                var g2 = h2 + carry;
                carry = g2 >> 26;
                g2 &= 0x3ffffff;
                var g3 = h3 + carry;
                carry = g3 >> 26;
                g3 &= 0x3ffffff;
                var g4 = unchecked(h4 + carry - (1u << 26));

                var mask = unchecked((g4 >> 31) - 1);
                g0 &= mask;
                g1 &= mask;
                g2 &= mask;
                g3 &= mask;
                g4 &= mask;
                mask = ~mask;
                h0 = (h0 & mask) | g0;
                h1 = (h1 & mask) | g1;
                h2 = (h2 & mask) | g2;
                h3 = (h3 & mask) | g3;
                h4 = (h4 & mask) | g4;

                var w0 = h0 | (h1 << 26);
                var w1 = (h1 >> 6) | (h2 << 20);
                var w2 = (h2 >> 12) | (h3 << 14);
                var w3 = (h3 >> 18) | (h4 << 8);

                var sum = (ulong)w0 + pad0;
                BinaryPrimitives.WriteUInt32LittleEndian(tag, (uint)sum);
                sum = (ulong)w1 + pad1 + (sum >> 32);
                BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(4), (uint)sum);
                sum = (ulong)w2 + pad2 + (sum >> 32);
                BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(8), (uint)sum);
                sum = (ulong)w3 + pad3 + (sum >> 32);
                BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(12), (uint)sum);
            }
        }
    }
}
