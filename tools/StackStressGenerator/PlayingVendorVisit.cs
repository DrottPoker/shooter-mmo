using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Tools.SimulationBotClient;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Tools.StackStressGenerator;

// A trade a bot makes at an open vendor, priced from the catalog as a Unity client prices it.
public abstract record PlayingTrade;

public sealed record PlayingSale(SimulationBotOwnedItem Item, int Quantity, long Price) : PlayingTrade;

public sealed record PlayingPurchase(string OfferDefinitionId, int Quantity, long Price) : PlayingTrade;

// A move of a carried item to another slot, which a trader makes between its trades.
public sealed record PlayingItemMove(SimulationBotOwnedItem Item, Guid ContainerId, int SlotIndex) : PlayingTrade;

public enum PlayingVisitStatus
{
    Running,
    Done,
    Failed
}

// One visit at the town's vendor, as a player makes it: walk to the town, find the NPC whose
// menu offers the vendor, stand within reach of it with a clear line of sight, open it, make
// the trades the caller plans one after another at human speed, and close it. The caller plans
// every next trade from the bot's current items and currency, so a trade always fits what the
// bot owns. A fighter's visit counts the arrows it buys; a trader's supplies are no restock.
public sealed class PlayingVendorVisit
{
    // The vendor counts for the town this far outside its rectangle.
    public const float TownMargin = 20f;

    // The bot stands this close to the vendor's interaction box, inside the worker's start
    // range of 3 m.
    private const float MinimumReach = 0.8f;
    private const float MaximumReach = 2.2f;
    private const float StandArrival = 0.6f;
    private const int MaximumStandAttempts = 4;
    private const int MaximumReopens = 2;

    private readonly PlayingRectangle town;
    private readonly Func<IPlayingBotBody, VendorProfileDefinition, PlayingVendorVisit, PlayingTrade?> nextTrade;
    private readonly HashSet<Guid> failedItems = [];
    private readonly HashSet<string> failedOffers = new(StringComparer.Ordinal);
    private readonly Random random;
    private readonly TimeSpan minimumSpacing;
    private readonly TimeSpan maximumSpacing;
    private readonly bool countsAmmunition;
    private VisitState state = VisitState.FindVendor;
    private ulong vendorEntity;
    private SimulationVector3 standPoint;
    private int standAttempts;
    private int reopens;
    private TimeSpan nextTradeAt;
    private PlayingTrade? inFlight;
    private bool awaiting;
    private bool closingOther;

    public PlayingVendorVisit(
        PlayingRectangle town,
        Func<IPlayingBotBody, VendorProfileDefinition, PlayingVendorVisit, PlayingTrade?> nextTrade,
        Random random,
        TimeSpan minimumSpacing,
        TimeSpan maximumSpacing,
        bool countsAmmunition = false)
    {
        this.town = town;
        this.countsAmmunition = countsAmmunition;
        this.nextTrade = nextTrade ?? throw new ArgumentNullException(nameof(nextTrade));
        this.random = random ?? throw new ArgumentNullException(nameof(random));
        this.minimumSpacing = minimumSpacing;
        this.maximumSpacing = maximumSpacing;
    }

    private enum VisitState
    {
        FindVendor,
        Approach,
        Opening,
        Trading,
        Closing
    }

    public string State => state.ToString().ToLowerInvariant();

    // Whether a trade of the item or offer was rejected during this visit, so the planner
    // does not offer it again.
    public bool HasFailed(SimulationBotOwnedItem item)
    {
        return failedItems.Contains(item.ItemInstanceId);
    }

    public bool HasFailed(string offerDefinitionId)
    {
        return failedOffers.Contains(offerDefinitionId);
    }

    public static bool Owns(string operation)
    {
        return operation is PlayingOperations.VendorOpen
            or PlayingOperations.VendorSale
            or PlayingOperations.VendorPurchase
            or PlayingOperations.VendorClose
            or PlayingOperations.ItemMove;
    }

