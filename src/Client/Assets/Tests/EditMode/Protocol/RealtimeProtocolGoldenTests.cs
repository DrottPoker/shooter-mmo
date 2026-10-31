using System;
using System.Collections.Generic;
using Mmorpg.SharedTests;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // The .NET backend tests run the same golden cases, so a failure here means Unity
    // encodes or accepts realtime packets differently from SimulationWorker.
    public sealed class RealtimeProtocolGoldenTests
    {
        [Test]
        public void EveryMessageEncodesToItsPinnedBytes()
        {
            AssertNoFailures(RealtimeProtocolGolden.VerifyVectors());
        }

        [Test]
        public void TruncatedExtendedOrMisaddressedPacketsAreRejected()
        {
            AssertNoFailures(RealtimeProtocolGolden.VerifyStructuralRejections());
        }

        [Test]
        public void FieldRulesAcceptAndRejectExactlyAsPinned()
        {
            AssertNoFailures(RealtimeProtocolGolden.VerifyDecodeCases());
        }

        [Test]
        public void InvalidValuesFailBeforeEncoding()
        {
            AssertNoFailures(RealtimeProtocolGolden.VerifyEncodeRejections());
        }

        [Test]
        public void ProtocolConstantsAndEnumValuesArePinned()
        {
            AssertNoFailures(RealtimeProtocolGolden.VerifyConstants());
        }

        private static void AssertNoFailures(IReadOnlyList<string> failures)
        {
            Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
        }
    }
}
