using Mmorpg.SimulationWorker.Config;

namespace Mmorpg.SimulationWorker.GameServiceApi;

public sealed class GameServiceAuthenticationHandler(SimulationWorkerConfig config) : DelegatingHandler
{
    public const string SimulationWorkerIdHeader = "X-Simulation-Worker-ID";
    public const string SimulationWorkerSecretHeader = "X-Simulation-Worker-Secret";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Remove(SimulationWorkerIdHeader);
        request.Headers.Remove(SimulationWorkerSecretHeader);
        request.Headers.TryAddWithoutValidation(SimulationWorkerIdHeader, config.SimulationWorkerId);
        request.Headers.TryAddWithoutValidation(SimulationWorkerSecretHeader, config.GameServiceSecret);

        return base.SendAsync(request, cancellationToken);
    }
}
