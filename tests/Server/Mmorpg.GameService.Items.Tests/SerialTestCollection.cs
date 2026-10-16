namespace Mmorpg.GameService.Items.Tests;

// Classes that observe process-wide state, such as a MeterListener that sees every item
// transaction in the process, join this collection. It runs alone after the parallel tests.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialTestCollection
{
    public const string Name = "Serial";
}
