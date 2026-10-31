#nullable disable
using System;
using System.Linq;
using Mmorpg.GameProtocol;

namespace Mmorpg.SharedTests
{
    // Combat intents, combatant state, combat events, and the owner combat state.
    public static partial class RealtimeProtocolGolden
    {
        private const ulong CombatAttackerEntityId = 0x3132333435363738;
        private const ulong CombatTargetEntityId = 0x4142434445464748;

        private static void RegisterCombat(Registry r)
        {
            RegisterCombatVectors(r);
            RegisterCombatIntentCases(r);
            RegisterCombatStateCases(r);
            RegisterCombatEventCases(r);
            RegisterOwnerCombatStateCases(r);
        }

        private static void RegisterCombatVectors(Registry r)
        {
            const RealtimeMessageType intent = RealtimeMessageType.CombatIntent;
            const RealtimeMessageType result = RealtimeMessageType.CombatIntentResult;
            const RealtimeMessageType state = RealtimeMessageType.CombatantState;
            const RealtimeMessageType events = RealtimeMessageType.CombatEventBatch;
            const RealtimeMessageType owner = RealtimeMessageType.OwnerCombatState;

            r.Vector(
                "Combat/CombatIntent/attack target",
                intent,
                () => RealtimeProtocol.EncodeCombatIntent(RealtimeCombatIntent.CreateAttackTarget(G(1), CombatTargetEntityId)));
            r.Vector(
                "Combat/CombatIntent/clear target",
                intent,
                () => RealtimeProtocol.EncodeCombatIntent(RealtimeCombatIntent.CreateClearTarget(G(1))));
            r.Vector(
                "Combat/CombatIntentResult/accepted",
                result,
                () => RealtimeProtocol.EncodeCombatIntentResult(CombatIntentResultModel()));
            r.Vector(
                "Combat/CombatIntentResult/rejected",
                result,
                () => RealtimeProtocol.EncodeCombatIntentResult(CombatIntentResultModel(
                    succeeded: false,
                    targetEntityId: 0,
                    error: new RealtimeError(RealtimeCombatErrorCodes.TargetDead, "Målet är redan dött ✓"))));
            r.Vector("Combat/CombatantState/alive", state, () => RealtimeProtocol.EncodeCombatantState(CombatantStateModel()));
            r.Vector(
                "Combat/CombatantState/dead",
                state,
                () => RealtimeProtocol.EncodeCombatantState(CombatantStateModel(
                    stateRevision: long.MaxValue,
                    health: 0,
                    lifeState: RealtimeCombatLifeState.Dead,
                    inCombat: false,
                    targetEntityId: 0)));
            r.Vector(
                "Combat/CombatEventBatch/release hit death",
                events,
                () => RealtimeProtocol.EncodeCombatEventBatch(new RealtimeCombatEventBatch(
                    0x01020304,
                    new[]
                    {
                        AttackReleasedModel(),
                        HitModel(),
                        DiedModel()
                    })));
            r.Vector(
                "Combat/CombatEventBatch/start and cancel",
                events,
                () => RealtimeProtocol.EncodeCombatEventBatch(new RealtimeCombatEventBatch(
                    0x01020304,
                    new[]
                    {
                        AttackModel(RealtimeCombatEventKind.AttackStarted, 0x01020310),
                        AttackModel(RealtimeCombatEventKind.AttackCancelled, 0)
                    })));
            r.Vector(
                "Combat/CombatEventBatch/full",
                events,
                () => RealtimeProtocol.EncodeCombatEventBatch(new RealtimeCombatEventBatch(
                    uint.MaxValue,
                    Enumerable.Range(0, RealtimeProtocolLimits.MaximumCombatEventsPerBatch)
                        .Select(_ => HitModel())
                        .ToArray())));
            r.Vector("Combat/OwnerCombatState/attacking", owner, () => RealtimeProtocol.EncodeOwnerCombatState(OwnerCombatStateModel()));
            r.Vector(
                "Combat/OwnerCombatState/not enough mana",
                owner,
                () => RealtimeProtocol.EncodeOwnerCombatState(OwnerCombatStateModel(
                    mana: 3,
                    attackStatus: RealtimeCombatAttackStatus.NotEnoughMana,
                    ammunitionItemInstanceId: Guid.Empty,
                    ammunitionQuantity: 0)));
            r.Vector(
                "Combat/OwnerCombatState/dead",
                owner,
                () => RealtimeProtocol.EncodeOwnerCombatState(OwnerCombatStateModel(
                    health: 0,
                    lifeState: RealtimeCombatLifeState.Dead,
                    lifeSequence: uint.MaxValue,
                    respawnTick: 0x0A0B0C0D,
                    targetEntityId: 0,
                    attackStatus: RealtimeCombatAttackStatus.Dead,
                    ammunitionItemInstanceId: Guid.Empty,
                    ammunitionQuantity: 0)));
        }

