using AuthService.Auth;
using AuthService.Http;

namespace AuthService.Characters;

public static class CharacterEndpoints
{
    public static IEndpointRouteBuilder MapCharacterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/characters");

        group.MapGet("/", async (
            HttpRequest request,
            SessionService sessionService,
            CharacterService characterService,
            CancellationToken cancellationToken) =>
        {
            var session = await sessionService.AuthenticateAsync(request, cancellationToken);
            if (!session.Succeeded)
            {
                return session.ToHttpResult();
            }

            var result = await characterService.ListAsync(session.Value!.AccountId, cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/", async (
            HttpRequest request,
            CreateCharacterRequest createRequest,
            SessionService sessionService,
            CharacterService characterService,
            CancellationToken cancellationToken) =>
        {
            var session = await sessionService.AuthenticateAsync(request, cancellationToken);
            if (!session.Succeeded)
            {
                return session.ToHttpResult();
            }

            var result = await characterService.CreateAsync(
                session.Value!.AccountId,
                createRequest,
                cancellationToken);

            return result.ToHttpResult();
        });

        return app;
    }
}

