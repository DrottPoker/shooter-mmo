using SimulationWorker.Config;

namespace SimulationWorker.Items;

public sealed class ItemInteractionAccessService
{
    private readonly SimulationWorkerConfig config;
    private readonly DevelopmentItemInteractionOptions developmentOptions;

    public ItemInteractionAccessService(SimulationWorkerConfig config)
        : this(config, DevelopmentItemInteractionOptions.Disabled)
    {
    }

    public ItemInteractionAccessService(
        SimulationWorkerConfig config,
        DevelopmentItemInteractionOptions developmentOptions)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.developmentOptions = developmentOptions
            ?? throw new ArgumentNullException(nameof(developmentOptions));
    }

    public ItemInteractionAccess Evaluate(float positionX, float positionY, float positionZ)
    {
        var bank = developmentOptions.GlobalBankAndRecoveryAccess;
        var recoveryStorage = developmentOptions.GlobalBankAndRecoveryAccess;
        var insuranceNpc = false;
        foreach (var point in config.ItemInteraction.ServicePoints)
        {
            if (!IsWithin(point, positionX, positionY, positionZ))
            {
                continue;
            }

            switch (point.Kind)
            {
                case ItemServiceKind.Bank:
                    bank = true;
                    break;
                case ItemServiceKind.RecoveryStorage:
                    recoveryStorage = true;
                    break;
                case ItemServiceKind.InsuranceNpc:
                    insuranceNpc = true;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported item service kind '{point.Kind}'.");
            }
        }

        return new ItemInteractionAccess(bank, recoveryStorage, insuranceNpc);
    }

    private static bool IsWithin(
        ItemServicePointConfig point,
        float positionX,
        float positionY,
        float positionZ)
    {
        var deltaX = positionX - point.X;
        var deltaY = positionY - point.Y;
        var deltaZ = positionZ - point.Z;
        return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ)
            <= point.Radius * point.Radius;
    }
}

public sealed record ItemInteractionAccess(
    bool Bank,
    bool RecoveryStorage,
    bool InsuranceNpc);
