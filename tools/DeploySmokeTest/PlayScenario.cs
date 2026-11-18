using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Mmorpg.GameProtocol;
using Mmorpg.Tools.SimulationBotClient;

namespace Mmorpg.Tools.DeploySmokeTest;

// A player's whole path through the stack as a client takes it, with no Development switch: the
// proxy's TLS, accounts, characters, the Shard list, the join, the realtime connection with
// movement, the leave, and the logout. Then what a stranger or a stranger's script meets: a
// forged forwarded address, the worker's routes without the worker's secret, and the routes that
// only Development has. Every check is one line of the report.
public sealed class PlayScenario(SmokeOptions options, StackApi api, SmokeReport report)
{
    private const string PasswordAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    // GameService answers an unauthorized worker route with this code, as a client of it may rely on.
    private const string InvalidServiceCredentials = "invalid_service_credentials";

    private sealed class Player(int index, string email, string username, string password, string characterName)
    {
        public int Index { get; } = index;

        public string Email { get; } = email;

        public string Username { get; } = username;

        public string Password { get; } = password;

        public string CharacterName { get; } = characterName;

        public AuthAnswer? Auth { get; set; }

        public string? Token { get; set; }

        public CharacterAnswer? Character { get; set; }

        public JoinAnswer? Join { get; set; }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await report.StepAsync("health", () => CheckHealthAsync(cancellationToken));
        var players = Enumerable.Range(1, options.Accounts).Select(NewPlayer).ToArray();
        var accountsReady = await report.StepAsync("accounts", () => RegisterAndLoginAsync(players, cancellationToken));
        var charactersReady = accountsReady
            && await report.StepAsync("characters", () => CreateCharactersAsync(players, cancellationToken));
        var shardReady = charactersReady
            && await report.StepAsync("shards", () => CheckShardsAsync(cancellationToken));
        if (shardReady)
        {
            await report.StepAsync("realtime", () => PlayAsync(players, cancellationToken));
        }

        if (accountsReady)
        {
            await report.StepAsync("worker routes", () => CheckWorkerRoutesAsync(players[0], cancellationToken));
            await report.StepAsync("logout", () => LogoutAsync(players, cancellationToken));
        }

        await report.StepAsync("development routes", () => CheckDevelopmentRoutesAsync(cancellationToken));
        await report.StepAsync("forged address", () => CheckForgedAddressAsync(cancellationToken));
    }

    private async Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        var live = await api.SendAsync(HttpMethod.Get, "/health/live", cancellationToken: cancellationToken);
        report.Check("health live", live.Status == HttpStatusCode.OK, $"answered {(int)live.Status}");

        var ready = await api.SendAsync(HttpMethod.Get, "/health/ready", cancellationToken: cancellationToken);
        var status = StackJson.Text(ready.Body, "status");
        report.Check(
            "health ready",
            ready.Status == HttpStatusCode.OK && status == "ready",
            $"answered {(int)ready.Status} with status {status ?? "none"}");
        report.Check(
            "health dependencies",
            StackJson.DependenciesReachable(ready.Body, out var names),
            $"reachable: {names}");

