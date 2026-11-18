using System;
using System.Collections;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Worlds;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;

namespace Mmorpg.Client.Networking
{
    public enum WorldJoinStatus
    {
        // The worker accepted the join.
        Joined,

        // GameService refused the join ticket or did not answer.
        TicketRefused,

        // GameService answered with a ticket that names no shard.
        TicketIncomplete,

        // The caller's check of the ticket stopped the join.
        TicketDeclined,

        // The worker refused the join, or the connection failed before it was accepted.
        JoinFailed
    }

    // How one ticket-and-join ended: the ticket when there was one, and the session or the error.
    public sealed class WorldJoinResult
    {
        private WorldJoinResult(
            WorldJoinStatus status,
            JoinShardResponse ticket,
            ApiError ticketError,
            RealtimeClientError joinError,
            ActiveSimulationSessionResponse session)
        {
            Status = status;
            Ticket = ticket;
            TicketError = ticketError;
            JoinError = joinError;
            Session = session;
        }

        public WorldJoinStatus Status { get; }

        public JoinShardResponse Ticket { get; }

        public ApiError TicketError { get; }

        public RealtimeClientError JoinError { get; }

        public ActiveSimulationSessionResponse Session { get; }

        internal static WorldJoinResult Of(WorldJoinStatus status, JoinShardResponse ticket)
        {
            return new WorldJoinResult(status, ticket, null, null, null);
        }

        internal static WorldJoinResult Refused(ApiError error)
        {
            return new WorldJoinResult(WorldJoinStatus.TicketRefused, null, error, null, null);
        }

        internal static WorldJoinResult Failed(JoinShardResponse ticket, RealtimeClientError error)
        {
            return new WorldJoinResult(WorldJoinStatus.JoinFailed, ticket, null, error, null);
        }

        internal static WorldJoinResult Joined(JoinShardResponse ticket, ActiveSimulationSessionResponse session)
        {
            return new WorldJoinResult(WorldJoinStatus.Joined, ticket, null, null, session);
        }
    }

    // Asks GameService for a join ticket for one character on one shard and joins the worker with
    // it: the part of entering a World that Enter World on CharacterSelect and the reconnect
    // share. The request names the versions of this client, so GameService refuses a client that
    // does not fit the Shard's worker before it issues a ticket. The caller checks the ticket
    // before the join, decides what a failure means, and enters the World; nothing here changes
    // the client session.
    public sealed class WorldJoin
    {
        private readonly IClientSession clientSession;
        private readonly IJoinTicketApi tickets;
        private readonly RealtimeSession realtimeSession;

        public WorldJoin(IClientSession session, IJoinTicketApi ticketApi, RealtimeSession realtime)
        {
            clientSession = session ?? throw new ArgumentNullException(nameof(session));
            tickets = ticketApi ?? throw new ArgumentNullException(nameof(ticketApi));
            realtimeSession = realtime ?? throw new ArgumentNullException(nameof(realtime));
        }

        // worldId is the World that the caller believes the Shard runs, whose baked collision
        // revision the request names. acceptTicket sees a complete ticket before the join and
        // returns false to stop there.
        public IEnumerator Run(
            string characterId,
            string characterName,
            string shardId,
            string worldId,
            Func<JoinShardResponse, bool> acceptTicket,
            Action<WorldJoinResult> onDone)
        {
            if (onDone == null)
            {
                throw new ArgumentNullException(nameof(onDone));
            }

            ClientLog.Info(
                ClientLogCategory.Client,
                "Character '" + characterName + "' (" + characterId + ") is requesting access to shard '"
                + shardId + "'.");

            JoinShardResponse ticket = null;
            ApiError ticketError = null;
            yield return tickets.CreateJoinTicket(
                clientSession.Settings.GameServiceBaseUrl,
                clientSession.SessionToken,
                shardId,
                CreateRequest(characterId, worldId),
                value => ticket = value,
                value => ticketError = value);

            if (ticketError != null)
            {
                onDone(WorldJoinResult.Refused(ticketError));
                yield break;
            }

            if (ticket == null || ticket.shard == null)
            {
                onDone(WorldJoinResult.Of(WorldJoinStatus.TicketIncomplete, ticket));
                yield break;
            }

            if (acceptTicket != null && !acceptTicket(ticket))
            {
                onDone(WorldJoinResult.Of(WorldJoinStatus.TicketDeclined, ticket));
                yield break;
            }

            ClientLog.Info(
                ClientLogCategory.Auth,
                "GameService issued a short-lived join ticket for character '" + characterName
                + "' on shard '" + shardId + "'.");

            ActiveSimulationSessionResponse session = null;
            RealtimeClientError joinError = null;
            yield return realtimeSession.Join(
                ticket,
                value => session = value,
                value => joinError = value);

            if (joinError == null && session != null)
            {
                onDone(WorldJoinResult.Joined(ticket, session));
                yield break;
            }

            onDone(WorldJoinResult.Failed(
                ticket,
                joinError ?? new RealtimeClientError(
                    RealtimeClientErrorKind.Network,
                    "connection_closed",
                    "The join ended without a session.")));
        }

        // The join request of this client: its protocol version and simulation revision, and the
        // collision revision that it baked for the World, when it has the World's collision data.
        // A client without it names no World, and GameService leaves the data to the later check.
        internal static JoinShardRequest CreateRequest(string characterId, string worldId)
        {
            var request = new JoinShardRequest
            {
                characterId = characterId,
                protocolVersion = RealtimeProtocol.Version,
                simulationRevision = GameSimulationCompatibility.Revision
            };
            if (UnityWorldCollisionLoader.TryReadRevision(worldId, out var revision, out _))
            {
                request.worldId = worldId;
                request.collisionRevision = revision;
            }

            return request;
        }
    }
}
