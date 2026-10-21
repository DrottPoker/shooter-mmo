namespace Mmorpg.GameService.Items.Transactions.Domain;

// Validation of corpse presentation and transforms.
internal static class CorpseTransformRules
{
    // The client lays a player corpse down as the body of the character.
    public const string PlayerCorpsePresentationKey = "corpse.remains";

    // A Mob corpse keeps the generic remains until Mobs have models.
    public const string MobCorpsePresentationKey = "corpse.mob";

    // A player corpse keeps its full lifetime even when empty. The ck_corpses_expiry CHECK in
    // PlayerCorpsePersistenceMigration enforces the same lifetime in the database.
    public static readonly TimeSpan PlayerCorpseLifetime = TimeSpan.FromMinutes(5);

    public static bool IsFinitePosition(double value)
    {
        return double.IsFinite(value) && value is >= -1_000_000d and <= 1_000_000d;
    }

    public static bool IsNormalizedRotation(
        double rotationX,
        double rotationY,
        double rotationZ,
        double rotationW)
    {
        var lengthSquared = (rotationX * rotationX)
            + (rotationY * rotationY)
            + (rotationZ * rotationZ)
            + (rotationW * rotationW);
        return double.IsFinite(lengthSquared)
            && Math.Abs(lengthSquared - 1d) <= 0.0001d
            && Math.Abs(rotationX) <= 1d
            && Math.Abs(rotationY) <= 1d
            && Math.Abs(rotationZ) <= 1d
            && Math.Abs(rotationW) <= 1d;
    }
}
