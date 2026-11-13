using System;
using LiteNetLib;
using UnityEngine;

namespace Mmorpg.Client.Networking
{
    // The client's one network entry point: the persistent MonoBehaviour that owns the transport
    // and the frame. It composes the parts, polls the transport once per Update, and exposes the
    // session, the diagnostics, and a channel per feature, which ClientBootstrap hands to the
    // features as narrow interfaces.
    public sealed class RealtimeSimulationClient : MonoBehaviour
    {
        private IRealtimeTransport transport;
        private RealtimeDiagnostics diagnostics;
        private RealtimeConnection connection;
        private RealtimeSession session;
        private RealtimeMessageTable messages;
        private RealtimeMovementChannel movement;
        private RealtimePlayerChannel players;
        private RealtimeLookChannel looks;
        private RealtimeItemChannel items;
        private RealtimeCorpseChannel corpses;
        private RealtimeWorldActorChannel worldActors;
        private RealtimeWorldInteractionChannel worldInteractions;
        private RealtimeCombatChannel combat;
        private RealtimeQuestChannel quests;
        private RealtimeAdminChannel admin;

        public RealtimeSession Session
        {
            get { return Composed().session; }
        }

        public IRealtimeDiagnostics Diagnostics
        {
            get { return Composed().diagnostics; }
        }

        public IRealtimeMovementChannel Movement
        {
            get { return Composed().movement; }
        }

        public IRealtimePlayerChannel Players
        {
            get { return Composed().players; }
        }

        public IRealtimeLookChannel Looks
        {
            get { return Composed().looks; }
        }

        public IRealtimeItemChannel Items
        {
            get { return Composed().items; }
        }

        public IRealtimeCorpseChannel Corpses
        {
            get { return Composed().corpses; }
        }

        public IRealtimeWorldActorChannel WorldActors
        {
            get { return Composed().worldActors; }
        }

        public IRealtimeWorldInteractionChannel WorldInteractions
        {
            get { return Composed().worldInteractions; }
        }

        public IRealtimeCombatChannel Combat
        {
            get { return Composed().combat; }
        }

        public IRealtimeQuestChannel Quests
        {
            get { return Composed().quests; }
        }

        public IRealtimeAdminChannel Admin
        {
            get { return Composed().admin; }
        }

        // The message table, for tests that read what it drops.
        internal RealtimeMessageTable Messages
        {
            get { return Composed().messages; }
        }

        // Gives the hub its transport and its clock before anything reads a part, which
        // otherwise composes the hub with the LiteNetLib transport and Unity's realtime clock.
        // EditMode tests pass fakes.
        internal void Initialize(IRealtimeTransport realtimeTransport, Func<float> clock)
        {
            if (realtimeTransport == null)
            {
                throw new ArgumentNullException(nameof(realtimeTransport));
            }

            if (clock == null)
            {
                throw new ArgumentNullException(nameof(clock));
            }

            if (transport != null)
            {
                throw new InvalidOperationException("The realtime client already has a transport.");
            }

            Compose(realtimeTransport, clock);
        }

        private void Awake()
        {
            Composed();
        }

        // One frame: the poll raises every transport event and handles every packet that
        // arrived, then the session checks its deadline and finishes a session that ended.
        // Internal so EditMode tests can run a frame.
        internal void Update()
        {
            connection.Poll();
            session.EndFrame();
        }

        private void OnDestroy()
        {
            session?.Shutdown();
        }

        private void OnApplicationQuit()
        {
            session?.Shutdown();
        }

        private RealtimeSimulationClient Composed()
        {
            if (transport == null)
            {
                Compose(new LiteNetLibRealtimeTransport(), () => Time.realtimeSinceStartup);
            }

            return this;
        }

        // Every part registers its message types; the table then checks that none is missing.
        private void Compose(IRealtimeTransport realtimeTransport, Func<float> clock)
        {
            transport = realtimeTransport;
            connection = new RealtimeConnection(transport);
            diagnostics = new RealtimeDiagnostics(connection, clock);
            var presence = new RealtimePresenceRegistry();
            session = new RealtimeSession(connection, clock, diagnostics, presence);
            var sender = new RealtimeSender(connection, session, diagnostics);
            messages = new RealtimeMessageTable(session);
            session.RegisterMessages(messages);
            movement = new RealtimeMovementChannel(session, diagnostics, sender, messages);
            players = new RealtimePlayerChannel(session, presence, messages);
            looks = new RealtimeLookChannel(session, presence, sender, messages);
            items = new RealtimeItemChannel(session, movement, sender, messages);
            corpses = new RealtimeCorpseChannel(session, movement, sender, messages);
            worldActors = new RealtimeWorldActorChannel(session, presence, messages);
            worldInteractions = new RealtimeWorldInteractionChannel(session, sender, messages);
            combat = new RealtimeCombatChannel(session, presence, sender, messages);
            quests = new RealtimeQuestChannel(session, sender, messages);
            admin = new RealtimeAdminChannel(session, sender, messages);
            messages.Seal();

            transport.Connected += session.OnTransportConnected;
            transport.Disconnected += session.OnTransportDisconnected;
            transport.Received += OnTransportReceived;
            transport.NetworkError += session.OnTransportError;
        }

        private void OnTransportReceived(byte[] packet, byte channel, DeliveryMethod deliveryMethod)
        {
            diagnostics.RecordReceivedPacket(packet.Length);
            messages.Dispatch(packet, channel, deliveryMethod);
        }
    }
}
