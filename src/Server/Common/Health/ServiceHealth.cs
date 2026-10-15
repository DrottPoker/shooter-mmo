namespace Mmorpg.Server.Common.Health;

public sealed record ServiceHealth(
    string Service,
    string Status,
    DateTimeOffset CheckedAt,
    IReadOnlyCollection<DependencyHealth> Dependencies);