        private static void RegisterCombatIntentCases(Registry r)
        {
            r.Accepts("Combat/CombatIntent/attack baseline", CombatIntentWire(1, Wire.Fragment().U64(CombatTargetEntityId)));
            r.Accepts("Combat/CombatIntent/clear baseline", CombatIntentWire(2, Wire.Fragment()));
            r.Rejects("Combat/CombatIntent/empty operation id", CombatIntentWire(2, Wire.Fragment(), operationId: Guid.Empty));
            r.Rejects("Combat/CombatIntent/kind 0", CombatIntentWire(0, Wire.Fragment()));
            r.Rejects("Combat/CombatIntent/kind 3", CombatIntentWire(3, Wire.Fragment()));
            r.Rejects("Combat/CombatIntent/attack target 0", CombatIntentWire(1, Wire.Fragment().U64(0)));
            r.Rejects("Combat/CombatIntent/attack without target", CombatIntentWire(1, Wire.Fragment()));
            r.Rejects("Combat/CombatIntent/clear with a target", CombatIntentWire(2, Wire.Fragment().U64(CombatTargetEntityId)));
            r.RejectsEncode(
                "Combat/CombatIntent/empty operation id",
                () => RealtimeProtocol.EncodeCombatIntent(RealtimeCombatIntent.CreateClearTarget(Guid.Empty)));
            r.RejectsEncode(
                "Combat/CombatIntent/attack target 0",
                () => RealtimeProtocol.EncodeCombatIntent(RealtimeCombatIntent.CreateAttackTarget(G(1), 0)));

            r.Accepts("Combat/CombatIntentResult/accepted baseline", CombatIntentResultWire());
            r.Accepts(
                "Combat/CombatIntentResult/rejected baseline",
                CombatIntentResultWire(succeeded: 0, targetEntityId: 0, error: ErrorFragment("combat_target_dead", "The target is dead.")));
            r.Rejects("Combat/CombatIntentResult/empty operation id", CombatIntentResultWire(operationId: Guid.Empty));
            r.Rejects("Combat/CombatIntentResult/kind 0", CombatIntentResultWire(kind: 0));
            r.Rejects(
                "Combat/CombatIntentResult/rejected without error",
                CombatIntentResultWire(succeeded: 0, targetEntityId: 0));
            r.Rejects(
                "Combat/CombatIntentResult/blank error code",
                CombatIntentResultWire(succeeded: 0, error: ErrorFragment(" ", "message")));
            r.RejectsEncode(
                "Combat/CombatIntentResult/rejected without error",
                () => RealtimeProtocol.EncodeCombatIntentResult(CombatIntentResultModel(succeeded: false, error: null)));
        }

