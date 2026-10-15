using NUnit.Framework;
using Mmorpg.GameSimulation;
using Mmorpg.SharedTests;

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
    }
}
