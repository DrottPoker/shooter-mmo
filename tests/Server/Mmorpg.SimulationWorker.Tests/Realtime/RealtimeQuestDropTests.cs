using System.Collections.Concurrent;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.WorldData.Actors;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// A Feral Wolf drops a stolen trophy for quest.fixture.trophies into its tagger's corpse, after
// its own loot, once GameService says the tagger's quest still needs it, and the tagger hears
// of it. Looting the trophy names the quest, so GameService grants it with the quest's lineage.
public sealed class RealtimeQuestDropTests
{
    private const string Trophies = "quest.fixture.trophies";
    private const string Trophy = "fixture.trophy";

    [Fact]
    public async Task AWolfDropsTheTrophyThatTheTaggersQuestNeedsIntoTheirCorpse()
    {
        await using var harness = Create(WolfNextToTheSpawn(respawnSeconds: 30f, x: 1f));
        var archer = AddArcher(harness, "Trophy Hunter");
        var grants = new ConcurrentQueue<SimulationMobLootGrantRequest>();
        harness.GameService.OnMobLootGrant = (_, request, _) =>
        {
            grants.Enqueue(request);
            return Task.FromResult(ScriptedGameServiceHandler.CommittedMobLootGrant(
                request,
                request.ExpectedCharacterRevision + 1,
                1,
                200));
        };
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var wolf = await WaitForWolfAsync(harness, client);

        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(() => Corpse(harness, wolf) is not null, "the wolf's corpse");

        var corpse = Corpse(harness, wolf)!;
        var trophy = Assert.Single(corpse.Loot, entry => entry.QuestId is not null);
        Assert.Equal((Trophy, 1, Trophies), (trophy.DefinitionId, trophy.Quantity, trophy.QuestId));
        Assert.True(corpse.Loot.Count > 1);
        Assert.Equal(corpse.Loot.Count - 1, trophy.SlotIndex);
        Assert.Equal(archer.CharacterId, corpse.OwnerCharacterId);
        var (sessionId, read) = Assert.Single(harness.GameService.QuestDropReads);
        Assert.Equal(archer.SimulationSessionId, sessionId);
        Assert.Equal(
            (archer.CharacterId, archer.SimulationSessionToken, "mob.feral_wolf"),
            (read.CharacterId, read.SessionToken, read.SourceActorDefinitionId));
        Assert.Equal([new SimulationQuestDrop(Trophies, Trophy, 1)], read.Drops);
        await harness.PollUntilAsync(() => QuestDrops(client).Count == 1, "the quest drop notice");
        var notice = Assert.Single(QuestDrops(client));
        Assert.Equal(
            (corpse.CorpseId, "mob.feral_wolf", Trophies, Trophy, 1),
            (notice.CorpseId, notice.SourceActorDefinitionId, notice.QuestId, notice.DefinitionId, notice.Quantity));

        client.SendCorpseIntent(RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), corpse.CorpseId));
        await harness.PollUntilAsync(() => client.CorpseResults.Count == 1, "the open");
        client.SendCorpseIntent(RealtimeCorpseInteractionIntent.CreateLootItem(
            Guid.NewGuid(),
            corpse.CorpseId,
            corpse.Revision,
            trophy.LootEntryId,
            0,
            Guid.NewGuid(),
            0,
            0,
            Guid.Empty,
            0));
        await harness.PollUntilAsync(() => client.CorpseResults.Count == 2, "the loot result");

        Assert.True(client.CorpseResults[^1].Succeeded, client.CorpseResults[^1].Error?.Code);
        var grant = Assert.Single(grants);
        Assert.Equal((trophy.GrantId, Trophy, Trophies), (grant.GrantId, grant.DefinitionId, grant.QuestId));
    }

    // A quest drop leaves a corpse when the wolf drops nothing of its own, unless the tagger's
    // quest no longer needs it, and then the wolf leaves without one.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AQuestDropAloneLeavesACorpseOnlyWhileTheQuestNeedsIt(bool needed)
    {
        var document = WolfNextToTheSpawn(respawnSeconds: 30f, x: 1f);
        document.Catalog.Actors.Single(actor => actor.Id == "mob.feral_wolf").LootProfileId = string.Empty;
        await using var harness = Create(document);
        var archer = AddArcher(harness, needed ? "Needful Hunter" : "Sated Hunter");
        if (!needed)
        {
            harness.GameService.OnQuestDrops = (_, _, _) => Task.FromResult(
                ScriptedGameServiceHandler.Json(new SimulationQuestDropResponse([])));
        }

        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var wolf = await WaitForWolfAsync(harness, client);

        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(
            () => client.Decode<RealtimeWorldActorDespawn>(RealtimeMessageType.WorldActorDespawn, RealtimeProtocol.TryDecodeWorldActorDespawn)
                .Any(value => value.EntityId == wolf.EntityId && value.Reason == "died"),
            "the wolf to despawn");

        Assert.Single(harness.GameService.QuestDropReads);
        if (needed)
        {
            var entry = Assert.Single(Corpse(harness, wolf)!.Loot);
            Assert.Equal((Trophy, Trophies, 0), (entry.DefinitionId, entry.QuestId, entry.SlotIndex));
            await harness.PollUntilAsync(() => QuestDrops(client).Count == 1, "the quest drop notice");
        }
        else
        {
            Assert.Null(Corpse(harness, wolf));
            Assert.Empty(QuestDrops(client));
        }
    }

    // Without the quest the wolf drops only its own loot and asks GameService nothing.
    [Fact]
    public async Task ATaggerWithoutTheQuestGetsNoQuestDrop()
    {
        await using var harness = Create(WolfNextToTheSpawn(respawnSeconds: 30f, x: 1f), questActive: false);
        var archer = AddArcher(harness, "Questless Looter");
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var wolf = await WaitForWolfAsync(harness, client);

        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(() => Corpse(harness, wolf) is not null, "the wolf's corpse");

        Assert.All(Corpse(harness, wolf)!.Loot, entry => Assert.Null(entry.QuestId));
        Assert.Empty(harness.GameService.QuestDropReads);
        Assert.Empty(QuestDrops(client));
    }

    private static IReadOnlyList<RealtimeQuestDropInCorpse> QuestDrops(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeQuestDropInCorpse>(
            RealtimeMessageType.QuestDropInCorpse,
            RealtimeProtocol.TryDecodeQuestDropInCorpse);
    }

    private static LiveMobCorpseState? Corpse(RealtimeServerHarness harness, RealtimeWorldActorSpawn wolf)
    {
        return harness.LiveMobCorpseStore!.ListActive()
            .SingleOrDefault(corpse => corpse.SourceActorRuntimeId == wolf.RuntimeActorId);
    }

    // The lowest roll lands every chance, so the wolf drops its own loot and the trophy.
    private static RealtimeServerHarness Create(WorldActorRuntime document, bool questActive = true)
    {
        var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true,
            EnableMobCorpses = true,
            EnableQuests = true,
            WorldActorDocument = document,
            ItemCatalog = TestCatalog(minimumDamage: 100, maximumDamage: 100),
            CombatRules = TestRules(respawnSeconds: 30f),
            CombatRandom = new LowestRoll()
        });
        if (questActive)
        {
            harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(ScriptedGameServiceHandler.QuestLog(
                request.CharacterId,
                1,
                new SimulationActiveQuest(Trophies, "collect", DateTime.UtcNow, [])));
        }

        return harness;
    }

    private static RealtimeTestCharacter AddArcher(RealtimeServerHarness harness, string name)
    {
        var archer = harness.AddCharacter(name) with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        return archer;
    }
}
