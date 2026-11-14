using Mmorpg.GameSimulation;
using Mmorpg.Tools.SimulationBotClient;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Tools.StackStressGenerator;

// Stays in the town and trades at its vendor like a player who sells goods and buys supplies:
// it walks up to the vendor, opens it, makes a few trades at human speed, closes it, and idles
// or strolls through the town before the next visit. A trade buys a few cheap supplies while
// the trader keeps a reserve of currency, sells part of a stack it carries, such as the pelts
// of its kit or what it bought before, or moves an item to another slot. A trader that runs low
// on room sells first. After a death it recovers its corpse; when the corpse is lost it goes on
// as a walker.
public sealed class TraderRole : PlayingRoleBase
{
    public const int MinimumTradesPerVisit = 2;
    public const int MaximumTradesPerVisit = 5;

    // The currency a trader keeps; it buys only what leaves at least this much.
    public const long CurrencyReserve = 20;

    public static readonly TimeSpan MinimumTradeGap = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan MaximumTradeGap = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan MinimumIdle = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan MaximumIdle = TimeSpan.FromSeconds(25);

    // The share of idle spells a trader strolls through the town instead of standing.
    public const double StrollChance = 0.5;

    // The supplies a trader buys, with the most it buys at once.
    private static readonly (string DefinitionId, int MaximumQuantity)[] Supplies =
    [
        ("ammunition.bronze_arrow", 20),
        ("food.cooked_trout", 2)
    ];

    private TraderState state = TraderState.Idle;
    private TimeSpan idleUntil;
    private PlayingVendorVisit? visit;
    private PlayingCorpseRecovery? recovery;
    private WalkerRole? walker;
    private int plannedTrades;
    private int tradesPlanned;

    public TraderRole(PlayingGround home, bool teleport, Random random)
        : base(home ?? throw new ArgumentNullException(nameof(home)), teleport, random, home)
    {
    }

    private enum TraderState
    {
        Idle,
        Visit,
        Dead
    }

    public override PlayingRole Role => PlayingRole.Trader;

    public override string State => walker is not null
        ? "walker"
        : state == TraderState.Visit && visit is not null ? "visit-" + visit.State : state.ToString().ToLowerInvariant();

    public bool ContinuesAsWalker => walker is not null;

    protected override void OnOutcome(IPlayingBotBody body, PlayingIntentOutcome outcome, TimeSpan now)
    {
        if (outcome.Operation == PlayingOperations.Teleport)
        {
            OnTeleportOutcome(outcome);
        }
        else if (recovery is not null && PlayingCorpseRecovery.Owns(outcome.Operation))
        {
            recovery.OnOutcome(body, outcome, now);
        }
        else if (visit is not null && PlayingVendorVisit.Owns(outcome.Operation))
        {
            visit.OnOutcome(body, outcome, now);
        }
    }

    protected override void Act(IPlayingBotBody body, TimeSpan now)
    {
        if (walker is not null)
        {
            walker.Think(body, now);
            return;
        }

        if (IsDead(body) && state != TraderState.Dead)
        {
            recovery = new PlayingCorpseRecovery(body.Position, body.View.OwnCombat!.LifeSequence);
            visit = null;
            ForgetTravel();
            state = TraderState.Dead;
            return;
        }

        switch (state)
        {
            case TraderState.Idle:
                if (now >= idleUntil)
                {
                    StartVisit(body);
                }

                break;
            case TraderState.Visit:
                ContinueVisit(body, now);
                break;
            case TraderState.Dead:
                Recover(body, now);
                break;
        }
    }

    private void StartVisit(IPlayingBotBody body)
    {
        body.Stand();
        ForgetTravel();
        plannedTrades = Random.Next(MinimumTradesPerVisit, MaximumTradesPerVisit + 1);
        tradesPlanned = 0;
        visit = new PlayingVendorVisit(Home!.Area, PlanTrade, Random, MinimumTradeGap, MaximumTradeGap);
        state = TraderState.Visit;
    }

