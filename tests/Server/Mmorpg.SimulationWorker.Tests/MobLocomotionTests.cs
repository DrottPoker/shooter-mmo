using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class MobLocomotionTests
{
    // A Mob follows its patrol at its profile's patrol speed and chases and walks home at its walk
    // speed, with the same motor and capsule either way.
    [Fact]
    public void APatrolWalksAtThePatrolSpeedAndEverythingElseAtTheWalkSpeed()
    {
        var document = WorldActorTestData.Compile();
        var profile = document.LocomotionProfiles.Single(value => value.Id == "mob.quadruped");
        var world = new MovementSimulationSettings(30, 5f, 720f, -24f, -2f, 0f, -100f, 100f, -100f, 100f);

        var locomotion = new MobLocomotion(profile, world, CollisionTestWorldFactory.Create(), 40f);

        Assert.True(profile.PatrolSpeed < profile.WalkSpeed);
        Assert.Equal(profile.WalkSpeed, locomotion.Settings.WalkSpeed);
        Assert.Equal(profile.PatrolSpeed, locomotion.PatrolSettings.WalkSpeed);
        Assert.Equal(locomotion.Settings.CharacterCollision.Radius, locomotion.PatrolSettings.CharacterCollision.Radius);
        Assert.Equal(locomotion.Settings.CharacterCollision.Height, locomotion.PatrolSettings.CharacterCollision.Height);
        Assert.Equal(locomotion.Settings.TickRateHz, locomotion.PatrolSettings.TickRateHz);
    }
}
