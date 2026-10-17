namespace Mmorpg.GameService.Items.Transactions.Domain;

// Validation of corpse presentation and transforms.
internal static class CorpseTransformRules
{
    public const string PlayerCorpsePresentationKey = "corpse.remains";

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
