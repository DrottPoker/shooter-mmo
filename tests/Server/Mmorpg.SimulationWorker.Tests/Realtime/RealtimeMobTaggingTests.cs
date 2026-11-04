using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Actors;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Mob tagging over real UDP: the first player who damages a Mob tags it, only the tagger
// gets the credit and may loot the corpse, and a Mob that gives up the fight is free again.
public sealed class RealtimeMobTaggingTests
{
    [Fact]
    public async Task TheFirstPlayerToDamageAMobTagsItForEveryViewer()
    {
        await using var harness = Create(WolfNextToTheSpawn(respawnSeconds: 30f), minimumDamage: 1);
        var tagger = AddArcher(harness, "Quick Tagger");
        var helper = AddArcher(harness, "Late Helper");
        await harness.StartAsync();
        var taggerClient = await harness.JoinAsync(tagger);
        var helperClient = await harness.JoinAsync(helper);
        var wolf = await WaitForWolfAsync(harness, taggerClient);
        var taggerEntityId = taggerClient.JoinAccepted!.ControlledEntityId;

        taggerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(
            () => States(helperClient).Any(value => value.EntityId == wolf.EntityId && value.TaggedByEntityId == taggerEntityId),
            "the helper to see the wolf tagged");
        helperClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        var helperEntityId = helperClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => Events(taggerClient).Any(value => value.Kind == RealtimeCombatEventKind.Hit
                && value.SourceEntityId == helperEntityId
                && value.TargetEntityId == wolf.EntityId),
            "the helper's arrow to hit the wolf");

