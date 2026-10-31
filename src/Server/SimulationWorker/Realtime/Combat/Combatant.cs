using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

internal enum CombatantKind
{
    Player,
    Mob
}

// The combat state of one player entity or Mob actor. Loop-confined; only the combat
// system and the Mob brains change it, and every change that clients see advances a
// revision.
internal sealed class Combatant
{
    private Combatant(ulong entityId, CombatantKind kind, int maximumHealth, int maximumMana)
    {
        EntityId = entityId;
        Kind = kind;
        MaximumHealth = maximumHealth;
        Health = maximumHealth;
        MaximumMana = maximumMana;
        Mana = maximumMana;
    }

    public ulong EntityId { get; }

    public CombatantKind Kind { get; }

    public int Health { get; set; }

    public int MaximumHealth { get; }

    // The mana a player's magic attacks spend. Only the owner sees it; a Mob has none.
    public int Mana { get; set; }

    public int MaximumMana { get; }

    public RealtimeCombatLifeState LifeState { get; set; } = RealtimeCombatLifeState.Alive;

    public bool IsAlive => LifeState == RealtimeCombatLifeState.Alive;

    public ulong TargetEntityId { get; set; }

    // The first tick at which the next attack may start.
    public uint NextAttackTick { get; set; }

    // The player's attack that started and has not released yet, while its windup lasts.
    public PendingAttack? Pending { get; set; }

    // The combatant counts as in combat until this tick.
    public uint InCombatUntilTick { get; set; }

    public bool InCombat { get; set; }

    // The revision of the public combatant state; clients drop older ones.
    public long StateRevision { get; private set; } = 1;

    public bool StateChanged { get; private set; } = true;

    // The attacker whose hit reduced the health to 0, for the death event and the corpse.
    public ulong LastAttackerEntityId { get; set; }

    // A player entity and the peer that controls it.
    public PlayerSimulationEntity? Player { get; private init; }

    // A Mob actor and its attack.
    public WorldActorRuntimeState? Actor { get; private init; }

    public CombatAttackProfile? MobAttack { get; private init; }

    // Damage dealt to a Mob by attacker, which a threat table replaces later.
    public Dictionary<ulong, long> DamageLedger { get; } = [];

    // Owner-only state of a player.
    public long OwnerStateRevision { get; private set; } = 1;

    public bool OwnerStateChanged { get; private set; } = true;

    public RealtimeCombatAttackStatus AttackStatus { get; set; } = RealtimeCombatAttackStatus.Idle;

    // Attacks wait until this tick after the item in an equipment slot changed.
    public uint LockoutEndTick { get; set; }

    // The occupant of every equipment slot at the last observed item state, to detect a change.
    public string EquipmentSignature { get; set; } = string.Empty;

    // The item-state revision the owner state and the equipment signature last reflected.
    public long ObservedItemStateRevision { get; set; } = -1;

    public uint LifeSequence { get; set; }

    public uint RespawnTick { get; set; }

    public uint NextRegenerationTick { get; set; }

    // The tick at which line of sight to the current target is checked again.
    public uint NextLineOfSightCheckTick { get; set; }

    public bool LastLineOfSight { get; set; } = true;

    public ulong LineOfSightTargetEntityId { get; set; }

    // A development admin made the player's hits leave its health as it is.
    public bool AdminInvulnerable { get; set; }

    public static Combatant ForPlayer(PlayerSimulationEntity player, int maximumHealth, int maximumMana)
    {
        ArgumentNullException.ThrowIfNull(player);
        return new Combatant(player.NetworkEntityId, CombatantKind.Player, maximumHealth, maximumMana)
        {
            Player = player
        };
    }

    public static Combatant ForMob(WorldActorRuntimeState actor, int maximumHealth, CombatAttackProfile attack)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(attack);
        return new Combatant(actor.NetworkEntityId, CombatantKind.Mob, maximumHealth, 0)
        {
            Actor = actor,
            MobAttack = attack
        };
    }

    public void MarkStateChanged()
    {
        StateRevision++;
        StateChanged = true;
        MarkOwnerStateChanged();
    }

    public void MarkOwnerStateChanged()
    {
        if (Kind != CombatantKind.Player)
        {
            return;
        }

        OwnerStateRevision++;
        OwnerStateChanged = true;
    }

    public void ClearChanges()
    {
        StateChanged = false;
        OwnerStateChanged = false;
    }

    public RealtimeCombatantState ToState()
    {
        return new RealtimeCombatantState(
            EntityId,
            StateRevision,
            Health,
            MaximumHealth,
            LifeState,
            InCombat,
            IsAlive ? TargetEntityId : 0);
    }
}

// An attack in its windup: whom it is for, when it started, when it releases, and its style.
internal readonly record struct PendingAttack(ulong TargetEntityId, uint StartTick, uint ReleaseTick, string CombatStyle);

// Every combatant by network entity id. Loop-confined.
internal sealed class CombatantRegistry
{
    private readonly Dictionary<ulong, Combatant> combatants = [];

    public int Count => combatants.Count;

    public int PlayerCount { get; private set; }

    public void Add(Combatant combatant)
    {
        ArgumentNullException.ThrowIfNull(combatant);
        combatants.Add(combatant.EntityId, combatant);
        if (combatant.Kind == CombatantKind.Player)
        {
            PlayerCount++;
        }
    }

    public bool TryGet(ulong entityId, out Combatant combatant)
    {
        return combatants.TryGetValue(entityId, out combatant!);
    }

    public bool Remove(ulong entityId, out Combatant combatant)
    {
        if (!combatants.Remove(entityId, out combatant!))
        {
            return false;
        }

        if (combatant.Kind == CombatantKind.Player)
        {
            PlayerCount--;
        }

        return true;
    }

    // Combatants in entity id order, so every tick is reproducible.
    public void CopyTo(List<Combatant> destination)
    {
        destination.Clear();
        destination.AddRange(combatants.Values);
        destination.Sort(static (left, right) => left.EntityId.CompareTo(right.EntityId));
    }

    // Every combatant that attacks the entity loses its target.
    public void ClearTargetsOn(ulong entityId)
    {
        foreach (var combatant in combatants.Values)
        {
            if (combatant.TargetEntityId == entityId)
            {
                combatant.TargetEntityId = 0;
                combatant.AttackStatus = RealtimeCombatAttackStatus.Idle;
                combatant.MarkStateChanged();
            }
        }
    }

    public void Clear()
    {
        combatants.Clear();
        PlayerCount = 0;
    }
}
