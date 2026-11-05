namespace Mmorpg.GameService.Items;

// The insurance price, until pricing content exists. It comes from configuration; nothing is
// defaulted in code.
public sealed record NpcItemLifecycleOptions(long InsurancePrice)
{
    public static NpcItemLifecycleOptions FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var insurancePrice = configuration.GetSection("Items:NpcLifecycle").GetValue<long?>("InsurancePrice");
        if (insurancePrice is not > 0)
        {
            throw new InvalidOperationException("Items:NpcLifecycle must define a positive insurance price.");
        }

        return new NpcItemLifecycleOptions(insurancePrice.Value);
    }
}
