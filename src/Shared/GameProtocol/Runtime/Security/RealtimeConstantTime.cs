using System;
using System.Runtime.CompilerServices;

namespace Mmorpg.GameProtocol
{
    // Comparisons whose time depends only on the length, never on where the bytes differ, for
    // tags and proofs. .NET Standard 2.1 has no CryptographicOperations.FixedTimeEquals.
    public static class RealtimeConstantTime
    {
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public static bool AreEqual(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            var difference = 0;
            for (var index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }
    }
}
