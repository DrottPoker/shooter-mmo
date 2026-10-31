using Mmorpg.SimulationWorker.Items;
using Mmorpg.WorldData.Combat;
using Mmorpg.WorldData.Items;

namespace Mmorpg.SimulationWorker.Combat;

public enum CombatLoadoutStatus
{
    Ready,
    WeaponMissing,
    AmmunitionMissing,
    AmmunitionIncompatible
}

// One attack a combatant can release: its damage roll, interval, range, and, for a
// projectile, its speed. A Ranged attack also names the ammunition family it fires, and a
// Magic attack the mana it costs. An attack releases WindupTicks after it starts, when the
// body's attack clip strikes or looses; 0 releases at once.
public sealed record CombatAttackProfile(
    string SourceId,
    string CombatStyle,
    string DamageType,
    int MinimumDamage,
    int MaximumDamage,
    uint IntervalTicks,
    float Range,
    float ProjectileSpeed,
    string? AmmunitionFamily,
    int ManaCost,
    uint WindupTicks = 0)
{
    public bool ConsumesAmmunition => AmmunitionFamily is not null;

    public bool CostsMana => ManaCost > 0;
}

// The equipped ammunition stack as GameService last committed it.
public sealed record EquippedAmmunition(
    Guid ItemInstanceId,
    string DefinitionId,
    string AmmunitionFamily,
    int DamageBonusPercent,
    int Quantity,
    long Revision);

public sealed record PlayerCombatLoadout(
    CombatLoadoutStatus Status,
    CombatAttackProfile? Attack,
    EquippedAmmunition? Ammunition);

// Reads a character's attack from its committed equipment and the item catalog the
// worker loaded. Unknown definitions count as missing; the catalog lockstep with
// GameService makes them impossible in practice.
public sealed class CombatItemCatalog
{
    private readonly Dictionary<string, ItemDefinition> definitions;
    private readonly Dictionary<string, ItemWeaponFamilyEntry> weaponFamilies;
    private readonly int tickRateHz;

    public CombatItemCatalog(ItemCatalogRuntimeDocument catalog, int tickRateHz)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (tickRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tickRateHz));
        }

        definitions = (catalog.Definitions ?? Array.Empty<ItemDefinition>())
            .ToDictionary(definition => definition.Id, StringComparer.Ordinal);
        weaponFamilies = (catalog.WeaponFamilies ?? Array.Empty<ItemWeaponFamilyEntry>())
            .ToDictionary(family => family.Id, StringComparer.Ordinal);
        this.tickRateHz = tickRateHz;
    }

    public PlayerCombatLoadout Resolve(CharacterItemState itemState)
    {
        ArgumentNullException.ThrowIfNull(itemState);
        var ammunition = ResolveAmmunition(itemState);
        var weaponItem = itemState.FindEquipped(ItemEquipmentSlotIds.MainHand);
        if (weaponItem is null
            || !definitions.TryGetValue(weaponItem.DefinitionId, out var weapon)
            || weapon.Weapon is null
            || !weaponFamilies.TryGetValue(weapon.Weapon.WeaponFamily, out var family))
        {
            return new PlayerCombatLoadout(CombatLoadoutStatus.WeaponMissing, null, ammunition);
        }

        var ranged = string.Equals(family.CombatStyle, CombatStyleIds.Ranged, StringComparison.Ordinal);
        var magic = string.Equals(family.CombatStyle, CombatStyleIds.Magic, StringComparison.Ordinal);
        var attack = new CombatAttackProfile(
            weapon.Id,
            family.CombatStyle,
            weapon.Weapon.DamageType,
            weapon.Weapon.MinimumDamage,
            weapon.Weapon.MaximumDamage,
            Math.Max(1u, CombatTicks.FromSeconds(weapon.Weapon.AttackIntervalSeconds, tickRateHz)),
            weapon.Weapon.Range,
            Math.Max(0f, family.ProjectileSpeed),
            ranged && !string.IsNullOrEmpty(family.AmmunitionFamily) ? family.AmmunitionFamily : null,
            magic ? Math.Max(0, weapon.Weapon.ManaCost) : 0,
            family.WindupSeconds > 0f ? CombatTicks.FromSeconds(family.WindupSeconds, tickRateHz) : 0u);
        if (!attack.ConsumesAmmunition)
        {
            return new PlayerCombatLoadout(CombatLoadoutStatus.Ready, attack, ammunition);
        }

        if (ammunition is null || ammunition.Quantity <= 0)
        {
            return new PlayerCombatLoadout(CombatLoadoutStatus.AmmunitionMissing, attack, ammunition);
        }

        return string.Equals(ammunition.AmmunitionFamily, attack.AmmunitionFamily, StringComparison.Ordinal)
            ? new PlayerCombatLoadout(CombatLoadoutStatus.Ready, attack, ammunition)
            : new PlayerCombatLoadout(CombatLoadoutStatus.AmmunitionIncompatible, attack, ammunition);
    }

    private EquippedAmmunition? ResolveAmmunition(CharacterItemState itemState)
    {
        var item = itemState.FindEquipped(ItemEquipmentSlotIds.Ammunition);
        if (item is null
            || !definitions.TryGetValue(item.DefinitionId, out var definition)
            || definition.Ammunition is null)
        {
            return null;
        }

        return new EquippedAmmunition(
            item.ItemInstanceId,
            item.DefinitionId,
            definition.Ammunition.AmmunitionFamily,
            Math.Max(0, definition.Ammunition.DamageBonusPercent),
            item.Quantity,
            item.Revision);
    }
}