        private static void RegisterCombatStateCases(Registry r)
        {
            r.Accepts("Combat/CombatantState/baseline", CombatantStateWire());
            r.Accepts("Combat/CombatantState/full health", CombatantStateWire(health: 50));
            r.Accepts("Combat/CombatantState/evading", CombatantStateWire(lifeState: 3));
            r.Accepts("Combat/CombatantState/dead", CombatantStateWire(health: 0, lifeState: 2, inCombat: 0, targetEntityId: 0));
            r.Rejects("Combat/CombatantState/entity zero", CombatantStateWire(entityId: 0));
            r.Rejects("Combat/CombatantState/state revision 0", CombatantStateWire(stateRevision: 0));
            r.Rejects("Combat/CombatantState/negative health", CombatantStateWire(health: -1));
            r.Rejects("Combat/CombatantState/maximum health 0", CombatantStateWire(health: 0, maximumHealth: 0));
            r.Rejects("Combat/CombatantState/health above maximum", CombatantStateWire(health: 51));
            r.Rejects("Combat/CombatantState/life state 0", CombatantStateWire(lifeState: 0));
            r.Rejects("Combat/CombatantState/life state 4", CombatantStateWire(lifeState: 4));
            r.Rejects("Combat/CombatantState/dead with health", CombatantStateWire(lifeState: 2, targetEntityId: 0));
            r.Rejects("Combat/CombatantState/dead with a target", CombatantStateWire(health: 0, lifeState: 2));
            r.Rejects("Combat/CombatantState/targets itself", CombatantStateWire(targetEntityId: CombatAttackerEntityId));
            r.AcceptsAs(
                "Combat/CombatantState/in combat byte 2 reads as true",
                CombatantStateWire(inCombat: 2),
                CombatantStateWire());
            r.RejectsEncode(
                "Combat/CombatantState/health above maximum",
                () => RealtimeProtocol.EncodeCombatantState(CombatantStateModel(health: 51)));
            r.RejectsEncode(
                "Combat/CombatantState/undefined life state",
                () => RealtimeProtocol.EncodeCombatantState(CombatantStateModel(lifeState: 0)));
        }

