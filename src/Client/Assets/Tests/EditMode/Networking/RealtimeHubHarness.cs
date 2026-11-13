using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Worlds;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mmorpg.Client.Tests.EditMode
{
    // One RealtimeSimulationClient on a recording fake transport and a hand-driven clock. Every
    // event of the session and of each channel and every call into the transport lands on one
    // timeline, and Frame() writes "frame" before it runs one Update, so a test reads what
    // happened and on which frame. Server packets queue until the next frame polls them, as
    // LiteNetLib delivers them.
    internal sealed class RealtimeHubHarness : IDisposable
    {
        public const string FailurePrefix = "Realtime operation failed: ";

        // The hub reads its timeouts from these; no request reaches a GameService.
        public static readonly ClientSettings Settings = new ClientSettings("http://127.0.0.1:9", 10, 10, 5);

        private static string bakedCollisionRevision;

        private readonly GameObject owner;
        private readonly List<string> errorLogs = new List<string>();
        private IEnumerator join;
        private IEnumerator leave;

        public RealtimeHubHarness()
        {
            Application.logMessageReceived += OnLog;
            owner = new GameObject("Realtime hub");
            owner.SetActive(false);
            Hub = owner.AddComponent<RealtimeSimulationClient>();
            Transport = new FakeRealtimeTransport(Timeline.Add);
            Hub.Initialize(Transport, () => Now, Settings);
            Subscribe();
        }

        public RealtimeSimulationClient Hub { get; }

        public FakeRealtimeTransport Transport { get; }

        public float Now { get; set; } = 100f;

        public List<string> Timeline { get; } = new List<string>();

        // The argument of every recorded event in timeline order, by event name.
        public List<KeyValuePair<string, object>> Payloads { get; } = new List<KeyValuePair<string, object>>();

        public ActiveSimulationSessionResponse JoinedSession { get; private set; }

        public RealtimeClientError JoinError { get; private set; }

        public bool LeaveSucceeded { get; private set; }

        // The seconds that the accepted leave says the body stays.
        public int LeaveBodySeconds { get; private set; }

        public RealtimeClientError LeaveError { get; private set; }

        public static float TimeoutSeconds
        {
            get { return Settings.RealtimeTimeoutSeconds; }
        }

        // A join must announce the baked collision revision of its World.
        public static string BakedCollisionRevision
        {
            get
            {
                if (bakedCollisionRevision == null)
                {
                    Assert.That(
                        UnityWorldCollisionLoader.TryCreateStream(
                            RealtimeHubMessages.WorldId,
                            out var stream,
                            out var error),
                        Is.True,
                        error);
                    bakedCollisionRevision = stream.CollisionWorld.Revision;
                }

                return bakedCollisionRevision;
            }
        }

        public static JoinShardResponse Placement(string joinTicket = "ticket-1", string host = "127.0.0.1")
        {
            return new JoinShardResponse
            {
                shard = new ShardResponse
                {
                    id = RealtimeHubMessages.ShardId,
                    worldId = RealtimeHubMessages.WorldId
                },
                endpoint = new SimulationEndpointResponse
                {
                    workerId = "worker-1",
                    runtimeId = "runtime-1",
                    host = host,
                    udpPort = 27015,
                    protocolVersion = RealtimeProtocol.Version,
                    simulationRevision = GameSimulationCompatibility.Revision,
                    collisionRevision = BakedCollisionRevision
                },
                characterId = RealtimeHubMessages.CharacterId.ToString("D"),
                joinTicket = joinTicket,
                expiresAt = DateTime.UtcNow.AddMinutes(5).ToString("O")
            };
        }

        public static RealtimeJoinAccepted JoinAccepted(bool isReconnect = false)
        {
            return RealtimeHubMessages.JoinAccepted(
                GameSimulationCompatibility.Revision,
                BakedCollisionRevision,
                isReconnect);
        }

        public void Frame()
        {
            Timeline.Add("frame");
            Hub.Update();
        }

        // Queues a packet on the channel and delivery its protocol descriptor names.
        public void Queue(byte[] packet)
        {
            Assert.That(RealtimeProtocol.TryReadMessageType(packet, out var type), Is.True);
            Assert.That(RealtimeProtocol.TryGetDescriptor(type, out var descriptor), Is.True);
            Transport.QueueReceived(packet, descriptor.Channel, DeliveryOf(descriptor.Delivery));
        }

        public void Deliver(byte[] packet)
        {
            Queue(packet);
            Frame();
        }

        public void Deliver(byte[] packet, byte channel, DeliveryMethod deliveryMethod)
        {
            Transport.QueueReceived(packet, channel, deliveryMethod);
            Frame();
        }

        public bool BeginJoin()
        {
            return BeginJoin(Placement());
        }

        // Starts a join and runs it to its first wait; returns whether it still waits.
        public bool BeginJoin(JoinShardResponse placement)
        {
            JoinedSession = null;
            JoinError = null;
            join = Hub.Session.Join(
                placement,
                session => JoinedSession = session,
                error => JoinError = error);
            return join.MoveNext();
        }

        // Resumes the join routine as Unity does once per frame; returns whether it still waits.
        public bool StepJoin()
        {
            return join.MoveNext();
        }

        // Connects the transport, so the hub sends its join request and waits for the answer.
        public void BeginJoinAndConnect()
        {
            Assert.That(BeginJoin(), Is.True);
            Transport.QueueConnected();
            Frame();
            Assert.That(Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Joining));
        }

        // Runs a whole join and then clears the timeline, so a test starts in a joined session.
        public void JoinSession(RealtimeJoinAccepted accepted = null)
        {
            BeginJoinAndConnect();
            Deliver(RealtimeProtocol.EncodeJoinAccepted(accepted ?? JoinAccepted()));
            Assert.That(StepJoin(), Is.False);
            Assert.That(JoinError, Is.Null, JoinError?.ToDiagnostic());
            Assert.That(Hub.Session.State, Is.EqualTo(RealtimeConnectionState.Joined));
            Assert.That(TakeFailures(), Is.Empty);
            Clear();
        }

        public bool BeginLeave(string simulationSessionId = null)
        {
            LeaveSucceeded = false;
            LeaveBodySeconds = -1;
            LeaveError = null;
            leave = Hub.Session.Leave(
                simulationSessionId ?? RealtimeHubMessages.SessionId.ToString("D"),
                bodySeconds =>
                {
                    LeaveSucceeded = true;
                    LeaveBodySeconds = bodySeconds;
                },
                error => LeaveError = error);
            return leave.MoveNext();
        }

        public bool StepLeave()
        {
            return leave.MoveNext();
        }

        public void Clear()
        {
            Timeline.Clear();
            Payloads.Clear();
            Transport.Sent.Clear();
        }

        // The payload of the latest event of that name.
        public object LastPayload(string eventName)
        {
            var match = Payloads.LastOrDefault(entry => entry.Key == eventName);
            Assert.That(match.Key, Is.EqualTo(eventName), eventName + " was not raised.");
            return match.Value;
        }

        // The error logs since the last call, a failure of the hub as "CATEGORY code". The hub
        // logs every failure as an error, which fails a test unless the test reads it here.
        public string[] TakeFailures()
        {
            foreach (var message in errorLogs)
            {
                LogAssert.Expect(LogType.Error, message);
            }

            var failures = errorLogs.Select(DescribeError).ToArray();
            errorLogs.Clear();
            return failures;
        }

        public void Dispose()
        {
            Application.logMessageReceived -= OnLog;
            UnityEngine.Object.DestroyImmediate(owner);
        }

        private static DeliveryMethod DeliveryOf(RealtimeDelivery delivery)
        {
            switch (delivery)
            {
                case RealtimeDelivery.Unreliable:
                    return DeliveryMethod.Unreliable;
                case RealtimeDelivery.Sequenced:
                    return DeliveryMethod.Sequenced;
                default:
                    return DeliveryMethod.ReliableOrdered;
            }
        }

        private static string DescribeError(string message)
        {
            var categoryEnd = message.IndexOf(']');
            var failure = message.IndexOf(FailurePrefix, StringComparison.Ordinal);
            var codeStart = message.LastIndexOf('[');
            if (!message.StartsWith("[", StringComparison.Ordinal)
                || categoryEnd < 0
                || failure < 0
                || codeStart <= failure
                || !message.EndsWith("]", StringComparison.Ordinal))
            {
                return message;
            }

            return message.Substring(1, categoryEnd - 1) + " "
                + message.Substring(codeStart + 1, message.Length - codeStart - 2);
        }

        private static string Code(RealtimeClientError error)
        {
            return error == null ? "null" : error.Code;
        }

        private void OnLog(string message, string stackTrace, LogType type)
        {
            if (type == LogType.Error)
            {
                errorLogs.Add(message);
            }
        }

        private void Record(string entry, string eventName, object payload)
        {
            Timeline.Add(entry);
            Payloads.Add(new KeyValuePair<string, object>(eventName, payload));
        }

        private void Subscribe()
        {
            Hub.Session.SessionJoined += () => Record("SessionJoined", "SessionJoined", null);
            Hub.Session.SessionEnded += error => Record("SessionEnded " + Code(error), "SessionEnded", error);
            Hub.Session.UnexpectedlyDisconnected += error =>
                Record("UnexpectedlyDisconnected " + Code(error), "UnexpectedlyDisconnected", error);
            Hub.Movement.SimulationSnapshotReceived += snapshot => Record(
                "SimulationSnapshotReceived " + snapshot.SnapshotSequence + "/" + snapshot.ServerTick,
                "SimulationSnapshotReceived",
                snapshot);
            Hub.Movement.OwnerSimulationSnapshotReceived += snapshot => Record(
                "OwnerSimulationSnapshotReceived " + snapshot.SnapshotSequence + "/" + snapshot.ServerTick,
                "OwnerSimulationSnapshotReceived",
                snapshot);
            Hub.Movement.CarryStateChanged += carry =>
                Record("CarryStateChanged " + carry.ItemStateRevision, "CarryStateChanged", carry);
            Hub.Items.ItemOperationCompleted += result => Record("ItemOperationCompleted", "ItemOperationCompleted", result);
            Hub.Corpses.CorpsePresenceChunkReceived += chunk =>
                Record("CorpsePresenceChunkReceived", "CorpsePresenceChunkReceived", chunk);
            Hub.Corpses.CorpseInteractionCompleted += result =>
                Record("CorpseInteractionCompleted", "CorpseInteractionCompleted", result);
            Hub.Corpses.CorpseViewStateChunkReceived += chunk =>
                Record("CorpseViewStateChunkReceived", "CorpseViewStateChunkReceived", chunk);
            Hub.Corpses.CorpseViewClosed += closed => Record("CorpseViewClosed", "CorpseViewClosed", closed);
            Hub.Players.EntitySpawned += spawn => Record("EntitySpawned " + spawn.EntityId, "EntitySpawned", spawn);
            Hub.Players.EntityDespawned += despawn => Record(
                "EntityDespawned " + despawn.EntityId + " " + despawn.Reason,
                "EntityDespawned",
                despawn);
            Hub.WorldActors.WorldActorSpawned += spawn =>
                Record("WorldActorSpawned " + spawn.EntityId, "WorldActorSpawned", spawn);
            Hub.WorldActors.WorldActorStateReceived += state => Record(
                "WorldActorStateReceived " + state.EntityId + " " + state.StateRevision,
                "WorldActorStateReceived",
                state);
            Hub.WorldActors.WorldActorDespawned += despawn => Record(
                "WorldActorDespawned " + despawn.EntityId + " " + despawn.Reason,
                "WorldActorDespawned",
                despawn);
            Hub.WorldInteractions.WorldInteractionOpened += opened =>
                Record("WorldInteractionOpened", "WorldInteractionOpened", opened);
            Hub.WorldInteractions.WorldInteractionCompleted += result =>
                Record("WorldInteractionCompleted", "WorldInteractionCompleted", result);
            Hub.WorldInteractions.WorldInteractionClosed += closed =>
                Record("WorldInteractionClosed", "WorldInteractionClosed", closed);
            Hub.WorldInteractions.GatheringStateReceived += state => Record("GatheringStateReceived", "GatheringStateReceived", state);
            Hub.Combat.CombatIntentCompleted += result => Record("CombatIntentCompleted", "CombatIntentCompleted", result);
            Hub.Combat.CombatantStateReceived += state =>
                Record("CombatantStateReceived " + state.EntityId, "CombatantStateReceived", state);
            Hub.Combat.CombatEventBatchReceived += batch =>
                Record("CombatEventBatchReceived", "CombatEventBatchReceived", batch);
            Hub.Combat.OwnerCombatStateReceived += state =>
                Record("OwnerCombatStateReceived", "OwnerCombatStateReceived", state);
            Hub.Items.OwnerItemQuantityChanged += change =>
                Record("OwnerItemQuantityChanged", "OwnerItemQuantityChanged", change);
            Hub.Items.OwnerItemStateChanged += change => Record("OwnerItemStateChanged", "OwnerItemStateChanged", change);
            Hub.Looks.EntityAppearanceChanged += change => Record(
                "EntityAppearanceChanged " + change.EntityId,
                "EntityAppearanceChanged",
                change);
            Hub.Looks.AppearanceIntentCompleted += result =>
                Record("AppearanceIntentCompleted", "AppearanceIntentCompleted", result);
            // The event has no argument; the state it announces is the admin channel's AdminState.
            Hub.Admin.AdminStateChanged += () => Record("AdminStateChanged", "AdminStateChanged", Hub.Admin.AdminState);
            Hub.Admin.AdminIntentCompleted += result => Record("AdminIntentCompleted", "AdminIntentCompleted", result);
            Hub.Quests.QuestLogChanged += change => Record("QuestLogChanged", "QuestLogChanged", change);
            Hub.Quests.QuestIntentCompleted += result => Record("QuestIntentCompleted", "QuestIntentCompleted", result);
            Hub.Quests.QuestDropInCorpse += drop => Record("QuestDropInCorpse", "QuestDropInCorpse", drop);
        }
    }
}
