namespace Mmorpg.GameService.Characters;

// Characters module composition: characters, ownership, and character currency.
public static class CharactersModuleRegistration
{
    public static IServiceCollection AddCharactersModule(
        this IServiceCollection services,
        CharactersModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddScoped<CharacterService>();
        return services;
    }

    public static IEndpointRouteBuilder MapCharactersModule(this IEndpointRouteBuilder app)
    {
        return app.MapCharacterEndpoints();
    }
}
