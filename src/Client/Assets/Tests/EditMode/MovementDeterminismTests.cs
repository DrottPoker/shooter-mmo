using Mmorpg.GameSimulation;
using Mmorpg.SharedTests;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class MovementDeterminismTests
    {
        // The .NET backend tests run the same scenario and pin the same values, so a
        // mismatch here means Unity prediction diverges from the authoritative worker.
        [Test]
        public void UnityRuntimeMatchesThePinnedMovementRevisionAndFingerprint()
        {
            Assert.That(
                GameSimulationCompatibility.Revision,
                Is.EqualTo(MovementDeterminismScenario.ExpectedSimulationRevision));
            Assert.That(
                MovementDeterminismScenario.ComputeFingerprint(),
                Is.EqualTo(MovementDeterminismScenario.ExpectedFingerprint));
        }

        // The worker can plan routes for world actors, so Unity and .NET must plan alike.
        [Test]
        public void UnityRuntimeMatchesThePinnedPlannerFingerprint()
        {
            Assert.That(
                MovementDeterminismScenario.ComputePlannerTrace().Fingerprint,
                Is.EqualTo(MovementDeterminismScenario.ExpectedPlannerFingerprint));
        }
    }
}
