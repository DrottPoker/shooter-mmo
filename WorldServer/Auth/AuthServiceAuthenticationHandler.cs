using WorldServer.Config;

namespace WorldServer.Auth;

public sealed class AuthServiceAuthenticationHandler(WorldServerConfig config) : DelegatingHandler
{
    public const string WorldServerIdHeader = "X-World-Server-ID";
    public const string WorldServerSecretHeader = "X-World-Server-Secret";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Remove(WorldServerIdHeader);
        request.Headers.Remove(WorldServerSecretHeader);
        request.Headers.TryAddWithoutValidation(WorldServerIdHeader, config.WorldServerId);
        request.Headers.TryAddWithoutValidation(WorldServerSecretHeader, config.AuthServiceSecret);

        return base.SendAsync(request, cancellationToken);
    }
}
