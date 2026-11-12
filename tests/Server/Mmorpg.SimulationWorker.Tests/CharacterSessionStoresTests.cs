using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Appearance;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Tests;

// Pins on which occasions each of the worker's per-character stores loses a character's state.
public sealed class CharacterSessionStoresTests
{
    private static readonly Guid CharacterId = Guid.NewGuid();
    private static readonly Guid SessionId = Guid.NewGuid();

    [Fact]
    public void EveryStoreLosesTheStateOfTheSessionWhenTheSessionEnds()
    {
        var fixture = new StoresFixture();

        fixture.Stores.Remove(CharacterId, Guid.NewGuid());
        Assert.Equal((true, true, true), fixture.Held());

        fixture.Stores.Remove(CharacterId, SessionId);
        Assert.Equal((false, false, false), fixture.Held());
    }

    [Fact]
    public void AReplacedEntityTakesTheItemStateAndTheLookButLeavesTheQuestLog()
    {
        var fixture = new StoresFixture();

        fixture.Stores.Remove(CharacterSessionRemovals.ReplacedEntity, CharacterId, SessionId);

        Assert.Equal((false, false, true), fixture.Held());
    }

    [Fact]
    public void AConfirmedReleaseTakesOnlyTheItemState()
    {
        var fixture = new StoresFixture();

        fixture.Stores.Remove(CharacterSessionRemovals.ConfirmedRelease, CharacterId, SessionId);

        Assert.Equal((false, true, true), fixture.Held());
    }

    [Fact]
    public void TheShutdownClearsOnlyTheItemState()
    {
        var fixture = new StoresFixture();

        fixture.Stores.Clear(CharacterSessionRemovals.Shutdown);

        Assert.Equal((false, true, true), fixture.Held());
    }

    private sealed class StoresFixture
    {
        public StoresFixture()
        {
            ItemStates.Register(
                CharacterId,
                SessionId,
                CharacterStateTestData.ItemState(PlayerCarryState.Default),
                out _);
            Looks.Register(CharacterId, SessionId, "session-token", new CharacterLook(CharacterStateTestData.Appearance));
            QuestLogs.Register(
                CharacterId,
                SessionId,
                CharacterQuestLog.From(new SimulationQuestLogResponse(CharacterId, "catalog-revision", 1, [], [])));
            Stores = CharacterSessionStores.Create(ItemStates, Looks, QuestLogs);
        }

        public CharacterItemStateStore ItemStates { get; } = new();

        public CharacterLookStore Looks { get; } = new();

        public CharacterQuestLogStore QuestLogs { get; } = new();

        public CharacterSessionStores Stores { get; }

        public (bool ItemState, bool Look, bool QuestLog) Held()
        {
            return (
                ItemStates.TryGet(CharacterId, SessionId, out _),
                Looks.TryGet(CharacterId, SessionId, out _, out _),
                QuestLogs.TryGet(CharacterId, SessionId, out _));
        }
    }
}