        private static void RegisterCombatEventCases(Registry r)
        {
            r.Accepts("Combat/CombatEventBatch/baseline", CombatEventBatchWire(AttackReleasedWire(), HitWire(), DiedWire()));
            r.Accepts("Combat/CombatEventBatch/died without a killer", CombatEventBatchWire(DiedWire(source: 0)));
            r.Accepts("Combat/CombatEventBatch/hit without a source", CombatEventBatchWire(HitWire(source: 0)));
            r.Accepts(
                "Combat/CombatEventBatch/32 events",
                CombatEventBatchWire(Enumerable.Range(0, 32).Select(_ => HitWire()).ToArray()));
            r.Rejects("Combat/CombatEventBatch/no events", CombatEventBatchWire());
            r.Rejects(
                "Combat/CombatEventBatch/33 events",
                CombatEventBatchWire(Enumerable.Range(0, 33).Select(_ => HitWire()).ToArray()));
            r.Rejects("Combat/CombatEventBatch/kind 0", CombatEventBatchWire(CombatEventWire(0)));
            r.Rejects("Combat/CombatEventBatch/kind 6", CombatEventBatchWire(CombatEventWire(6)));
            r.Accepts("Combat/CombatEventBatch/attack started", CombatEventBatchWire(AttackStartedWire()));
            r.Accepts("Combat/CombatEventBatch/attack cancelled", CombatEventBatchWire(AttackCancelledWire()));
            r.Rejects("Combat/CombatEventBatch/cancel with a tick", CombatEventBatchWire(AttackCancelledWire(tick: 1)));
            r.Rejects("Combat/CombatEventBatch/start without attacker", CombatEventBatchWire(AttackStartedWire(source: 0)));
            r.Rejects("Combat/CombatEventBatch/start with damage", CombatEventBatchWire(AttackStartedWire(damage: 3)));
            r.Rejects("Combat/CombatEventBatch/cancel at itself", CombatEventBatchWire(AttackCancelledWire(source: CombatTargetEntityId)));
            r.Rejects("Combat/CombatEventBatch/target 0", CombatEventBatchWire(HitWire(target: 0)));
            r.Rejects("Combat/CombatEventBatch/negative damage", CombatEventBatchWire(HitWire(damage: -1)));
            r.Rejects("Combat/CombatEventBatch/negative health after", CombatEventBatchWire(HitWire(healthAfter: -1)));
            r.Rejects("Combat/CombatEventBatch/damage type 0", CombatEventBatchWire(HitWire(damageType: 0)));
            r.Rejects("Combat/CombatEventBatch/damage type 6", CombatEventBatchWire(HitWire(damageType: 6)));
            r.Accepts("Combat/CombatEventBatch/shadow hit", CombatEventBatchWire(HitWire(damageType: 5)));
            r.Rejects("Combat/CombatEventBatch/hit with a tick", CombatEventBatchWire(HitWire(tick: 1)));
            r.Accepts("Combat/CombatEventBatch/arrow release", CombatEventBatchWire(AttackReleasedWire(attackStyle: 2)));
            r.Accepts("Combat/CombatEventBatch/melee release", CombatEventBatchWire(AttackReleasedWire(attackStyle: 1)));
            r.Rejects("Combat/CombatEventBatch/attack style 0", CombatEventBatchWire(AttackReleasedWire(attackStyle: 0)));
            r.Rejects("Combat/CombatEventBatch/attack style 4", CombatEventBatchWire(AttackReleasedWire(attackStyle: 4)));
            r.Rejects("Combat/CombatEventBatch/hit with a magic style", CombatEventBatchWire(HitWire(attackStyle: 3)));
            r.Rejects("Combat/CombatEventBatch/death with a ranged style", CombatEventBatchWire(DiedWire(attackStyle: 2)));
            r.Rejects("Combat/CombatEventBatch/release without attacker", CombatEventBatchWire(AttackReleasedWire(source: 0)));
            r.Rejects(
                "Combat/CombatEventBatch/release at itself",
                CombatEventBatchWire(AttackReleasedWire(source: CombatTargetEntityId)));
            r.Rejects("Combat/CombatEventBatch/release with damage", CombatEventBatchWire(AttackReleasedWire(damage: 3)));
            r.Rejects("Combat/CombatEventBatch/death with damage", CombatEventBatchWire(DiedWire(damage: 3)));
            r.Rejects("Combat/CombatEventBatch/death with a tick", CombatEventBatchWire(DiedWire(tick: 1)));
            r.RejectsEncode(
                "Combat/CombatEventBatch/no events",
                () => RealtimeProtocol.EncodeCombatEventBatch(new RealtimeCombatEventBatch(1, new RealtimeCombatEvent[0])));
            r.RejectsEncode(
                "Combat/CombatEventBatch/33 events",
                () => RealtimeProtocol.EncodeCombatEventBatch(new RealtimeCombatEventBatch(
                    1,
                    Enumerable.Range(0, 33).Select(_ => HitModel()).ToArray())));
            r.RejectsEncode(
                "Combat/CombatEventBatch/release with damage",
                () => RealtimeProtocol.EncodeCombatEventBatch(new RealtimeCombatEventBatch(
                    1,
                    new[]
                    {
                        new RealtimeCombatEvent(
                            RealtimeCombatEventKind.AttackReleased,
                            CombatAttackerEntityId,
                            CombatTargetEntityId,
                            100,
                            1,
                            RealtimeCombatDamageType.Normal,
                            0,
                            RealtimeCombatAttackStyle.Magic)
                    })));
        }

