using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class WorldActorLoaderTests
{
    [Fact]
    public void RuntimeStructuralTamperingFailsStartupValidation()
    {
        var runtime = WorldActorTestData.Compile();
        runtime.Actors[0].DisplayName = "Tampered Actor";

        Assert.Throws<WorldActorValidationException>(
            () => WorldActorCompiler.ValidateRuntime(runtime));
        Assert.Throws<InvalidDataException>(
            () => SimulationWorker.WorldActors.WorldActorLoader.Validate(
                runtime,
                runtime.WorldId));
    }
}
