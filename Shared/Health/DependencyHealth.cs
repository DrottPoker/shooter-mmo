namespace ShooterMmo.Shared.Health;

public sealed record DependencyHealth(
    string Name,
    string Target,
    bool IsReachable,
    string? Error);

