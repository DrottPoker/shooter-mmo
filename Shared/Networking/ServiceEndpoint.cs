namespace ShooterMmo.Shared.Networking;

public sealed record ServiceEndpoint(string Host, int Port)
{
    public string Target => $"{Host}:{Port}";
}

