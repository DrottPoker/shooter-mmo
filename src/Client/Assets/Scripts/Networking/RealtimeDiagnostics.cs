using System;
using System.Collections.Generic;
using Mmorpg.GameSimulation;
using UnityEngine;

namespace Mmorpg.Client.Networking
{
    // What the client observes about its realtime connection, for the Debug menu and the combat
    // clock. Every counter starts again with each session.
    public interface IRealtimeDiagnostics
    {
        string ConnectedHost { get; }

        int ConnectedPort { get; }

        // The round trip time to the connected server, or 0 without one.
        int RoundTripTimeMilliseconds { get; }

        // The server tick of the newest owner snapshot, or 0 before the first one.
        uint LatestServerTick { get; }

        // Seconds since the owner snapshot of the newest sequence arrived, or -1 before one did.
        float LatestSnapshotAgeSeconds { get; }

        long SnapshotPacketsReceived { get; }

        // Owner snapshot sequences seen; an older or repeated sequence counts no frame.
        long SnapshotFramesReceived { get; }

        // The owner snapshot sequences skipped between the frames received.
        long EstimatedMissingSnapshotFrames { get; }

        long RealtimePacketsReceived { get; }

        long RealtimeBytesReceived { get; }

        long RealtimePacketsSent { get; }

        long RealtimeBytesSent { get; }

        // Whether reliable intents are timed to their results. It is off unless a measurement
        // turns it on, so a client that measures nothing keeps no record of its intents.
        bool IntentTimingEnabled { get; set; }

        // Raised for every reliable intent sent while the timing is on, with its kind of
        // RealtimeIntentKinds.
        event Action<string> IntentSent;

        // Raised when the result of a timed intent arrives: its kind and the seconds since it was
        // sent. The result of an intent sent before the timing started raises nothing.
        event Action<string, float> IntentAnswered;
    }

    // The kinds of reliable intents, one per channel that sends them.
    public static class RealtimeIntentKinds
    {
        public const string Item = "item";
        public const string Corpse = "corpse";
        public const string WorldInteraction = "world_interaction";
        public const string Combat = "combat";
        public const string Quest = "quest";
        public const string Admin = "admin";
        public const string Appearance = "appearance";
    }

    // The counters, the owner snapshot sequence and its loss estimate, the newest server tick,
    // and the snapshot age. The session clears it with every session.
    internal sealed class RealtimeDiagnostics : IRealtimeDiagnostics, IRealtimeSessionScoped
    {
        private readonly RealtimeConnection connection;
        private readonly Func<float> realtimeClock;
        private readonly Dictionary<Guid, PendingIntent> pendingIntents = new Dictionary<Guid, PendingIntent>();
        private bool intentTimingEnabled;
        private bool hasSnapshotSequence;
        private uint latestSnapshotSequence;
        private float latestSnapshotReceivedAt = -1f;

        public RealtimeDiagnostics(RealtimeConnection connection, Func<float> realtimeClock)
        {
            this.connection = connection;
            this.realtimeClock = realtimeClock;
        }

        public string ConnectedHost
        {
            get { return connection.Host; }
        }

        public int ConnectedPort
        {
            get { return connection.Port; }
        }

        // LiteNetLib's Ping is half the round trip, so the round trip is read directly.
        public int RoundTripTimeMilliseconds
        {
            get { return connection.RoundTripTimeMilliseconds; }
        }

        public uint LatestServerTick { get; private set; }

        public float LatestSnapshotAgeSeconds
        {
            get
            {
                return latestSnapshotReceivedAt < 0f
                    ? -1f
                    : Mathf.Max(0f, realtimeClock() - latestSnapshotReceivedAt);
            }
        }

        public long SnapshotPacketsReceived { get; private set; }

        public long SnapshotFramesReceived { get; private set; }

        public long EstimatedMissingSnapshotFrames { get; private set; }

        public long RealtimePacketsReceived { get; private set; }

        public long RealtimeBytesReceived { get; private set; }

        public long RealtimePacketsSent { get; private set; }

        public long RealtimeBytesSent { get; private set; }

        public bool IntentTimingEnabled
        {
            get { return intentTimingEnabled; }
            set
            {
                intentTimingEnabled = value;
                if (!value)
                {
                    pendingIntents.Clear();
                }
            }
        }

        public event Action<string> IntentSent;

        public event Action<string, float> IntentAnswered;

        public void RecordIntentSent(Guid operationId, string kind)
        {
            if (!intentTimingEnabled)
            {
                return;
            }

            pendingIntents[operationId] = new PendingIntent(kind, realtimeClock());
            IntentSent?.Invoke(kind);
        }

        public void RecordIntentAnswered(Guid operationId)
        {
            if (!intentTimingEnabled || !pendingIntents.TryGetValue(operationId, out var pending))
            {
                return;
            }

            pendingIntents.Remove(operationId);
            IntentAnswered?.Invoke(pending.Kind, Mathf.Max(0f, realtimeClock() - pending.SentAt));
        }

        public void RecordReceivedPacket(int byteCount)
        {
            RealtimePacketsReceived++;
            RealtimeBytesReceived += byteCount;
        }

        public void RecordSentPacket(int byteCount)
        {
            RealtimePacketsSent++;
            RealtimeBytesSent += byteCount;
        }

        public void RecordRemoteSnapshot()
        {
            SnapshotPacketsReceived++;
        }

        // Counts an owner snapshot by its sequence and stamps when the newest sequence arrived.
        // Returns whether its server tick is not older than the newest one, which is when the
        // snapshot is raised.
        public bool RecordOwnerSnapshot(uint snapshotSequence, uint serverTick)
        {
            SnapshotPacketsReceived++;
            if (!hasSnapshotSequence)
            {
                hasSnapshotSequence = true;
                latestSnapshotSequence = snapshotSequence;
                SnapshotFramesReceived++;
            }
            else if (snapshotSequence != latestSnapshotSequence
                && MovementSequence.IsNewer(snapshotSequence, latestSnapshotSequence))
            {
                var sequenceDelta = unchecked(snapshotSequence - latestSnapshotSequence);
                if (sequenceDelta > 1)
                {
                    EstimatedMissingSnapshotFrames += sequenceDelta - 1;
                }

                latestSnapshotSequence = snapshotSequence;
                SnapshotFramesReceived++;
            }

            if (snapshotSequence == latestSnapshotSequence)
            {
                latestSnapshotReceivedAt = realtimeClock();
            }

            if (LatestServerTick == 0
                || serverTick == LatestServerTick
                || MovementSequence.IsNewer(serverTick, LatestServerTick))
            {
                LatestServerTick = serverTick;
                return true;
            }

            return false;
        }

        public void ClearSessionState()
        {
            LatestServerTick = 0;
            SnapshotPacketsReceived = 0;
            SnapshotFramesReceived = 0;
            EstimatedMissingSnapshotFrames = 0;
            RealtimePacketsReceived = 0;
            RealtimeBytesReceived = 0;
            RealtimePacketsSent = 0;
            RealtimeBytesSent = 0;
            hasSnapshotSequence = false;
            latestSnapshotSequence = 0;
            latestSnapshotReceivedAt = -1f;

            // An intent of an ended session gets no result.
            pendingIntents.Clear();
        }

        private readonly struct PendingIntent
        {
            public PendingIntent(string kind, float sentAt)
            {
                Kind = kind;
                SentAt = sentAt;
            }

            public string Kind { get; }

            public float SentAt { get; }
        }
    }
}
