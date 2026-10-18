using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class CombatClientTests
    {
        [Test]
        public void CombatantStatesKeepOnlyNewerRevisions()
        {
            var state = new CombatClientState();
            var changes = 0;
            state.Changed += () => changes++;

            Assert.That(state.TryApplyCombatant(Combatant(7, 2, 40), out var error), Is.True, error);
            Assert.That(state.TryApplyCombatant(Combatant(7, 1, 50), out error), Is.True, error);
            Assert.That(state.TryGetCombatant(7, out var current), Is.True);
            Assert.That(current.Health, Is.EqualTo(40));

            // A repeated revision changes nothing; different values for it are a conflict.
            Assert.That(state.TryApplyCombatant(Combatant(7, 2, 40), out error), Is.True, error);
            Assert.That(changes, Is.EqualTo(1));
            Assert.That(state.TryApplyCombatant(Combatant(7, 2, 35), out error), Is.False);
            Assert.That(error, Does.Contain("conflicts"));

            Assert.That(
                state.TryApplyCombatant(Combatant(7, 3, 0, RealtimeCombatLifeState.Dead), out error),
                Is.True,
                error);
            Assert.That(state.TryGetCombatant(7, out current), Is.True);
            Assert.That(current.LifeState, Is.EqualTo(RealtimeCombatLifeState.Dead));
            Assert.That(current.StateRevision, Is.EqualTo(3));

            Assert.That(state.TryApplyCombatant(Combatant(9, 1, 50), out error), Is.True, error);
            Assert.That(
                state.Combatants.Select(combatant => combatant.EntityId),
                Is.EqualTo(new ulong[] { 7, 9 }));
            Assert.That(changes, Is.EqualTo(3));
        }

        [Test]
        public void OwnerStateKeepsOnlyNewerRevisions()
        {
            var state = new CombatClientState();

            Assert.That(state.TryApplyOwner(Owner(3, 0, RealtimeCombatLifeState.Alive), out var error), Is.True, error);
            Assert.That(state.TryApplyOwner(Owner(2, 0, RealtimeCombatLifeState.Alive, health: 10), out error), Is.True, error);
            Assert.That(state.Owner.StateRevision, Is.EqualTo(3));
            Assert.That(state.Owner.Health, Is.EqualTo(100));

            // Only the server tick of a repeated revision may differ.
            Assert.That(
                state.TryApplyOwner(Owner(3, 0, RealtimeCombatLifeState.Alive, serverTick: 140), out error),
                Is.True,
                error);
            Assert.That(state.Owner.ServerTick, Is.EqualTo(100));
            Assert.That(
                state.TryApplyOwner(Owner(3, 0, RealtimeCombatLifeState.Alive, health: 90), out error),
                Is.False);
            Assert.That(error, Does.Contain("conflicts"));

            Assert.That(state.TryApplyOwner(Owner(4, 1, RealtimeCombatLifeState.Dead), out error), Is.True, error);
            Assert.That(state.Owner.LifeState, Is.EqualTo(RealtimeCombatLifeState.Dead));
            Assert.That(state.Owner.AttackStatus, Is.EqualTo(RealtimeCombatAttackStatus.Dead));
        }

        [Test]
        public void DespawnRemovesOnlyThatCombatant()
        {
            var state = new CombatClientState();
            Assert.That(state.TryApplyCombatant(Combatant(7, 4, 30), out var error), Is.True, error);
            Assert.That(state.TryApplyCombatant(Combatant(9, 2, 50), out error), Is.True, error);
            var changes = 0;
            state.Changed += () => changes++;

            state.RemoveCombatant(7);
            state.RemoveCombatant(11);

            Assert.That(state.TryGetCombatant(7, out _), Is.False);
            Assert.That(state.TryGetCombatant(9, out _), Is.True);
            Assert.That(changes, Is.EqualTo(1));

            // A combatant that spawns again starts from the state that follows its spawn.
            Assert.That(state.TryApplyCombatant(Combatant(7, 1, 50), out error), Is.True, error);
            Assert.That(state.TryGetCombatant(7, out var respawned), Is.True);
            Assert.That(respawned.StateRevision, Is.EqualTo(1));
        }

        [Test]
        public void IntentsCompleteByOperationIdAndKind()
        {
            var state = new CombatClientState();
            var attack = RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), 7);
            var clear = RealtimeCombatIntent.CreateClearTarget(Guid.NewGuid());
            var completed = new List<RealtimeCombatIntentResult>();
            state.IntentCompleted += completed.Add;

            Assert.That(state.TryBeginIntent(attack, out var error), Is.True, error);
            Assert.That(state.TryBeginIntent(clear, out error), Is.True, error);
            Assert.That(state.TryBeginIntent(attack, out error), Is.False);
            Assert.That(state.PendingIntentCount, Is.EqualTo(2));
            Assert.That(state.IsIntentPending(attack.OperationId), Is.True);

            Assert.That(
                state.TryCompleteIntent(
                    new RealtimeCombatIntentResult(
                        Guid.NewGuid(),
                        RealtimeCombatIntentKind.AttackTarget,
                        true,
                        7,
                        null),
                    out error),
                Is.False);
            Assert.That(error, Does.Contain("does not match"));
            Assert.That(
                state.TryCompleteIntent(
                    new RealtimeCombatIntentResult(
                        attack.OperationId,
                        RealtimeCombatIntentKind.ClearTarget,
                        true,
                        0,
                        null),
                    out error),
                Is.False);
            Assert.That(error, Does.Contain("kind"));

            // Results complete their own intent in any order.
            Assert.That(
                state.TryCompleteIntent(
                    new RealtimeCombatIntentResult(
                        clear.OperationId,
                        RealtimeCombatIntentKind.ClearTarget,
                        true,
                        0,
                        null),
                    out error),
                Is.True,
                error);
            var rejection = new RealtimeCombatIntentResult(
                attack.OperationId,
                RealtimeCombatIntentKind.AttackTarget,
                false,
                0,
                new RealtimeError(RealtimeCombatErrorCodes.TargetDead, "The target is dead."));
            Assert.That(state.TryCompleteIntent(rejection, out error), Is.True, error);

            Assert.That(state.PendingIntentCount, Is.Zero);
            Assert.That(state.LastIntentResult, Is.SameAs(rejection));
            Assert.That(state.LastIntentResult.Error.Code, Is.EqualTo(RealtimeCombatErrorCodes.TargetDead));
            Assert.That(
                completed.Select(result => result.OperationId),
                Is.EqualTo(new[] { clear.OperationId, attack.OperationId }));
            Assert.That(state.TryCompleteIntent(rejection, out _), Is.False);

            var unsent = RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), 9);
            Assert.That(state.TryBeginIntent(unsent, out error), Is.True, error);
            state.CancelIntent(unsent.OperationId);
            Assert.That(state.IsIntentPending(unsent.OperationId), Is.False);
        }

        [Test]
        public void PendingIntentsAreBounded()
        {
            var state = new CombatClientState();
            for (var index = 0; index < CombatClientState.MaximumPendingIntents; index++)
            {
                Assert.That(
                    state.TryBeginIntent(RealtimeCombatIntent.CreateClearTarget(Guid.NewGuid()), out var error),
                    Is.True,
                    error);
            }

            Assert.That(
                state.TryBeginIntent(RealtimeCombatIntent.CreateClearTarget(Guid.NewGuid()), out var limitError),
                Is.False);
            Assert.That(limitError, Does.Contain("Too many"));
            Assert.That(state.PendingIntentCount, Is.EqualTo(CombatClientState.MaximumPendingIntents));
        }

        [Test]
        public void ClearingForAReconnectDropsEveryRevisionAndIntent()
        {
            var state = new CombatClientState();
            var oldIntent = RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), 7);
            var respawns = 0;
            state.Respawned += _ => respawns++;
            Assert.That(state.TryApplyCombatant(Combatant(7, 5, 20), out var error), Is.True, error);
            Assert.That(state.TryApplyOwner(Owner(9, 2, RealtimeCombatLifeState.Alive), out error), Is.True, error);
            Assert.That(state.TryBeginIntent(oldIntent, out error), Is.True, error);

            state.Clear();

            Assert.That(state.Owner, Is.Null);
            Assert.That(state.Combatants, Is.Empty);
            Assert.That(state.PendingIntentCount, Is.Zero);
            Assert.That(state.LastIntentResult, Is.Null);

            // The new session numbers its revisions from the start again.
            Assert.That(state.TryApplyOwner(Owner(1, 4, RealtimeCombatLifeState.Alive), out error), Is.True, error);
            Assert.That(state.Owner.StateRevision, Is.EqualTo(1));
            Assert.That(state.TryApplyCombatant(Combatant(7, 1, 50), out error), Is.True, error);
            Assert.That(state.TryGetCombatant(7, out var combatant), Is.True);
            Assert.That(combatant.Health, Is.EqualTo(50));
            Assert.That(respawns, Is.Zero);
            Assert.That(
                state.TryCompleteIntent(
                    new RealtimeCombatIntentResult(
                        oldIntent.OperationId,
                        RealtimeCombatIntentKind.AttackTarget,
                        true,
                        7,
                        null),
                    out _),
                Is.False);
        }

        [Test]
        public void RespawnSignalFiresWhenTheLifeSequenceAdvancesToAlive()
        {
            var state = new CombatClientState();
            var respawns = new List<RealtimeOwnerCombatState>();
            state.Respawned += respawns.Add;

            Apply(state, Owner(1, 0, RealtimeCombatLifeState.Alive));
            Apply(state, Owner(2, 1, RealtimeCombatLifeState.Dead));
            Apply(state, Owner(3, 1, RealtimeCombatLifeState.Dead, serverTick: 120));
            Assert.That(respawns, Is.Empty);

            Apply(state, Owner(4, 2, RealtimeCombatLifeState.Alive));
            Assert.That(respawns.Select(owner => owner.StateRevision), Is.EqualTo(new long[] { 4 }));

            Apply(state, Owner(5, 2, RealtimeCombatLifeState.Alive, health: 80));
            Apply(state, Owner(3, 9, RealtimeCombatLifeState.Alive));
            Assert.That(respawns.Count, Is.EqualTo(1));

            // A death that was never seen as a state still ends in a respawn.
            Apply(state, Owner(6, 4, RealtimeCombatLifeState.Alive));
            Assert.That(respawns.Select(owner => owner.StateRevision), Is.EqualTo(new long[] { 4, 6 }));
        }

        [Test]
        public void EventBatchesAreForwardedInSimulationOrder()
        {
            var state = new CombatClientState();
            var received = new List<RealtimeCombatEventBatch>();
            state.EventsReceived += received.Add;
            var batch = new RealtimeCombatEventBatch(
                120,
                new[]
                {
                    new RealtimeCombatEvent(
                        RealtimeCombatEventKind.AttackReleased,
                        5,
                        7,
                        130,
                        0,
                        RealtimeCombatDamageType.Normal,
                        0),
                    new RealtimeCombatEvent(
                        RealtimeCombatEventKind.Hit,
                        5,
                        7,
                        0,
                        12,
                        RealtimeCombatDamageType.Normal,
                        0),
                    new RealtimeCombatEvent(
                        RealtimeCombatEventKind.Died,
                        5,
                        7,
                        0,
                        0,
                        RealtimeCombatDamageType.Normal,
                        0)
                });

            state.ApplyEvents(batch);
            state.ApplyEvents(null);

            Assert.That(received, Has.Count.EqualTo(1));
            Assert.That(received[0], Is.SameAs(batch));
            Assert.That(
                received[0].Events.Select(combatEvent => combatEvent.Kind),
                Is.EqualTo(new[]
                {
                    RealtimeCombatEventKind.AttackReleased,
                    RealtimeCombatEventKind.Hit,
                    RealtimeCombatEventKind.Died
                }));
        }

        [Test]
        public void CombatMessagesInFlightAreIgnoredWhileLeaving()
        {
            var combatMessages = new[]
            {
                RealtimeMessageType.CombatIntentResult,
                RealtimeMessageType.CombatantState,
                RealtimeMessageType.CombatEventBatch,
                RealtimeMessageType.OwnerCombatState,
                RealtimeMessageType.OwnerItemQuantityChanged
            };

            foreach (var messageType in combatMessages)
            {
                Assert.That(
                    RealtimeSimulationClient.ShouldIgnoreWhileLeaving(messageType),
                    Is.True,
                    messageType.ToString());
            }
        }

        [Test]
        public void EveryReliableServerMessageOutsideTheSessionLifecycleIsIgnoredWhileLeaving()
        {
            // The client handles these while leaving; every other reliable message from the
            // worker is in-flight presence, combat, or an operation result.
            var handledWhileLeaving = new[]
            {
                RealtimeMessageType.JoinAccepted,
                RealtimeMessageType.JoinRejected,
                RealtimeMessageType.LeaveAccepted,
                RealtimeMessageType.LeaveRejected,
                RealtimeMessageType.ServerDisconnect,
                RealtimeMessageType.CarryStateChanged
            };

            foreach (RealtimeMessageType messageType in Enum.GetValues(typeof(RealtimeMessageType)))
            {
                if (!RealtimeProtocol.TryGetDescriptor(messageType, out var descriptor)
                    || descriptor.Direction != RealtimeMessageDirection.ServerToClient
                    || descriptor.Delivery != RealtimeDelivery.ReliableOrdered)
                {
                    continue;
                }

                Assert.That(
                    RealtimeSimulationClient.ShouldIgnoreWhileLeaving(messageType),
                    Is.EqualTo(!handledWhileLeaving.Contains(messageType)),
                    messageType.ToString());
            }
        }

        private static void Apply(CombatClientState state, RealtimeOwnerCombatState owner)
        {
            Assert.That(state.TryApplyOwner(owner, out var error), Is.True, error);
        }

        private static RealtimeCombatantState Combatant(
            ulong entityId,
            long revision,
            int health,
            RealtimeCombatLifeState lifeState = RealtimeCombatLifeState.Alive)
        {
            return new RealtimeCombatantState(
                entityId,
                revision,
                health,
                50,
                lifeState,
                health < 50,
                0);
        }

        private static RealtimeOwnerCombatState Owner(
            long revision,
            uint lifeSequence,
            RealtimeCombatLifeState lifeState,
            uint serverTick = 100,
            int health = 100)
        {
            var dead = lifeState == RealtimeCombatLifeState.Dead;
            return new RealtimeOwnerCombatState(
                revision,
                serverTick,
                dead ? 0 : health,
                100,
                lifeState,
                lifeSequence,
                dead ? 250u : 0u,
                false,
                0,
                dead ? RealtimeCombatAttackStatus.Dead : RealtimeCombatAttackStatus.Idle,
                0,
                0,
                Guid.Empty,
                0);
        }
    }
}