        private static void RegisterOwnerCombatStateCases(Registry r)
        {
            r.Accepts("Combat/OwnerCombatState/baseline", OwnerCombatStateWire());
            r.Accepts(
                "Combat/OwnerCombatState/idle without a target",
                OwnerCombatStateWire(targetEntityId: 0, attackStatus: 1));
            r.Accepts(
                "Combat/OwnerCombatState/dead",
                OwnerCombatStateWire(health: 0, lifeState: 2, respawnTick: 300, targetEntityId: 0, attackStatus: 10));
            r.Accepts(
                "Combat/OwnerCombatState/no ammunition",
                OwnerCombatStateWire(attackStatus: 8, ammunitionItemInstanceId: Guid.Empty, ammunitionQuantity: 0));
            r.Accepts("Combat/OwnerCombatState/not enough mana", OwnerCombatStateWire(mana: 4, attackStatus: 11));
            r.Accepts("Combat/OwnerCombatState/full mana", OwnerCombatStateWire(mana: 100));
            r.Accepts("Combat/OwnerCombatState/without mana", OwnerCombatStateWire(mana: 0, maximumMana: 0));
            r.Rejects("Combat/OwnerCombatState/state revision 0", OwnerCombatStateWire(stateRevision: 0));
            r.Rejects("Combat/OwnerCombatState/health above maximum", OwnerCombatStateWire(health: 101));
            r.Rejects("Combat/OwnerCombatState/life state 0", OwnerCombatStateWire(lifeState: 0));
            r.Rejects("Combat/OwnerCombatState/evading", OwnerCombatStateWire(lifeState: 3));
            r.Rejects("Combat/OwnerCombatState/attack status 0", OwnerCombatStateWire(attackStatus: 0));
            r.Rejects("Combat/OwnerCombatState/attack status 12", OwnerCombatStateWire(attackStatus: 12));
            r.Rejects("Combat/OwnerCombatState/mana above maximum", OwnerCombatStateWire(mana: 101));
            r.Rejects("Combat/OwnerCombatState/negative mana", OwnerCombatStateWire(mana: -1));
            r.Rejects("Combat/OwnerCombatState/negative maximum mana", OwnerCombatStateWire(mana: 0, maximumMana: -1));
            r.Rejects(
                "Combat/OwnerCombatState/dead with health",
                OwnerCombatStateWire(lifeState: 2, respawnTick: 300, targetEntityId: 0, attackStatus: 10));
            r.Rejects(
                "Combat/OwnerCombatState/dead and still attacking",
                OwnerCombatStateWire(health: 0, lifeState: 2, respawnTick: 300, targetEntityId: 0));
            r.Rejects("Combat/OwnerCombatState/alive with a respawn tick", OwnerCombatStateWire(respawnTick: 300));
            r.Rejects("Combat/OwnerCombatState/alive with status dead", OwnerCombatStateWire(attackStatus: 10));
            r.Rejects(
                "Combat/OwnerCombatState/attacking without a target",
                OwnerCombatStateWire(targetEntityId: 0));
            r.Rejects(
                "Combat/OwnerCombatState/arrows without a stack",
                OwnerCombatStateWire(ammunitionItemInstanceId: Guid.Empty));
            r.Rejects("Combat/OwnerCombatState/negative arrows", OwnerCombatStateWire(ammunitionQuantity: -1));
            r.RejectsEncode(
                "Combat/OwnerCombatState/dead with health",
                () => RealtimeProtocol.EncodeOwnerCombatState(OwnerCombatStateModel(
                    lifeState: RealtimeCombatLifeState.Dead,
                    targetEntityId: 0,
                    attackStatus: RealtimeCombatAttackStatus.Dead)));
            r.RejectsEncode(
                "Combat/OwnerCombatState/mana above maximum",
                () => RealtimeProtocol.EncodeOwnerCombatState(OwnerCombatStateModel(mana: 101)));
        }

        private static RealtimeCombatIntentResult CombatIntentResultModel(
            bool succeeded = true,
            ulong targetEntityId = CombatTargetEntityId,
            RealtimeError error = null)
        {
            return new RealtimeCombatIntentResult(
                G(1),
                RealtimeCombatIntentKind.AttackTarget,
                succeeded,
                targetEntityId,
                error);
        }

