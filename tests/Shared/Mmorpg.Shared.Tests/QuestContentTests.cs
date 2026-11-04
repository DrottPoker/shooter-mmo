using Mmorpg.Testing.Content;
using Mmorpg.Tools.ContentPipeline;
using Mmorpg.Tools.QuestCatalogCompiler;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.Shared.Tests;

// The checked-in quests: the development chain that Development World 2 plays through.
public sealed class QuestContentTests
{
    private const string TestWorldId = "development-world-2";

    private static readonly Lazy<QuestCatalogRuntimeDocument> Catalog = new(QuestCatalogTestData.Compile);

    [Fact]
    public void CheckedInQuestsMatchTheGeneratedCatalog()
    {
        QuestCatalogCompiler.ValidateRuntime(Catalog.Value);

        Assert.Equal(
            QuestJson.SerializeRuntime(Catalog.Value),
            StrictContentJson.NormalizeLineEndings(QuestCatalogTestData.RuntimeJson));
    }

    [Fact]
    public void TheDevelopmentChainLeadsFromTheWolvesAndTheLetterToTheAlpha()
    {
        var quests = Catalog.Value.Quests.ToDictionary(quest => quest.Id, StringComparer.Ordinal);

        Assert.Empty(quests["quest.wolves_at_the_gate"].RequiredQuestIds);
        Assert.Equal(["quest.wolves_at_the_gate"], quests["quest.pelts_and_fangs"].RequiredQuestIds);
        Assert.Empty(quests["quest.letter_for_the_outpost"].RequiredQuestIds);
        Assert.Equal(["quest.letter_for_the_outpost"], quests["quest.sergeants_request"].RequiredQuestIds);
        Assert.Equal(
            ["quest.sergeants_request", "quest.wolves_at_the_gate"],
            quests["quest.the_alpha"].RequiredQuestIds);
    }

    // Development World 2 tests everything, so every checked-in quest is playable there.
    [Fact]
    public void EveryCheckedInQuestCanBePlayedInDevelopmentWorldTwo()
    {
        var world = WorldActorTestData.CompileWorld(TestWorldId);
        var placed = world.SpawnInstances.Select(spawn => spawn.ActorId).ToHashSet(StringComparer.Ordinal);
        var placedMobTags = world.Actors
            .Where(actor => actor.Kind == WorldActorKindIds.Mob && placed.Contains(actor.Id))
            .SelectMany(actor => actor.Tags)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var quest in Catalog.Value.Quests)
        {
            Assert.Contains(quest.GiverActorId, placed);
            Assert.Contains(quest.TurnInActorId, placed);
            foreach (var objective in quest.Steps.SelectMany(step => step.Objectives))
            {
                Assert.All(objective.ActorIds, actorId => Assert.Contains(actorId, placed));
                Assert.All(objective.DropSources, source => Assert.Contains(source.ActorId, placed));
                if (objective.ActorTag.Length > 0)
                {
                    Assert.Contains(objective.ActorTag, placedMobTags);
                }

                if (objective.NpcActorId.Length > 0)
                {
                    Assert.Contains(objective.NpcActorId, placed);
                }
            }
        }
    }
}
