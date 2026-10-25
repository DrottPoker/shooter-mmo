using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Mmorpg.WorldData.Characters;

namespace Mmorpg.GameService.Characters.Tests;

// Character looks against the checked-in appearance content: creation, the catalog that the
// creation screen reads, the changes a worker stores, and the check at start.
public sealed class CharacterAppearanceIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task ACharacterCreatedWithoutALookGetsTheDefaultLookOfTheDefaultBody()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();

        var player = await context.RegisterPlayerAsync();

        Assert.Equal("Human_Male", player.Character.BodyModel);
        var stored = await ReadAsync(context, player.Character.Id);
        Assert.NotNull(stored);
        Assert.Equal("Human_Male", stored.BodyModel);
        Assert.Equal(["face=Stubble", "eyes=Teal", "underwear(legs)=Briefs"], stored.Choices.Select(choice => choice.ToString()));
        Assert.False(stored.HeadwearLowered);
    }

    [PostgresIntegrationFact]
    public async Task ACharacterCreatedWithALookKeepsItsBodyAndChoicesInOrder()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();

        var created = await context.CharacterService.CreateAsync(
            player.Registration.AccountId,
            new CreateCharacterRequest(
                "Woad Weaver",
                new CharacterAppearanceRequest(
                    "Human_Female",
                    [
                        new CharacterAppearanceChoiceRequest("face", "", "Freckles"),
                        new CharacterAppearanceChoiceRequest("eyes", null, "Hazel"),
                        new CharacterAppearanceChoiceRequest("tattoo", "", "ForearmRunes"),
                        new CharacterAppearanceChoiceRequest("underwear", "body", "Bra"),
                        new CharacterAppearanceChoiceRequest("underwear", "legs", "Briefs")
                    ])),
            CancellationToken.None);

        Assert.True(created.Succeeded, created.Error?.Message);
        Assert.Equal("Human_Female", created.Value!.BodyModel);
        var listed = await context.CharacterService.ListAsync(player.Registration.AccountId, CancellationToken.None);
        Assert.Equal(["Human_Male", "Human_Female"], listed.Value!.Select(character => character.BodyModel));
        var stored = await ReadAsync(context, created.Value.Id);
        Assert.NotNull(stored);
        // No hair: a bald character is a look of its own.
        Assert.Equal(
            ["face=Freckles", "eyes=Hazel", "tattoo=ForearmRunes", "underwear(body)=Bra", "underwear(legs)=Briefs"],
            stored.Choices.Select(choice => choice.ToString()));
    }

    [PostgresIntegrationFact]
    public async Task ABodyModelWithoutChoicesGetsItsOwnDefaultLook()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();

        var created = await context.CharacterService.CreateAsync(
            player.Registration.AccountId,
            new CreateCharacterRequest("Default Female", new CharacterAppearanceRequest("Human_Female", null)),
            CancellationToken.None);

        Assert.True(created.Succeeded, created.Error?.Message);
        var stored = await ReadAsync(context, created.Value!.Id);
        Assert.Equal(
            ["face=Blush", "eyes=Teal", "underwear(body)=Bra", "underwear(legs)=Briefs"],
            stored!.Choices.Select(choice => choice.ToString()));
    }

    [PostgresIntegrationFact]
    public async Task ALookCreationDoesNotOfferIsRejectedWithoutCreatingTheCharacter()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        CharacterAppearanceRequest[] invalidLooks =
        [
            new("Human_Giant", null),
            // Another body's face.
            new("Human_Male", [Choice("face", "Freckles"), Choice("eyes", "Teal"), Choice("underwear", "Briefs", "legs")]),
            // Required underwear left out.
            new("Human_Male", [Choice("face", "Clean"), Choice("eyes", "Teal")]),
            new("Human_Male", [Choice("face", "Clean"), Choice("face", "Stubble"), Choice("eyes", "Teal"), Choice("underwear", "Briefs", "legs")]),
            new("Human_Male", [Choice("face", "Clean"), Choice("eyes", "Teal"), Choice("underwear", "Briefs", "legs"), Choice("beard", "Long")])
        ];

        foreach (var look in invalidLooks)
        {
            var created = await context.CharacterService.CreateAsync(
                player.Registration.AccountId,
                new CreateCharacterRequest("Rejected Look", look),
                CancellationToken.None);

            Assert.False(created.Succeeded);
            Assert.Equal(StatusCodes.Status400BadRequest, created.StatusCode);
            Assert.Equal("invalid_character_appearance", created.Error!.Code);
        }

        Assert.Equal(1, await context.ExecuteScalarIntAsync("select count(*) from characters;"));
        Assert.Equal(1, await context.ExecuteScalarIntAsync("select count(*) from character_appearances;"));
    }

    [PostgresIntegrationFact]
    public async Task TheCreationCatalogListsTheKindsAndEveryBodyModelWithItsOffers()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);

        var catalog = context.CharacterService.AppearanceCatalog();

        Assert.Equal(["face", "eyes", "scar", "tattoo", "underwear", "hair"], catalog.Kinds.Select(kind => kind.Id));
        var underwear = Assert.Single(catalog.Kinds, kind => kind.Id == "underwear");
        Assert.True(underwear.Required);
        Assert.True(underwear.ChangesInGame);
        Assert.Equal(["Human_Male", "Human_Female"], catalog.BodyModels.Select(body => body.Id));
        var female = catalog.BodyModels[1];
        Assert.Equal(["Blush", "Freckles"], Assert.Single(female.Offers, offer => offer.Kind == "face").Options);
        Assert.Equal(["body", "legs"], female.Offers.Where(offer => offer.Kind == "underwear").Select(offer => offer.Slot));
    }

    [PostgresIntegrationFact]
    public async Task HeadwearAndADevelopmentLookAreStoredForTheCharacter()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);
        var player = await context.RegisterPlayerAsync();
        var appearances = context.Services.GetRequiredService<ICharacterAppearances>();

        var lowered = await ChangeAsync(context, scope => appearances.SetHeadwearLoweredAsync(
            scope,
            player.Character.Id,
            true,
            CancellationToken.None));
        Assert.True(lowered.Appearance!.HeadwearLowered);

        // One look serves every body, so a face of the other body is kept for a body swap.
        var developmentLook = await ChangeAsync(context, scope => appearances.SetDevelopmentLookAsync(
            scope,
            player.Character.Id,
            "Human_Female",
            [
                new CharacterAppearanceChoice("face", "", "Stubble"),
                new CharacterAppearanceChoice("hair", "", "DarkBrownHair")
            ],
            CancellationToken.None));
        Assert.Equal("Human_Female", developmentLook.Appearance!.BodyModel);
        Assert.Equal(["face=Stubble", "hair=DarkBrownHair"], developmentLook.Appearance.Choices.Select(choice => choice.ToString()));
        // The headwear stays as the player left it.
        Assert.True(developmentLook.Appearance.HeadwearLowered);

        var unknownOption = await ChangeAsync(context, scope => appearances.SetDevelopmentLookAsync(
            scope,
            player.Character.Id,
            "Human_Female",
            [new CharacterAppearanceChoice("hair", "", "Mohawk")],
            CancellationToken.None));
        Assert.Equal("invalid_character_appearance", unknownOption.ErrorCode);

        var missing = await ChangeAsync(context, scope => appearances.SetHeadwearLoweredAsync(
            scope,
            Guid.NewGuid(),
            false,
            CancellationToken.None));
        Assert.Equal("character_not_found", missing.ErrorCode);
    }

    [PostgresIntegrationFact]
    public async Task TheLooksOfDeletedCharactersStayReadableForTheirCorpses()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);
        var player = await context.RegisterPlayerAsync();
        var appearances = context.Services.GetRequiredService<ICharacterAppearances>();
        await context.ExecuteAsync($"update characters set deleted_at = now() where id = '{player.Character.Id}';");

        Assert.Null(await ReadAsync(context, player.Character.Id));
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var looks = await appearances.ReadManyAsync(
            new PostgresTransactionScope(connection, transaction),
            [player.Character.Id, Guid.NewGuid()],
            CancellationToken.None);
        Assert.Equal("Human_Male", Assert.Single(looks).Value.BodyModel);
    }

    [PostgresIntegrationFact]
    public async Task StartIsRefusedWhenTheContentNoLongerOffersAStoredLook()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);
        var player = await context.RegisterPlayerAsync();
        await context.ExecuteAsync(
            $$"""
            update character_appearances
            set choices = '[{"kind": "hair", "slot": "", "option": "Mohawk"}]'::jsonb
            where character_id = '{{player.Character.Id}}';
            """);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.DatabaseInitializer.InitializeAsync(CancellationToken.None));

        Assert.Contains(player.Character.Id.ToString(), exception.Message);
        Assert.Contains("No body model offers hair=Mohawk.", exception.Message);
    }

    private static CharacterAppearanceChoiceRequest Choice(string kind, string option, string slot = "")
    {
        return new CharacterAppearanceChoiceRequest(kind, slot, option);
    }

    private static async Task<CharacterAppearanceState?> ReadAsync(PostgresIntegrationTestContext context, Guid characterId)
    {
        var appearances = context.Services.GetRequiredService<ICharacterAppearances>();
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        return await appearances.ReadAsync(new PostgresTransactionScope(connection, transaction), characterId, CancellationToken.None);
    }

    private static async Task<CharacterAppearanceChange> ChangeAsync(
        PostgresIntegrationTestContext context,
        Func<PostgresTransactionScope, Task<CharacterAppearanceChange>> change)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await change(new PostgresTransactionScope(connection, transaction));
        await transaction.CommitAsync();
        return result;
    }
}
