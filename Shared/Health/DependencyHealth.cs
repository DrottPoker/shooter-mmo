namespace Mmorpg.Server.Common.Health;

public sealed record DependencyHealth(
    string Name,
    string Target,
    bool IsReachable,
    string? Error);