        private static RealtimeCombatantState CombatantStateModel(
            long stateRevision = 7,
            int health = 37,
            RealtimeCombatLifeState lifeState = RealtimeCombatLifeState.Alive,
            bool inCombat = true,
            ulong targetEntityId = CombatTargetEntityId)
        {
            return new RealtimeCombatantState(
                CombatAttackerEntityId,
                stateRevision,
                health,
                50,
                lifeState,
                inCombat,
                targetEntityId);
        }

        private static RealtimeCombatEvent AttackReleasedModel()
        {
            return new RealtimeCombatEvent(
                RealtimeCombatEventKind.AttackReleased,
                CombatAttackerEntityId,
                CombatTargetEntityId,
                0x01020310,
                0,
                RealtimeCombatDamageType.Normal,
                0,
                RealtimeCombatAttackStyle.Magic);
        }

        private static RealtimeCombatEvent AttackModel(RealtimeCombatEventKind kind, uint tick)
        {
            return new RealtimeCombatEvent(
                kind,
                CombatAttackerEntityId,
                CombatTargetEntityId,
                tick,
                0,
                RealtimeCombatDamageType.Normal,
                0,
                RealtimeCombatAttackStyle.Ranged);
        }

        private static RealtimeCombatEvent HitModel()
        {
            return new RealtimeCombatEvent(
                RealtimeCombatEventKind.Hit,
                CombatAttackerEntityId,
                CombatTargetEntityId,
                0,
                17,
                RealtimeCombatDamageType.Fire,
                33,
                RealtimeCombatAttackStyle.Melee);
        }

        private static RealtimeCombatEvent DiedModel()
        {
            return new RealtimeCombatEvent(
                RealtimeCombatEventKind.Died,
                CombatAttackerEntityId,
                CombatTargetEntityId,
                0,
                0,
                RealtimeCombatDamageType.Normal,
                0,
                RealtimeCombatAttackStyle.Melee);
        }

        private static RealtimeOwnerCombatState OwnerCombatStateModel(
            int health = 64,
            int mana = 40,
            RealtimeCombatLifeState lifeState = RealtimeCombatLifeState.Alive,
            uint lifeSequence = 2,
            uint respawnTick = 0,
            ulong targetEntityId = CombatTargetEntityId,
            RealtimeCombatAttackStatus attackStatus = RealtimeCombatAttackStatus.Attacking,
            Guid? ammunitionItemInstanceId = null,
            int ammunitionQuantity = 997)
        {
            return new RealtimeOwnerCombatState(
                stateRevision: 11,
                serverTick: 0x01020304,
                health: health,
                maximumHealth: 100,
                mana: mana,
                maximumMana: 100,
                lifeState: lifeState,
                lifeSequence: lifeSequence,
                respawnTick: respawnTick,
                inCombat: true,
                targetEntityId: targetEntityId,
                attackStatus: attackStatus,
                nextAttackTick: 0x01020340,
                lockoutEndTick: 0x01020320,
                ammunitionItemInstanceId: ammunitionItemInstanceId ?? G(5),
                ammunitionQuantity: ammunitionQuantity);
        }

        private static Wire CombatIntentWire(byte kind, Wire payload, Guid? operationId = null)
        {
            return Wire.Message(RealtimeMessageType.CombatIntent)
                .Guid(operationId ?? G(1))
                .U8(kind)
                .Append(payload);
        }

        private static Wire CombatIntentResultWire(
            Guid? operationId = null,
            byte kind = 1,
            byte succeeded = 1,
            ulong targetEntityId = CombatTargetEntityId,
            Wire error = null)
        {
            var wire = Wire.Message(RealtimeMessageType.CombatIntentResult)
                .Guid(operationId ?? G(1))
                .U8(kind)
                .U8(succeeded)
                .U64(targetEntityId);
            return error == null ? wire : wire.Append(error);
        }