        // The helper's damage never takes the tag over.
        Assert.All(
            States(taggerClient).Where(value => value.EntityId == wolf.EntityId && value.TaggedByEntityId != 0),
            value => Assert.Equal(taggerEntityId, value.TaggedByEntityId));
        Assert.All(
            States(taggerClient).Where(value => value.EntityId == taggerEntityId || value.EntityId == helperEntityId),
            value => Assert.Equal(0ul, value.TaggedByEntityId));
    }

    // The wolf stands within reach of the spawn, so both players may open its corpse there.
    [Fact]
    public async Task OnlyTheTaggerMayLootTheCorpse()
    {
        await using var harness = Create(WolfNextToTheSpawn(respawnSeconds: 30f, x: 1f), minimumDamage: 100);
        var tagger = AddArcher(harness, "Lucky Tagger");
        var bystander = AddArcher(harness, "Hopeful Bystander");
        await harness.StartAsync();
        var taggerClient = await harness.JoinAsync(tagger);
        var bystanderClient = await harness.JoinAsync(bystander);
        var wolf = await WaitForWolfAsync(harness, taggerClient);

        taggerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(
            () => harness.LiveMobCorpseStore!.ListActive().Any(corpse => corpse.SourceActorRuntimeId == wolf.RuntimeActorId),
            "the wolf's corpse");
        var corpse = harness.LiveMobCorpseStore!.ListActive().Single(value => value.SourceActorRuntimeId == wolf.RuntimeActorId);
        Assert.Equal(tagger.CharacterId, corpse.OwnerCharacterId);
        await harness.PollUntilAsync(
            () => Presence(taggerClient, corpse.CorpseId) is not null && Presence(bystanderClient, corpse.CorpseId) is not null,
            "both players to see the corpse");
        Assert.True(Presence(taggerClient, corpse.CorpseId)!.IsLootable);
        Assert.False(Presence(bystanderClient, corpse.CorpseId)!.IsLootable);

        bystanderClient.SendCorpseIntent(RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), corpse.CorpseId));
        taggerClient.SendCorpseIntent(RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), corpse.CorpseId));
        await harness.PollUntilAsync(
            () => Results(bystanderClient).Count == 1 && Results(taggerClient).Count == 1,
            "both open results");

        var refused = Assert.Single(Results(bystanderClient));
        Assert.False(refused.Succeeded);
        Assert.Equal(RealtimeCorpseViewClosureCodes.NotLootable, refused.Error!.Code);
        var opened = Assert.Single(Results(taggerClient));
        Assert.True(opened.Succeeded, opened.Error?.Code);
    }

    // The wolf stands just outside its detection range, so it dies to the first arrow where it
    // stood, eight and a half metres from the archer.
    [Fact]
    public async Task ATaggerBeyondTheCreditRangeLeavesTheCorpseToEveryone()
    {
        await using var harness = Create(
            WolfNextToTheSpawn(respawnSeconds: 30f, x: 8.5f),
            minimumDamage: 100,
            killCreditRangeMeters: 1f);
        var archer = AddArcher(harness, "Distant Archer");
        var bystander = AddArcher(harness, "Nearby Bystander");
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var bystanderClient = await harness.JoinAsync(bystander);
        var wolf = await WaitForWolfAsync(harness, archerClient);

        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(
            () => harness.LiveMobCorpseStore!.ListActive().Any(corpse => corpse.SourceActorRuntimeId == wolf.RuntimeActorId),
            "the wolf's corpse");

        var corpse = harness.LiveMobCorpseStore!.ListActive().Single(value => value.SourceActorRuntimeId == wolf.RuntimeActorId);
        Assert.Null(corpse.OwnerCharacterId);
        await harness.PollUntilAsync(
            () => Presence(bystanderClient, corpse.CorpseId) is not null,
            "the bystander to see the corpse");
        Assert.True(Presence(bystanderClient, corpse.CorpseId)!.IsLootable);
    }

    [Fact]
    public async Task AMobThatGivesUpTheFightIsFreeAgain()
    {
        var document = WolfNextToTheSpawn(respawnSeconds: 30f, x: 6f);
        var definition = document.Actors.Single(actor => actor.Id == "mob.feral_wolf");
        document.BrainProfiles.Single(profile => profile.Id == definition.BrainProfileId).LeashRange = 3f;
        await using var harness = Create(document, minimumDamage: 1);
        var tagger = AddArcher(harness, "Fleeting Tagger");
        await harness.StartAsync();
        var client = await harness.JoinAsync(tagger);
        var wolf = await WaitForWolfAsync(harness, client);
        var taggerEntityId = client.JoinAccepted!.ControlledEntityId;

        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(
            () => States(client).Any(value => value.EntityId == wolf.EntityId && value.TaggedByEntityId == taggerEntityId),
            "the wolf to be tagged");
        await harness.PollUntilAsync(
            () => States(client).Any(value => value.EntityId == wolf.EntityId && value.LifeState == RealtimeCombatLifeState.Evading),
            "the wolf to give up past its leash");

        Assert.All(
            States(client).Where(value => value.EntityId == wolf.EntityId && value.LifeState == RealtimeCombatLifeState.Evading),
            value => Assert.Equal(0ul, value.TaggedByEntityId));
    }

    private static RealtimeServerHarness Create(
        WorldActorRuntimeDocument document,
        int minimumDamage,
        float killCreditRangeMeters = 100f)
    {
        return RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true,
            EnableMobCorpses = true,
            WorldActorDocument = document,
            ItemCatalog = TestCatalog(minimumDamage: minimumDamage, maximumDamage: minimumDamage),
            CombatRules = TestRules(respawnSeconds: 30f, killCreditRangeMeters: killCreditRangeMeters),
            CombatRandom = new LowestRoll()
        });
    }

    private static RealtimeTestCharacter AddArcher(RealtimeServerHarness harness, string name)
    {
        var archer = harness.AddCharacter(name) with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        return archer;
    }

    private static RealtimeCorpsePresence? Presence(RealtimeRecordingClient client, Guid corpseId)
    {
        return client.CorpsePresence
            .SelectMany(chunk => chunk.Corpses)
            .LastOrDefault(value => value.CorpseId == corpseId);
    }

    private static IReadOnlyList<RealtimeCorpseInteractionResult> Results(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeCorpseInteractionResult>(
            RealtimeMessageType.CorpseInteractionResult,
            RealtimeProtocol.TryDecodeCorpseInteractionResult);
    }
}
