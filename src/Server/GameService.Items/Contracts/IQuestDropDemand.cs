using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Items.Contracts;

// What a character's quests ask for from a drop: the quantity of an item in total that the
// collect objective of the quest's current step asks for, when the drop's source is one of the
// objective's drop sources, or 0 when the quest is unknown, not active, or asks for no such drop.
// Items owns it and its corpse loot rules use it; Quests implements it, reading in the caller's
// transaction.
public interface IQuestDropDemand
{
    Task<int> WantedAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        string questId,
        string sourceActorDefinitionId,
        string definitionId,
        CancellationToken cancellationToken);
}
