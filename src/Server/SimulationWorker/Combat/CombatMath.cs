using Mmorpg.WorldData.Combat;

namespace Mmorpg.SimulationWorker.Combat;

// The one random source of combat. A worker runtime owns one, and tests inject a fixed
// sequence, so every roll is reproducible.
public interface ICombatRandom
{
    // A uniformly distributed integer between both bounds, inclusive.
    int NextInclusive(int minimum, int maximum);
}

public sealed class SeededCombatRandom(int seed) : ICombatRandom
{
    private readonly Random random = new(seed);

    public int NextInclusive(int minimum, int maximum)
    {
        if (maximum < minimum)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        return minimum == maximum
            ? minimum
            : (int)random.NextInt64(minimum, (long)maximum + 1);
    }
}

public readonly record struct CombatDamage(int Amount, string DamageType);

// Damage in the combat design's order. Only the first two steps exist yet: the base roll
// of the weapon or Mob attack, then the ammunition bonus. The combat triangle, armor, and
// elemental modifiers add their steps here without touching timing or replication.
public static class CombatDamageCalculator
{
    public static CombatDamage Calculate(
        int minimumDamage,
        int maximumDamage,
        string damageType,
        int ammunitionBonusPercent,
        ICombatRandom random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (!CombatContentRules.IsValidDamageRange(minimumDamage, maximumDamage)
            || ammunitionBonusPercent < 0
            || ammunitionBonusPercent > CombatContentRules.MaximumAmmunitionDamageBonusPercent
            || !DamageTypeIds.IsKnown(damageType))
        {
            throw new ArgumentException("The attack has invalid damage data.");
        }

        var roll = random.NextInclusive(minimumDamage, maximumDamage);
        var bonus = ((long)roll * ammunitionBonusPercent + 50) / 100;
        return new CombatDamage((int)Math.Min(int.MaxValue, roll + bonus), damageType);
    }
}

// Converts content seconds to whole simulation ticks once.
public static class CombatTicks
{
    // An interval such as 2.4 s is 72 ticks at 30 Hz; rounding hides float error.
    public static uint FromSeconds(float seconds, int tickRateHz)
    {
        if (!CombatContentRules.IsFinite(seconds) || seconds < 0f || tickRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        return (uint)Math.Round((double)seconds * tickRateHz, MidpointRounding.AwayFromZero);
    }

    // A projectile always needs at least one tick, so it never hits in its release tick.
    public static uint FlightTicks(float distance, float projectileSpeed, int tickRateHz)
    {
        if (projectileSpeed <= 0f || tickRateHz <= 0)
        {
            return 0;
        }

        var ticks = Math.Ceiling(Math.Max(0d, distance) / projectileSpeed * tickRateHz);
        return (uint)Math.Max(1d, Math.Min(ticks, tickRateHz * 60d));
    }

    // Tick comparisons stay correct across the wrap of the 32-bit server tick.
    public static bool IsReached(uint now, uint due)
    {
        return unchecked((int)(now - due)) >= 0;
    }
}
