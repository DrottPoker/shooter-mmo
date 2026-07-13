namespace WorldServer.Worlds;

public sealed record WorldServerInstanceIdentity(string InstanceId)
{
    public static WorldServerInstanceIdentity Create()
    {
        return new WorldServerInstanceIdentity(Guid.NewGuid().ToString("N"));
    }
}
