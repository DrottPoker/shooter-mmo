using System.Security.Cryptography;
using Mmorpg.Tools.SimulationBotClient;

namespace Mmorpg.Tools.DeploySmokeTest;

// One player who joins and stays in the world until the worker ends the connection, so that the
// orderly stop of the stack has a player to save and to tell. It prints HOLDING once it plays, and
// ENDED with the code of the worker's disconnect when the connection ends. The test is satisfied
// when that code is server_shutdown: the worker said why it stopped, instead of leaving the
// player to time out.
public sealed class HoldScenario(SmokeOptions options, StackApi api, SmokeReport report, TextWriter output)
{
    public const string HoldingMarker = "HOLDING";
    public const string EndedMarker = "ENDED";
    public const string OrderlyStopCode = "server_shutdown";

    private const string PasswordAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await report.StepAsync("hold", () => HoldAsync(cancellationToken));
    }

    private async Task HoldAsync(CancellationToken cancellationToken)
    {
        var username = $"hold{options.RunId}";
        var password = RandomNumberGenerator.GetString(PasswordAlphabet, 24);
        var characterName = $"Hold{options.RunId}";
        var account = await api.RegisterAsync($"hold.{options.RunId}@example.test", username, password, cancellationToken);
        var character = await api.CreateCharacterAsync(account.SessionToken, characterName, cancellationToken);
        var join = await api.JoinAsync(account.SessionToken, options.ShardId, character.Id, cancellationToken);

        using var bot = new RealtimeBot(
            1,
            characterName,
            join.ToIdentity(1, account.AccountId, characterName),
            join.Endpoint.Host,
            join.Endpoint.UdpPort);
        bot.Start();
        var joined = await RealtimeBot.PollUntilAsync(
            [bot],
            () => bot.State == SimulationBotClientState.Joined,
            TimeSpan.FromSeconds(20),
            cancellationToken);
        report.Check("hold join", joined, $"{characterName} is {bot.State}");
        if (!joined)
        {
            return;
        }

        output.WriteLine($"{HoldingMarker} {characterName}");
        output.Flush();

        // Plays until the worker ends the connection or the time is up.
        var ended = await RealtimeBot.PollUntilAsync(
            [bot],
            () => bot.State is SimulationBotClientState.Failed or SimulationBotClientState.Completed,
            options.Timeout,
            cancellationToken);
        var code = bot.Capture().FailureCode ?? "none";
        output.WriteLine($"{EndedMarker} {code}");
        output.Flush();
        report.Check(
            "orderly stop seen by the player",
            ended && code == OrderlyStopCode,
            ended
                ? $"the worker ended the connection with {code}"
                : "the connection was still open when the time ran out");
    }
}