    // The vendor NPC in view nearest the town's center, or null.
    public static SimulationBotWorldActor? FindVendor(IPlayingBotBody body, PlayingRectangle town)
    {
        ArgumentNullException.ThrowIfNull(body);
        SimulationBotWorldActor? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var actor in body.View.Actors.Values)
        {
            if (actor.Kind != RealtimeWorldActorKind.Npc
                || !actor.IsActive
                || !town.Contains(actor.Position.X, actor.Position.Z, TownMargin)
                || !actor.MenuOptions.Any(option => option.Kind == RealtimeWorldActorCapabilityKind.Vendor))
            {
                continue;
            }

            var dx = actor.Position.X - town.CenterX;
            var dz = actor.Position.Z - town.CenterZ;
            var distance = (dx * dx) + (dz * dz);
            if (distance < nearestDistance)
            {
                nearest = actor;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    // A point with walkable ground from which a character reaches an actor's interaction box
    // and sees its center, or null after a few tries. The first try lies on the side the bot
    // comes from.
    public static SimulationVector3? FindStandPoint(
        IPlayingBotBody body,
        SimulationBotWorldActor actor,
        Random random,
        float minimumReach,
        float maximumReach)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(actor);
        var bounds = body.GameData.InteractionBounds(actor.ActorDefinitionId);
        var halfSize = bounds is null ? 0.5f : Math.Max(bounds.SizeX, bounds.SizeZ) * 0.5f;
        var center = BoundsCenter(body, actor);
        var towardBot = MathF.Atan2(body.Position.X - center.X, body.Position.Z - center.Z);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var angle = attempt == 0 ? towardBot : (float)(random.NextDouble() * Math.PI * 2d);
            var reach = halfSize + minimumReach + ((maximumReach - minimumReach) * (float)random.NextDouble());
            var x = center.X + (MathF.Sin(angle) * reach);
            var z = center.Z + (MathF.Cos(angle) * reach);
            if (body.TryFindGround(x, z, out var ground) && body.HasLineOfSight(ground, center))
            {
                return ground;
            }
        }

        return null;
    }

    // The center of an actor's interaction box, which range and line of sight measure to.
    public static SimulationVector3 BoundsCenter(IPlayingBotBody body, SimulationBotWorldActor actor)
    {
        var bounds = body.GameData.InteractionBounds(actor.ActorDefinitionId);
        return new SimulationVector3(
            actor.Position.X + (bounds?.CenterX ?? 0f),
            actor.Position.Y + (bounds?.CenterY ?? 1f),
            actor.Position.Z + (bounds?.CenterZ ?? 0f));
    }

    // Whether the bot stands within a range of an actor's interaction box.
    public static bool IsWithinReach(IPlayingBotBody body, SimulationBotWorldActor actor, float range)
    {
        var bounds = body.GameData.InteractionBounds(actor.ActorDefinitionId);
        var center = BoundsCenter(body, actor);
        var position = body.Position;
        return WorldInteractionRules.DistanceSquaredToBounds(
                position.X,
                position.Y,
                position.Z,
                center.X,
                center.Y,
                center.Z,
                bounds?.SizeX ?? 1f,
                bounds?.SizeY ?? 2f,
                bounds?.SizeZ ?? 1f)
            <= range * range;
    }

    public void OnOutcome(IPlayingBotBody body, PlayingIntentOutcome outcome, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(outcome);
        awaiting = false;
        switch (outcome.Operation)
        {
            case PlayingOperations.VendorOpen:
                if (outcome.Succeeded)
                {
                    state = VisitState.Trading;
                    nextTradeAt = now + Spacing();
                }
                else
                {
                    state = VisitState.FindVendor;
                }

                break;
            case PlayingOperations.VendorSale:
            case PlayingOperations.VendorPurchase:
            case PlayingOperations.ItemMove:
                if (outcome.Succeeded && inFlight is not null)
                {
                    Record(body, inFlight);
                }
                else if (!outcome.Succeeded)
                {
                    switch (inFlight)
                    {
                        case PlayingSale sale:
                            failedItems.Add(sale.Item.ItemInstanceId);
                            break;
                        case PlayingItemMove move:
                            failedItems.Add(move.Item.ItemInstanceId);
                            break;
                        case PlayingPurchase purchase:
                            failedOffers.Add(purchase.OfferDefinitionId);
                            break;
                    }
                }

                inFlight = null;
                nextTradeAt = now + Spacing();
                break;
            case PlayingOperations.VendorClose:
                // Closing what else was open lets the bot open the vendor next.
                state = closingOther ? VisitState.Approach : VisitState.Closing;
                closingOther = false;
                break;
        }
    }

