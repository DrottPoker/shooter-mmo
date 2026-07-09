namespace ShooterMmo.Shared.Health;

public sealed record ServiceHealth(
    string Service,
    string Status,
    DateTimeOffset CheckedAt,
    IReadOnlyCollection<DependencyHealth> Dependencies);

