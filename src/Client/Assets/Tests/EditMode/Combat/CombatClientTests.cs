using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Ui;
using Mmorpg.GameProtocol;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class CombatClientTests
    {
        private const int TickRate = 30;

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

        // The combat HUD and the nameplates read every combatant on every combatant state, tens
        // of times a second in a crowd: the state keeps them in the order of their entity ids as
        // they change, so a read neither sorts nor allocates.
        [Test]
        public void TheCombatantsReadInOrderWithoutSortingOrAllocating()
        {
            var state = new CombatClientState();
            foreach (var entityId in new ulong[] { 9, 3, 7, 1 })
            {
                Apply(state, Combatant(entityId, 1, 50));
            }

            Assert.That(state.Combatants.Select(combatant => combatant.EntityId), Is.EqualTo(new ulong[] { 1, 3, 7, 9 }));
            Assert.That(state.Combatants, Is.SameAs(state.Combatants));

            Apply(state, Combatant(7, 2, 30));
            state.RemoveCombatant(3);
            Assert.That(state.Combatants.Select(combatant => combatant.EntityId), Is.EqualTo(new ulong[] { 1, 7, 9 }));
            Assert.That(state.Combatants[1].Health, Is.EqualTo(30));

            var sum = 0UL;
            Assert.That(
                ManagedAllocations.Of(() =>
                {
                    var combatants = state.Combatants;
                    for (var index = 0; index < combatants.Count; index++)
                    {
                        sum += combatants[index].EntityId;
                    }
                }),
                Is.Zero);
            Assert.That(sum, Is.EqualTo(34UL));

            state.Clear();
            Assert.That(state.Combatants, Is.Empty);
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

        // The shared rule as the client runs it: the amount at the named tick and every second
        // after it, up to the maximum, across the wrap of the server tick.
        [Test]
        public void TheRegenerationRuleAddsTheAmountEverySecondUpToTheMaximum()
        {
            var regenerating = Regenerating(7, 1, 20, uint.MaxValue - 9);

            Assert.That(RealtimeCombatHealth.At(regenerating, uint.MaxValue - 10, TickRate), Is.EqualTo(20));
            Assert.That(RealtimeCombatHealth.At(regenerating, uint.MaxValue - 9, TickRate), Is.EqualTo(23));
            Assert.That(RealtimeCombatHealth.At(regenerating, 19, TickRate), Is.EqualTo(23));
            Assert.That(RealtimeCombatHealth.At(regenerating, 20, TickRate), Is.EqualTo(26));
            Assert.That(RealtimeCombatHealth.At(regenerating, 1_000_000, TickRate), Is.EqualTo(50));
            Assert.That(RealtimeCombatHealth.At(regenerating, 20, 0), Is.EqualTo(20));
            Assert.That(RealtimeCombatHealth.At(Combatant(7, 1, 20), 1_000_000, TickRate), Is.EqualTo(20));
        }

        // A regenerating player's bar rises with the clock's estimate of the server tick without
        // a new message, goes once the player is whole, and a hit shows at once.
        [Test]
        public void AHealthBarRisesWithoutANewMessage()
        {
            var state = new CombatClientState();
            var changes = 0;
            state.Changed += () => changes++;
            Apply(state, Regenerating(7, 4, 20, 1_000));
            Assert.That(state.TryGetCombatant(7, out var regenerating), Is.True);

            Assert.That(state.HealthAt(regenerating, Clock(999)), Is.EqualTo(20));
            Assert.That(state.HealthAt(regenerating, Clock(1_000)), Is.EqualTo(23));
            Assert.That(state.HealthAt(regenerating, new CombatServerClock(1_000, 0.5f, TickRate)), Is.EqualTo(23));
            Assert.That(state.HealthAt(regenerating, new CombatServerClock(1_000, 1f, TickRate)), Is.EqualTo(26));
            var fractions = Enumerable.Range(1, 9)
                .Select(second => CombatHudPresentation.HealthFraction(
                    state.HealthAt(regenerating, Clock(1_000 + (uint)(second * TickRate))),
                    50))
                .ToArray();
            Assert.That(fractions, Is.Ordered.Ascending);
            Assert.That(fractions.Distinct().Count(), Is.EqualTo(9));
            Assert.That(fractions[0], Is.EqualTo(26f / 50f).Within(0.0001f));
            Assert.That(fractions[8], Is.EqualTo(1f));
            Assert.That(
                CombatHudPresentation.ShowsHealthBar(
                    state.HealthAt(regenerating, Clock(1_270)),
                    50,
                    RealtimeCombatLifeState.Alive),
                Is.False);
            Assert.That(changes, Is.EqualTo(1));

            // An estimate that steps back, as when a late owner snapshot arrives, never takes
            // shown health back, and neither does a clock that is not known.
            Assert.That(state.HealthAt(regenerating, Clock(1_000)), Is.EqualTo(50));
            Assert.That(state.HealthAt(regenerating, default(CombatServerClock)), Is.EqualTo(50));

            // A hit is a new state that shows at once, and the regeneration after it starts there.
            Apply(state, Combatant(7, 5, 12));
            Assert.That(state.TryGetCombatant(7, out var hit), Is.True);
            Assert.That(state.HealthAt(hit, Clock(1_300)), Is.EqualTo(12));
            Apply(state, Regenerating(7, 6, 12, 1_330));
            Assert.That(state.TryGetCombatant(7, out var again), Is.True);
            Assert.That(state.HealthAt(again, Clock(1_329)), Is.EqualTo(12));
            Assert.That(state.HealthAt(again, Clock(1_330)), Is.EqualTo(15));
            Assert.That(changes, Is.EqualTo(3));

            // Without a known clock a state shows its own health.
            Assert.That(new CombatClientState().HealthAt(again, default(CombatServerClock)), Is.EqualTo(12));
        }

        // A state of the same revision that was sent later carries the health and the next step
        // the regeneration reached by then; it describes the same regeneration and is no conflict.
        [Test]
        public void ARepeatedRevisionMayCarryTheRegenerationFurtherOn()
        {
            var state = new CombatClientState();
            Apply(state, Regenerating(7, 4, 20, 1_000));

            Apply(state, Regenerating(7, 4, 26, 1_060));
            Assert.That(state.TryGetCombatant(7, out var kept), Is.True);
            Assert.That((kept.Health, kept.HealthRegenerationTick), Is.EqualTo((20, 1_000u)));
            Assert.That(state.TryApplyCombatant(Regenerating(7, 4, 20, 1_000, amount: 5), out var error), Is.False);
            Assert.That(error, Does.Contain("conflicts"));
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
        public void DeathSignalFiresOnceForEveryDeath()
        {
            var state = new CombatClientState();
            var deaths = new List<long>();
            state.Died += owner => deaths.Add(owner.StateRevision);

            Apply(state, Owner(1, 0, RealtimeCombatLifeState.Alive));
            Apply(state, Owner(2, 1, RealtimeCombatLifeState.Dead));
            Apply(state, Owner(3, 1, RealtimeCombatLifeState.Dead));
            Assert.That(deaths, Is.EqualTo(new long[] { 2 }));

            Apply(state, Owner(4, 2, RealtimeCombatLifeState.Alive));
            Apply(state, Owner(5, 3, RealtimeCombatLifeState.Dead));
            Assert.That(deaths, Is.EqualTo(new long[] { 2, 5 }));

            // A respawn that was never seen still makes the next death a new one.
            Apply(state, Owner(6, 5, RealtimeCombatLifeState.Dead));
            Assert.That(deaths, Is.EqualTo(new long[] { 2, 5, 6 }));

            // Joining while dead is a death as well.
            var joined = new CombatClientState();
            var joinedDeaths = 0;
            joined.Died += _ => joinedDeaths++;
            Apply(joined, Owner(1, 1, RealtimeCombatLifeState.Dead));
            Assert.That(joinedDeaths, Is.EqualTo(1));
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
                        0,
                        RealtimeCombatAttackStyle.Ranged),
                    new RealtimeCombatEvent(
                        RealtimeCombatEventKind.Hit,
                        5,
                        7,
                        0,
                        12,
                        RealtimeCombatDamageType.Normal,
                        0,
                        RealtimeCombatAttackStyle.Melee),
                    new RealtimeCombatEvent(
                        RealtimeCombatEventKind.Died,
                        5,
                        7,
                        0,
                        0,
                        RealtimeCombatDamageType.Normal,
                        0,
                        RealtimeCombatAttackStyle.Melee)
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

            using (var harness = new RealtimeHubHarness())
            {
                foreach (var messageType in combatMessages)
                {
                    Assert.That(
                        harness.Hub.Messages.IsDropped(messageType, RealtimeConnectionState.Leaving),
                        Is.True,
                        messageType.ToString());
                }
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

            using (var harness = new RealtimeHubHarness())
            {
                foreach (RealtimeMessageType messageType in Enum.GetValues(typeof(RealtimeMessageType)))
                {
                    if (!RealtimeProtocol.TryGetDescriptor(messageType, out var descriptor)
                        || descriptor.Direction != RealtimeMessageDirection.ServerToClient
                        || descriptor.Delivery != RealtimeDelivery.ReliableOrdered)
                    {
                        continue;
                    }

                    Assert.That(
                        harness.Hub.Messages.IsDropped(messageType, RealtimeConnectionState.Leaving),
                        Is.EqualTo(!handledWhileLeaving.Contains(messageType)),
                        messageType.ToString());
                }
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
                0,
                0,
                0,
                0);
        }

        // A living player out of combat that regains the amount at the tick and every second
        // after it.
        private static RealtimeCombatantState Regenerating(
            ulong entityId,
            long revision,
            int health,
            uint tick,
            int amount = 3)
        {
            return new RealtimeCombatantState(
                entityId,
                revision,
                health,
                50,
                RealtimeCombatLifeState.Alive,
                false,
                0,
                0,
                amount,
                tick);
        }

        // The worker's tick exactly at the given one.
        private static CombatServerClock Clock(uint tick)
        {
            return new CombatServerClock(tick, 0f, TickRate);
        }

        private static void Apply(CombatClientState state, RealtimeCombatantState combatant)
        {
            Assert.That(state.TryApplyCombatant(combatant, out var error), Is.True, error);
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
                100,
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