    public PlayingVisitStatus Think(
        IPlayingBotBody body,
        TimeSpan now,
        Func<SimulationVector3, float, bool?> travel)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(travel);
        if (awaiting)
        {
            return PlayingVisitStatus.Running;
        }

        switch (state)
        {
            case VisitState.FindVendor:
                return FindVendor(body, travel);
            case VisitState.Approach:
                return Approach(body, travel);
            case VisitState.Opening:
                return PlayingVisitStatus.Running;
            case VisitState.Trading:
                return Trade(body, now);
            default:
                return Close(body);
        }
    }

    private PlayingVisitStatus FindVendor(IPlayingBotBody body, Func<SimulationVector3, float, bool?> travel)
    {
        if (FindVendor(body, town) is { } vendor)
        {
            vendorEntity = vendor.EntityId;
            return PickStandPoint(body, vendor);
        }

        // The vendor is out of the bot's interest until it nears the town.
        var center = new SimulationVector3(town.CenterX, body.Position.Y, town.CenterZ);
        if (body.TryFindGround(town.CenterX, town.CenterZ, out var ground))
        {
            center = ground;
        }

        return travel(center, 4f) switch
        {
            false => PlayingVisitStatus.Running,
            _ => PlayingVisitStatus.Failed
        };
    }

    private PlayingVisitStatus PickStandPoint(IPlayingBotBody body, SimulationBotWorldActor vendor)
    {
        if (++standAttempts > MaximumStandAttempts
            || FindStandPoint(body, vendor, random, MinimumReach, MaximumReach) is not { } point)
        {
            return PlayingVisitStatus.Failed;
        }

        standPoint = point;
        state = VisitState.Approach;
        return PlayingVisitStatus.Running;
    }

    private PlayingVisitStatus Approach(IPlayingBotBody body, Func<SimulationVector3, float, bool?> travel)
    {
        if (!body.View.Actors.TryGetValue(vendorEntity, out var vendor))
        {
            state = VisitState.FindVendor;
            return PlayingVisitStatus.Running;
        }

        switch (travel(standPoint, StandArrival))
        {
            case false:
                return PlayingVisitStatus.Running;
            case null:
                return PickStandPoint(body, vendor);
        }

        body.Stand();
        if (!IsWithinReach(body, vendor, WorldInteractionRules.AuthoritativeStartRange - 0.2f))
        {
            return PickStandPoint(body, vendor);
        }

        // One interaction at a time: whatever else is open closes first.
        if (body.View.OpenInteraction is { } open)
        {
            if (open.TargetEntityId == vendorEntity)
            {
                state = VisitState.Trading;
                return PlayingVisitStatus.Running;
            }

            awaiting = body.CloseInteraction(PlayingOperations.VendorClose);
            closingOther = awaiting;
            return PlayingVisitStatus.Running;
        }

        if (body.OpenInteraction(vendor, PlayingOperations.VendorOpen))
        {
            awaiting = true;
            state = VisitState.Opening;
        }

        return PlayingVisitStatus.Running;
    }

    private PlayingVisitStatus Trade(IPlayingBotBody body, TimeSpan now)
    {
        if (body.View.OpenInteraction is not { } open || open.TargetEntityId != vendorEntity)
        {
            // The worker closed the vendor, such as when a Mob pushed the bot out of reach.
            if (++reopens > MaximumReopens)
            {
                return PlayingVisitStatus.Done;
            }

            state = VisitState.Approach;
            return PlayingVisitStatus.Running;
        }

        if (now < nextTradeAt)
        {
            return PlayingVisitStatus.Running;
        }

        var capability = open.Capabilities.FirstOrDefault(value => value.Kind == RealtimeWorldActorCapabilityKind.Vendor);
        var vendor = capability is null ? null : body.GameData.Vendor(capability.ProfileId);
        var trade = vendor is null ? null : nextTrade(body, vendor, this);
        if (trade is null)
        {
            state = VisitState.Closing;
            return Close(body);
        }

        var sent = trade switch
        {
            PlayingSale sale => body.CapabilityAction(
                PlayingOperations.VendorSale,
                new PlayingCapabilityAction(
                    RealtimeWorldActorCapabilityKind.Vendor,
                    RealtimeCapabilityActionKind.VendorSale,
                    Item: sale.Item,
                    Quantity: sale.Quantity,
                    ExpectedPrice: sale.Price)),
            PlayingPurchase purchase => body.CapabilityAction(
                PlayingOperations.VendorPurchase,
                new PlayingCapabilityAction(
                    RealtimeWorldActorCapabilityKind.Vendor,
                    RealtimeCapabilityActionKind.VendorPurchase,
                    OfferDefinitionId: purchase.OfferDefinitionId,
                    Quantity: purchase.Quantity,
                    ExpectedPrice: purchase.Price)),
            PlayingItemMove move => body.MoveItem(move.Item, move.ContainerId, move.SlotIndex),
            _ => false
        };
        if (sent)
        {
            inFlight = trade;
            awaiting = true;
        }
        else
        {
            nextTradeAt = now + Spacing();
        }

        return PlayingVisitStatus.Running;
    }

    private PlayingVisitStatus Close(IPlayingBotBody body)
    {
        if (body.View.OpenInteraction is not { } open || open.TargetEntityId != vendorEntity)
        {
            return PlayingVisitStatus.Done;
        }

        if (body.CloseInteraction(PlayingOperations.VendorClose))
        {
            awaiting = true;
        }

        return PlayingVisitStatus.Running;
    }

    private void Record(IPlayingBotBody body, PlayingTrade trade)
    {
        switch (trade)
        {
            case PlayingSale sale:
                body.Count(PlayingCounter.VendorSales);
                body.Count(PlayingCounter.CurrencyEarned, sale.Price);
                break;
            case PlayingPurchase purchase:
                body.Count(PlayingCounter.VendorPurchases);
                body.Count(PlayingCounter.CurrencySpent, purchase.Price);
                if (countsAmmunition && body.GameData.Item(purchase.OfferDefinitionId)?.Ammunition is not null)
                {
                    body.Count(PlayingCounter.ArrowsBought, purchase.Quantity);
                }

                break;
            case PlayingItemMove:
                body.Count(PlayingCounter.TraderItemMoves);
                break;
        }
    }

    private TimeSpan Spacing()
    {
        return minimumSpacing + TimeSpan.FromTicks((long)((maximumSpacing - minimumSpacing).Ticks * random.NextDouble()));
    }

    // The sale of a whole stack, or null when the vendor buys nothing of the kind or pays
    // nothing for so few.
    public static PlayingSale? SaleOf(IPlayingBotBody body, VendorProfileDefinition vendor, SimulationBotOwnedItem item, int quantity)
    {
        return body.GameData.SalePrice(vendor, item.DefinitionId, quantity) is { } price
            ? new PlayingSale(item, quantity, price)
            : null;
    }

    // The purchase of a quantity of an offer, or null when the vendor does not sell it or the
    // bot cannot pay for it.
    public static PlayingPurchase? PurchaseOf(IPlayingBotBody body, VendorProfileDefinition vendor, string definitionId, int quantity)
    {
        return quantity > 0
            && body.GameData.PurchasePrice(vendor, definitionId, quantity) is { } price
            && price <= body.View.Items.Currency
                ? new PlayingPurchase(definitionId, quantity, price)
                : null;
    }

    // The most of an offer the bot can pay for, up to a quantity.
    public static int AffordableQuantity(IPlayingBotBody body, VendorProfileDefinition vendor, string definitionId, int wanted)
    {
        var item = body.GameData.Item(definitionId);
        if (item is null || wanted <= 0 || !VendorPricing.Offers(vendor, definitionId))
        {
            return 0;
        }

        var low = 0;
        var high = Math.Min(wanted, Math.Max(1, item.MaximumStackSize));
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (body.GameData.PurchasePrice(vendor, definitionId, middle) is { } price && price <= body.View.Items.Currency)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }
}