    private void ContinueVisit(IPlayingBotBody body, TimeSpan now)
    {
        var status = visit!.Think(body, now, (goal, arrival) => TravelTo(body, goal, arrival));
        if (status == PlayingVisitStatus.Running)
        {
            return;
        }

        if (status == PlayingVisitStatus.Failed)
        {
            body.Count(PlayingCounter.VendorTripsFailed);
        }

        visit = null;
        ForgetTravel();
        state = TraderState.Idle;
        idleUntil = now + RandomBetween(MinimumIdle, MaximumIdle);
        if (Random.NextDouble() < StrollChance && RandomGroundIn(body, Home!.Area) is { } point)
        {
            body.WalkTo(point);
        }
    }

    private void Recover(IPlayingBotBody body, TimeSpan now)
    {
        switch (recovery!.Think(body, now, (goal, arrival) => TravelTo(body, goal, arrival)))
        {
            case PlayingVisitStatus.Done:
                recovery = null;
                ForgetTravel();
                state = TraderState.Idle;
                idleUntil = now;
                break;
            case PlayingVisitStatus.Failed:
                recovery = null;
                body.Count(PlayingCounter.CorpsesLost);
                body.Stand();
                walker = new WalkerRole(null, teleport: false, Random);
                break;
        }
    }

    // The next trade of the visit, from what the trader owns now.
    private PlayingTrade? PlanTrade(IPlayingBotBody body, VendorProfileDefinition vendor, PlayingVendorVisit current)
    {
        if (tradesPlanned >= plannedTrades)
        {
            return null;
        }

        tradesPlanned++;
        if (NeedsToSell(body))
        {
            return PlanSale(body, vendor, current) ?? PlanMove(body, current);
        }

        var roll = Random.NextDouble();
        if (roll < 0.4)
        {
            return (PlayingTrade?)PlanPurchase(body, vendor, current) ?? PlanSale(body, vendor, current) ?? PlanMove(body, current);
        }

        if (roll < 0.75)
        {
            return PlanSale(body, vendor, current) ?? PlanMove(body, current);
        }

        return PlanMove(body, current) ?? PlanSale(body, vendor, current);
    }

    protected override PlayingPurchase? PlanPurchase(IPlayingBotBody body, VendorProfileDefinition vendor, PlayingVendorVisit visit)
    {
        var (definitionId, maximum) = Supplies[Random.Next(Supplies.Length)];
        if (visit.HasFailed(definitionId))
        {
            return null;
        }

        var wanted = Random.Next(1, maximum + 1);
        var quantity = PlayingVendorVisit.AffordableQuantity(body, vendor, definitionId, wanted);
        while (quantity > 0
            && body.GameData.PurchasePrice(vendor, definitionId, quantity) is { } price
            && body.View.Items.Currency - price < CurrencyReserve)
        {
            quantity--;
        }

        return PlayingVendorVisit.PurchaseOf(body, vendor, definitionId, quantity);
    }

    // Part of a random stack the vendor pays for.
    private PlayingTrade? PlanSale(IPlayingBotBody body, VendorProfileDefinition vendor, PlayingVendorVisit visit)
    {
        var candidates = body.View.Items.Items
            .Where(item => !item.IsEquipped && !visit.HasFailed(item))
            .OrderBy(item => item.SlotIndex)
            .ToArray();
        for (var attempt = 0; attempt < candidates.Length; attempt++)
        {
            var item = candidates[(Random.Next(candidates.Length) + attempt) % candidates.Length];
            var quantity = Math.Min(item.Quantity, Random.Next(1, 4));
            for (; quantity <= item.Quantity; quantity++)
            {
                if (PlayingVendorVisit.SaleOf(body, vendor, item, quantity) is { } sale)
                {
                    return sale;
                }

                // A single cheap item sells for nothing, so the trader sells a few at once.
                if (quantity >= 20)
                {
                    break;
                }
            }
        }

        return null;
    }

    // A move of a random carried item to a free slot of the permanent inventory.
    private PlayingTrade? PlanMove(IPlayingBotBody body, PlayingVendorVisit visit)
    {
        var items = body.View.Items;
        var slot = items.FindFreeSlot(items.PermanentInventoryId);
        var candidates = items.Items
            .Where(item => !item.IsEquipped && !visit.HasFailed(item))
            .ToArray();
        if (slot < 0 || candidates.Length == 0)
        {
            return null;
        }

        return new PlayingItemMove(candidates[Random.Next(candidates.Length)], items.PermanentInventoryId, slot);
    }
}