        var content = await api.SendAsync(HttpMethod.Get, "/api/content", cancellationToken: cancellationToken);
        report.Check("content manifest", content.IsSuccess, $"answered {(int)content.Status}");
    }

    private Player NewPlayer(int index)
    {
        var name = $"smoke{options.RunId}{index}";
        return new Player(
            index,
            $"smoke.{options.RunId}.{index}@example.test",
            name,
            RandomNumberGenerator.GetString(PasswordAlphabet, 24),
            $"Smoke{options.RunId}{index}");
    }

    private async Task RegisterAndLoginAsync(IReadOnlyList<Player> players, CancellationToken cancellationToken)
    {
        foreach (var player in players)
        {
            var registered = await api.RegisterAsync(player.Email, player.Username, player.Password, cancellationToken);
            report.Pass($"register {player.Index}", $"account {registered.Username} with a session of {registered.SessionId.ToString("N")[..8]}");

            var firstSession = await api.ValidateSessionAsync(registered.SessionToken, cancellationToken);
            report.Check($"session {player.Index}", firstSession.IsSuccess, $"validated, answered {(int)firstSession.Status}");

            var login = await api.LoginAsync(player.Username, player.Password, cancellationToken);
            player.Auth = login;
            player.Token = login.SessionToken;
            report.Check(
                $"login {player.Index}",
                login.SessionId != registered.SessionId,
                "a new session replaces the one that registering made");

            var replaced = await api.ValidateSessionAsync(registered.SessionToken, cancellationToken);
            report.Check(
                $"replaced session {player.Index}",
                replaced.Status == HttpStatusCode.Unauthorized,
                $"the old session answers {(int)replaced.Status}");
            var current = await api.ValidateSessionAsync(login.SessionToken, cancellationToken);
            report.Check($"current session {player.Index}", current.IsSuccess, $"answers {(int)current.Status}");
        }
    }

    private async Task CreateCharactersAsync(IReadOnlyList<Player> players, CancellationToken cancellationToken)
    {
        foreach (var player in players)
        {
            player.Character = await api.CreateCharacterAsync(player.Token!, player.CharacterName, cancellationToken);
            var listed = await api.ListCharactersAsync(player.Token!, cancellationToken);
            report.Check(
                $"character {player.Index}",
                listed.Any(character => character.Id == player.Character.Id),
                $"{player.CharacterName} created and listed");
        }
    }

    private async Task CheckShardsAsync(CancellationToken cancellationToken)
    {
        var shards = await api.ListShardsAsync(cancellationToken);
        var shard = shards.FirstOrDefault(candidate => candidate.Id == options.ShardId);
        report.Check(
            "shard online",
            shard is { IsOnline: true, Capacity: > 0 },
            shard is null
                ? $"{options.ShardId} is not listed"
                : $"{shard.Id} on {shard.WorldId}, online {shard.IsOnline}, {shard.ActivePlayers} of {shard.Capacity} players");
        if (shard is not { IsOnline: true })
        {
            throw new SmokeException($"{options.ShardId} is not online, so nobody can join it.");
        }
    }

    private async Task PlayAsync(IReadOnlyList<Player> players, CancellationToken cancellationToken)
    {
        var bots = new List<RealtimeBot>();
        try
        {
            foreach (var player in players)
            {
                player.Join = await api.JoinAsync(player.Token!, options.ShardId, player.Character!.Id, cancellationToken);
                CheckJoin(player);
                var identity = player.Join.ToIdentity(player.Index, player.Auth!.AccountId, player.CharacterName);
                bots.Add(new RealtimeBot(player.Index, player.CharacterName, identity, player.Join.Endpoint.Host, player.Join.Endpoint.UdpPort));
            }

            foreach (var bot in bots)
            {
                bot.Start();
            }

            var joined = await RealtimeBot.PollUntilAsync(
                bots,
                () => bots.All(bot => bot.State == SimulationBotClientState.Joined),
                TimeSpan.FromSeconds(20),
                cancellationToken);
            report.Check(
                "udp join",
                joined,
                string.Join(", ", bots.Select(bot => $"{bot.Name} {bot.State}{FailureText(bot)}")));
            if (!joined)
            {
                return;
            }

            report.Note("join latency " + string.Join(", ", bots.Select(bot => $"{bot.Capture().JoinLatencyMs:0} ms")));

            await WalkAsync(bots, cancellationToken);
            await LeaveAsync(bots, cancellationToken);
        }
        finally
        {
            foreach (var bot in bots)
            {
                bot.Dispose();
            }
        }
    }

    private void CheckJoin(Player player)
    {
        var join = player.Join!;
        report.Check(
            $"join {player.Index}",
            join.CharacterId == player.Character!.Id
                && join.Shard.Id == options.ShardId
                && join.JoinTicketId != Guid.Empty
                && join.TransportKey is { Length: RealtimeTransportKeys.TransportKeySize }
                && !join.IsReconnect,
            $"a ticket and a transport key for {player.CharacterName} on {join.Shard.Id}");
        report.Check(
            $"worker endpoint {player.Index}",
            options.ExpectedEndpoint is null || options.ExpectedEndpoint == $"{join.Endpoint.Host}:{join.Endpoint.UdpPort}",
            $"{join.Endpoint.WorkerId} advertises {join.Endpoint.Host}:{join.Endpoint.UdpPort}");
        report.Check(
            $"worker identity {player.Index}",
            join.Endpoint.WorkerId == options.WorkerId,
            $"the join names the worker {join.Endpoint.WorkerId}");
        report.Check(
            $"protocol version {player.Index}",
            join.Endpoint.ProtocolVersion == RealtimeProtocol.Version,
            $"worker {join.Endpoint.ProtocolVersion}, client library {RealtimeProtocol.Version}");
    }

    private async Task WalkAsync(IReadOnlyList<RealtimeBot> bots, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var nextReport = started + (10 * Stopwatch.Frequency);
        var end = started + (long)(options.Walk.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < end && bots.All(bot => bot.State == SimulationBotClientState.Joined))
        {
            foreach (var bot in bots)
            {
                bot.Poll();
            }

            if (Stopwatch.GetTimestamp() >= nextReport)
            {
                nextReport += 10 * Stopwatch.Frequency;
                report.Note($"walking, {Stopwatch.GetElapsedTime(started).TotalSeconds:0} s: "
                    + string.Join(", ", bots.Select(bot => $"{bot.Name} {bot.Capture().SnapshotsReceived} snapshots, {bot.Walker.FarthestMeters:0.0} m")));
            }

            await Task.Delay(5, cancellationToken);
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        foreach (var bot in bots)
        {
            var snapshot = bot.Capture();
            report.Check(
                $"walk {bot.Index}",
                bot.State == SimulationBotClientState.Joined,
                $"{bot.Name} stayed joined for {elapsed:0} s{FailureText(bot)}");
            report.Check(
                $"movement {bot.Index}",
                bot.Walker.FarthestMeters >= 3d,
                $"the worker moved {bot.Name} {bot.Walker.FarthestMeters:0.0} m from its spawn");
            report.Check(
                $"snapshots {bot.Index}",
                snapshot.SnapshotsReceived >= elapsed * 8,
                $"{snapshot.SnapshotsReceived} snapshots in {elapsed:0} s, about {snapshot.SnapshotsReceived / elapsed:0} per second, {snapshot.EstimatedMissingSnapshots} missed");
        }
    }

    private async Task LeaveAsync(IReadOnlyList<RealtimeBot> bots, CancellationToken cancellationToken)
    {
        foreach (var bot in bots.Where(bot => bot.State == SimulationBotClientState.Joined))
        {
            bot.BeginLeave();
        }

        var left = await RealtimeBot.PollUntilAsync(
            bots,
            () => bots.All(bot => bot.State == SimulationBotClientState.Completed),
            TimeSpan.FromSeconds(20),
            cancellationToken);
        report.Check(
            "udp leave",
            left,
            string.Join(", ", bots.Select(bot => $"{bot.Name} {bot.State}{FailureText(bot)}")));
    }

    private async Task CheckWorkerRoutesAsync(Player player, CancellationToken cancellationToken)
    {
        var path = $"/api/simulation-workers/{Uri.EscapeDataString(options.WorkerId)}/offline";
        var wrongSecret = RandomNumberGenerator.GetString(PasswordAlphabet, 48);

        var none = await api.SendAsync(HttpMethod.Post, path, cancellationToken: cancellationToken);
        report.Check("worker route without credentials", IsRefused(none), Describe(none));

        var wrong = await api.SendAsync(
            HttpMethod.Post,
            path,
            headers: WorkerHeaders(options.WorkerId, wrongSecret),
            cancellationToken: cancellationToken);
        report.Check("worker route with a wrong secret", IsRefused(wrong), Describe(wrong));

        var other = await api.SendAsync(
            HttpMethod.Post,
            path,
            headers: WorkerHeaders("another-worker", wrongSecret),
            cancellationToken: cancellationToken);
        report.Check("worker route of another worker", IsRefused(other), Describe(other));

        var read = await api.SendAsync(
            HttpMethod.Get,
            $"/api/simulation-workers/{Uri.EscapeDataString(options.WorkerId)}/corpses",
            headers: WorkerHeaders(options.WorkerId, wrongSecret),
            cancellationToken: cancellationToken);
        report.Check("worker read with a wrong secret", IsRefused(read), Describe(read));

        var account = await api.SendAsync(HttpMethod.Post, path, bearerToken: player.Token, cancellationToken: cancellationToken);
        report.Check("worker route with a player session", IsRefused(account), Describe(account));

        static bool IsRefused(StackResponse response)
        {
            return response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
        }

        static string Describe(StackResponse response)
        {
            return $"answered {(int)response.Status}{(response.ProblemCode is null ? string.Empty : $" {response.ProblemCode}")}";
        }
    }

    private async Task LogoutAsync(IReadOnlyList<Player> players, CancellationToken cancellationToken)
    {
        foreach (var player in players)
        {
            var logout = await api.LogoutAsync(player.Token!, cancellationToken);
            var after = await api.ValidateSessionAsync(player.Token!, cancellationToken);
            report.Check(
                $"logout {player.Index}",
                logout.IsSuccess && after.Status == HttpStatusCode.Unauthorized,
                $"logout answered {(int)logout.Status}, and the session then answers {(int)after.Status}");
        }
    }

    // Development has routes that issue tickets to bots and seed stress fixtures; a deployment has
    // none of them, so a client that asks gets 404 and no authority decision is ever made.
    private async Task CheckDevelopmentRoutesAsync(CancellationToken cancellationToken)
    {
        var bots = await api.SendAsync(
            HttpMethod.Post,
            "/api/development/simulation-bots/join-tickets",
            new { },
            cancellationToken: cancellationToken);
        var fixtures = await api.SendAsync(
            HttpMethod.Post,
            "/api/development/stack-stress/loot-hotspot",
            new { },
            cancellationToken: cancellationToken);
        report.Check(
            "development routes",
            bots.Status == HttpStatusCode.NotFound && fixtures.Status == HttpStatusCode.NotFound,
            $"the bot route answers {(int)bots.Status} and the fixture route {(int)fixtures.Status}");
    }

    // The proxy appends the address it saw and drops whatever the client wrote, so a client that
    // names another address in X-Forwarded-For stays one client with one rate limit window. Without
    // that, each forged address would start its own window and nothing would ever be limited.
    private async Task CheckForgedAddressAsync(CancellationToken cancellationToken)
    {
        const int Requests = 14;
        var limited = 0;
        StackResponse? limit = null;
        for (var index = 0; index < Requests; index++)
        {
            var forged = $"203.0.113.{index + 1}";
            var response = await api.SendAsync(
                HttpMethod.Post,
                "/api/accounts/register",
                new { },
                headers: new Dictionary<string, string> { ["X-Forwarded-For"] = forged },
                cancellationToken: cancellationToken);
            if (response.Status == HttpStatusCode.TooManyRequests)
            {
                limited++;
                limit ??= response;
            }
        }

        report.Check(
            "forged forwarded address",
            limited > 0,
            $"{limited} of {Requests} requests, each naming another X-Forwarded-For address, were limited as one client");
        report.Check(
            "rate limit answer",
            limit is { ProblemCode: "rate_limit_exceeded", RetryAfter: { } retry } && retry > TimeSpan.Zero,
            limit is null
                ? "no request was limited"
                : $"429 {limit.ProblemCode} with Retry-After {limit.RetryAfter?.TotalSeconds:0} s");
    }

    private static Dictionary<string, string> WorkerHeaders(string workerId, string secret)
    {
        return new Dictionary<string, string>
        {
            ["X-Simulation-Worker-ID"] = workerId,
            ["X-Simulation-Worker-Secret"] = secret
        };
    }

    private static string FailureText(RealtimeBot bot)
    {
        var snapshot = bot.Capture();
        return snapshot.FailureCode is null ? string.Empty : $" (failed with {snapshot.FailureCode})";
    }
}