        private static Wire CombatantStateWire(
            ulong entityId = CombatAttackerEntityId,
            long stateRevision = 7,
            int health = 37,
            int maximumHealth = 50,
            byte lifeState = 1,
            byte inCombat = 1,
            ulong targetEntityId = CombatTargetEntityId)
        {
            return Wire.Message(RealtimeMessageType.CombatantState)
                .U64(entityId)
                .I64(stateRevision)
                .I32(health)
                .I32(maximumHealth)
                .U8(lifeState)
                .U8(inCombat)
                .U64(targetEntityId);
        }

        private static Wire CombatEventBatchWire(params Wire[] events)
        {
            var wire = Wire.Message(RealtimeMessageType.CombatEventBatch)
                .U32(0x01020304)
                .U8((byte)events.Length);
            foreach (var value in events)
            {
                wire.Append(value);
            }

            return wire;
        }

        private static Wire CombatEventWire(
            byte kind,
            ulong source = CombatAttackerEntityId,
            ulong target = CombatTargetEntityId,
            uint tick = 0,
            int damage = 0,
            byte damageType = 1,
            int healthAfter = 0,
            byte attackStyle = 1)
        {
            return Wire.Fragment()
                .U8(kind)
                .U64(source)
                .U64(target)
                .U32(tick)
                .I32(damage)
                .U8(damageType)
                .I32(healthAfter)
                .U8(attackStyle);
        }

        private static Wire AttackReleasedWire(ulong source = CombatAttackerEntityId, int damage = 0, byte attackStyle = 3)
        {
            return CombatEventWire(1, source: source, tick: 0x01020310, damage: damage, attackStyle: attackStyle);
        }

        private static Wire AttackStartedWire(ulong source = CombatAttackerEntityId, int damage = 0)
        {
            return CombatEventWire(4, source: source, tick: 0x01020310, damage: damage, attackStyle: 2);
        }

        private static Wire AttackCancelledWire(ulong source = CombatAttackerEntityId, uint tick = 0)
        {
            return CombatEventWire(5, source: source, tick: tick, attackStyle: 2);
        }

        private static Wire HitWire(
            ulong source = CombatAttackerEntityId,
            ulong target = CombatTargetEntityId,
            uint tick = 0,
            int damage = 17,
            byte damageType = 2,
            int healthAfter = 33,
            byte attackStyle = 1)
        {
            return CombatEventWire(2, source, target, tick, damage, damageType, healthAfter, attackStyle);
        }

        private static Wire DiedWire(ulong source = CombatAttackerEntityId, int damage = 0, uint tick = 0, byte attackStyle = 1)
        {
            return CombatEventWire(3, source: source, tick: tick, damage: damage, attackStyle: attackStyle);
        }

        private static Wire OwnerCombatStateWire(
            long stateRevision = 11,
            int health = 64,
            int mana = 40,
            int maximumMana = 100,
            byte lifeState = 1,
            uint respawnTick = 0,
            ulong targetEntityId = CombatTargetEntityId,
            byte attackStatus = 2,
            Guid? ammunitionItemInstanceId = null,
            int ammunitionQuantity = 997)
        {
            return Wire.Message(RealtimeMessageType.OwnerCombatState)
                .I64(stateRevision)
                .U32(0x01020304)
                .I32(health)
                .I32(100)
                .I32(mana)
                .I32(maximumMana)
                .U8(lifeState)
                .U32(2)
                .U32(respawnTick)
                .U8(1)
                .U64(targetEntityId)
                .U8(attackStatus)
                .U32(0x01020340)
                .U32(0x01020320)
                .Guid(ammunitionItemInstanceId ?? G(5))
                .I32(ammunitionQuantity);
        }
    }
}
