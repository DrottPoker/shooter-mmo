using System;
using System.Collections.Generic;
using Mmorpg.GameProtocol;
using Mmorpg.SharedTests;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // The .NET tests run the same cases of the sealed transport, and the IL2CPP test player runs
    // them in the Player category, so a failure here means the client seals, opens, or proves
    // differently from SimulationWorker. Unity's runtime has no platform ChaCha20-Poly1305, so the
    // client uses the managed one.
    public sealed class RealtimeTransportSecurityTests
    {
        [Test]
        public void TheClientUsesTheManagedImplementation()
        {
            Assert.That(RealtimeAead.IsPlatformSupported, Is.False);
            Assert.That(RealtimeAead.DefaultImplementationName, Is.EqualTo(RealtimeManagedChaCha20Poly1305.Name));
        }

        [Test]
        public void TheManagedImplementationMeetsTheRfc8439Vectors()
        {
            AssertNoFailures(RealtimeTransportSecurityCases.VerifyAeadVectors(RealtimeTransportSecurityCases.RealtimeManagedOnly));
        }

        [Test]
        public void HkdfMeetsTheRfc5869Vectors()
        {
            AssertNoFailures(RealtimeTransportSecurityCases.VerifyHkdfVectors());
        }

        [Test]
        public void TheKeyScheduleTheProofAndTheDatagramFormatArePinned()
        {
            AssertNoFailures(RealtimeTransportSecurityCases.VerifyPinnedVectors());
        }

        [Test]
        public void AJoinProofVerifiesOnlyWithItsOwnInputs()
        {
            AssertNoFailures(RealtimeTransportSecurityCases.VerifyJoinProof());
        }

        [Test]
        public void TheReplayWindowTakesEveryCounterOnceInAnyOrder()
        {
            AssertNoFailures(RealtimeTransportSecurityCases.VerifyReplayWindow());
        }

        [Test]
        public void TheLayersSealAfterTheSwitchAndDropWhatDoesNotAuthenticate()
        {
            AssertNoFailures(RealtimeTransportSecurityCases.VerifyLayers());
        }

        [Test]
        public void CountersNeverRepeatOverALongRun()
        {
            AssertNoFailures(RealtimeTransportSecurityCases.VerifyCountersNeverRepeat(20000));
        }

        private static void AssertNoFailures(IReadOnlyList<string> failures)
        {
            Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
        }
    }
}
