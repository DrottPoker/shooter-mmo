using System;
using System.Collections.Generic;
using Mmorpg.GameSimulation;
using Mmorpg.SharedTests;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.PlayMode
{
    // The pinned goldens of the shared movement simulation and the realtime protocol, run in the
    // runtime that executes them: Mono in the Editor's PlayMode suite, and IL2CPP in the test
    // player that tools/Run-UnityTests.ps1 -Platform StandaloneWindows64 builds and runs for this
    // category. The pinned values are the ones the EditMode and .NET tests use, so a failure in
    // the player means the shipped client predicts or encodes differently from the worker.
    [Category(Category)]
    public sealed class PlayerRuntimeGoldenTests
    {
        public const string Category = "Player";

#if ENABLE_IL2CPP
        private const string Runtime = "IL2CPP";
#elif ENABLE_MONO
        private const string Runtime = "Mono";
#else
        private const string Runtime = "unknown";
#endif

        [Test]
        public void SimulationRevisionIsThePinnedOne()
        {
            Report("simulation revision", GameSimulationCompatibility.Revision);
            Assert.That(
                GameSimulationCompatibility.Revision,
                Is.EqualTo(MovementDeterminismScenario.ExpectedSimulationRevision));
        }

        [Test]
        public void MovementFingerprintIsThePinnedOne()
        {
            var fingerprint = MovementDeterminismScenario.ComputeFingerprint();
            Report("movement fingerprint", fingerprint);
            Assert.That(fingerprint, Is.EqualTo(MovementDeterminismScenario.ExpectedFingerprint));
        }

        [Test]
        public void PlannerFingerprintIsThePinnedOne()
        {
            var fingerprint = MovementDeterminismScenario.ComputePlannerTrace().Fingerprint;
            Report("planner fingerprint", fingerprint);
            Assert.That(fingerprint, Is.EqualTo(MovementDeterminismScenario.ExpectedPlannerFingerprint));
        }

        [Test]
        public void EveryMessageEncodesToItsPinnedBytes()
        {
            AssertNoFailures("protocol vectors", RealtimeProtocolGolden.VerifyVectors());
        }

        [Test]
        public void TruncatedExtendedOrMisaddressedPacketsAreRejected()
        {
            AssertNoFailures("protocol structural rejections", RealtimeProtocolGolden.VerifyStructuralRejections());
        }

        [Test]
        public void FieldRulesAcceptAndRejectExactlyAsPinned()
        {
            AssertNoFailures("protocol decode cases", RealtimeProtocolGolden.VerifyDecodeCases());
        }

        [Test]
        public void InvalidValuesFailBeforeEncoding()
        {
            AssertNoFailures("protocol encode rejections", RealtimeProtocolGolden.VerifyEncodeRejections());
        }

        [Test]
        public void ProtocolConstantsAndEnumValuesArePinned()
        {
            AssertNoFailures("protocol constants", RealtimeProtocolGolden.VerifyConstants());
        }

        // The value lands in the result file, so a run shows what the runtime computed.
        private static void Report(string name, string value)
        {
            TestContext.Out.WriteLine("[PLAYER] " + Runtime + " " + name + ": " + value);
        }

        private static void AssertNoFailures(string name, IReadOnlyList<string> failures)
        {
            Report(name, failures.Count == 0 ? "no failures" : failures.Count + " failures");
            Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
        }
    }
}
