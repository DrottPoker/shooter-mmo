using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Mmorpg.Client.Networking;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The one recorder of what the client measures about itself. It always keeps the numbers that
    // the Debug menu shows: frame times, the round trip, snapshot age, rate, and loss, packet and
    // byte rates, and the corrections of the local player's prediction. On request it also records
    // what a network test needs: the distribution of corrections, rubber banding of the presented
    // local player, remote entities that ran out of snapshots, silences of the line, and the time
    // from each reliable intent to its result, and writes one JSON line per interval and a summary
    // to a file below the persistent data path. Nothing of that is measured or written unless a
    // recording runs. ClientMeasurementDriver calls Frame once a frame after the presentation.
    public sealed class ClientMeasurements : IDisposable
    {
        public const string FolderName = "measurements";
        public const float DefaultIntervalSeconds = 5f;

        // Unexplained movement of a frame from this many meters counts as a visible jump.
        public const float VisibleJumpMeters = 0.25f;

        // An owner snapshot gap from this long is a silence of the line.
        public const float SilenceSeconds = 1f;

        // Corrections and jumps this long after a silence count as its aftermath.
        public const float AfterSilenceSeconds = 2f;

        private readonly IRealtimeDiagnostics diagnostics;
        private readonly IRealtimeMovementChannel movement;
        private readonly LocalPlayerSource localPlayers;
        private readonly string folder;
        private readonly ClientTelemetryWindow telemetry = new ClientTelemetryWindow();
        private readonly List<IRemoteMotionSource> remoteSources = new List<IRemoteMotionSource>();
        private readonly List<RemoteMotionSample> remoteSamples = new List<RemoteMotionSample>();
        private readonly Action<float, bool> onCorrected;
        private readonly Action<string> onIntentSent;
        private readonly Action<string, float> onIntentAnswered;
        private LocalPlayerController subscribedPlayer;
        private Recording recording;

        public ClientMeasurements(
            IRealtimeDiagnostics diagnostics,
            IRealtimeMovementChannel movement,
            LocalPlayerSource localPlayers,
            string folder)
        {
            this.diagnostics = diagnostics;
            this.movement = movement;
            this.localPlayers = localPlayers;
            this.folder = folder;
            onCorrected = OnPositionCorrected;
            onIntentSent = kind => recording?.IntentSent(kind);
            onIntentAnswered = (kind, seconds) => recording?.IntentAnswered(kind, seconds);
        }

        // The frame and network rates of the last moments.
        public ClientTelemetryWindow Telemetry
        {
            get { return telemetry; }
        }

        public string ConnectedHost
        {
            get { return diagnostics != null ? diagnostics.ConnectedHost : string.Empty; }
        }

        public int ConnectedPort
        {
            get { return diagnostics != null ? diagnostics.ConnectedPort : 0; }
        }

        public uint LatestServerTick
        {
            get { return diagnostics != null ? diagnostics.LatestServerTick : 0; }
        }

        public int RoundTripMilliseconds
        {
            get { return diagnostics != null ? diagnostics.RoundTripTimeMilliseconds : 0; }
        }

        public float LatestSnapshotAgeSeconds
        {
            get { return diagnostics != null ? diagnostics.LatestSnapshotAgeSeconds : -1f; }
        }

        public long SnapshotPacketsReceived
        {
            get { return diagnostics != null ? diagnostics.SnapshotPacketsReceived : 0; }
        }

        public long SnapshotFramesReceived
        {
            get { return diagnostics != null ? diagnostics.SnapshotFramesReceived : 0; }
        }

        public long EstimatedMissingSnapshotFrames
        {
            get { return diagnostics != null ? diagnostics.EstimatedMissingSnapshotFrames : 0; }
        }

        public float EstimatedSnapshotLossPercent
        {
            get { return EstimateSnapshotLossPercent(SnapshotFramesReceived, EstimatedMissingSnapshotFrames); }
        }

        // The corrections of the local player since its movement was bound.
        public long Reconciliations
        {
            get { return Player != null ? Player.ReconciliationCount : 0; }
        }

        public long HardSnaps
        {
            get { return Player != null ? Player.HardReconciliationCount : 0; }
        }

        public float LatestCorrectionMeters
        {
            get { return Player != null ? Player.LatestReconciliationDistance : 0f; }
        }

        public bool IsRecording
        {
            get { return recording != null; }
        }

        // The file of the running recording, or of the last one.
        public string RecordingPath { get; private set; } = string.Empty;

        // The summary of the running recording so far, or of the last one, or null.
        public ClientMeasurementRecord LatestSummary
        {
            get { return recording != null ? recording.Summary(clockNow) : lastSummary; }
        }

        private ClientMeasurementRecord lastSummary;
        private float clockNow;

        private LocalPlayerController Player
        {
            get { return localPlayers != null ? localPlayers.Controller : null; }
        }

        public static float EstimateSnapshotLossPercent(long receivedFrames, long estimatedMissingFrames)
        {
            var expectedFrames = receivedFrames + estimatedMissingFrames;
            if (receivedFrames < 0 || estimatedMissingFrames < 0 || expectedFrames <= 0)
            {
                return 0f;
            }

            return (float)(estimatedMissingFrames * 100d / expectedFrames);
        }

        // The other players and the Mobs of the World scene that is shown; a destroyed source is
        // skipped.
        public void SetRemoteSources(params IRemoteMotionSource[] sources)
        {
            remoteSources.Clear();
            if (sources == null)
            {
                return;
            }

            foreach (var source in sources)
            {
                if (source != null)
                {
                    remoteSources.Add(source);
                }
            }
        }

        // Starts recording into a new file below the persistent data path and returns its path.
        // A recording that runs already goes on.
        public string StartRecording(string label, float intervalSeconds = DefaultIntervalSeconds)
        {
            if (recording != null)
            {
                return RecordingPath;
            }

            var name = SafeLabel(label);
            Directory.CreateDirectory(folder);
            RecordingPath = Path.Combine(
                folder,
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + name + ".jsonl");
            recording = new Recording(name, Mathf.Max(0.25f, intervalSeconds), clockNow, new StreamWriter(RecordingPath, false));
            if (diagnostics != null)
            {
                diagnostics.IntentSent += onIntentSent;
                diagnostics.IntentAnswered += onIntentAnswered;
                diagnostics.IntentTimingEnabled = true;
            }

            return RecordingPath;
        }

        // Ends the recording, writes its last interval and its summary, and returns the summary.
        public ClientMeasurementRecord StopRecording()
        {
            if (recording == null)
            {
                return lastSummary;
            }

            if (diagnostics != null)
            {
                diagnostics.IntentTimingEnabled = false;
                diagnostics.IntentSent -= onIntentSent;
                diagnostics.IntentAnswered -= onIntentAnswered;
            }

            lastSummary = recording.Finish(clockNow);
            recording = null;
            return lastSummary;
        }

        // One frame, after every presentation moved: realtime seconds, the unscaled frame time for
        // the frame statistics, and the scaled one that the presentation moved by.
        public void Frame(float now, float unscaledDeltaSeconds, float deltaSeconds)
        {
            clockNow = now;
            telemetry.RecordFrame(unscaledDeltaSeconds);
            if (diagnostics != null)
            {
                telemetry.SampleNetwork(
                    now,
                    diagnostics.RealtimePacketsReceived,
                    diagnostics.RealtimeBytesReceived,
                    diagnostics.RealtimePacketsSent,
                    diagnostics.RealtimeBytesSent,
                    diagnostics.SnapshotPacketsReceived,
                    diagnostics.SnapshotFramesReceived);
            }
            else
            {
                telemetry.ResetNetwork();
            }

            FollowPlayer();
            if (recording == null)
            {
                return;
            }

            var player = Player;
            var bound = player != null && player.IsServerAuthoritative;
            var position = bound ? player.transform.position : Vector3.zero;
            var session = movement != null ? movement.MovementSession : null;
            var walkSpeed = session != null ? session.PredictionSettings.WalkSpeed : 0f;
            remoteSamples.Clear();
            foreach (var source in remoteSources)
            {
                // A source of a scene that is gone is a destroyed component.
                if (source is UnityEngine.Object component && component == null)
                {
                    continue;
                }

                source.CollectRemoteMotion(remoteSamples);
            }

            recording.Frame(
                now,
                unscaledDeltaSeconds,
                deltaSeconds,
                player,
                bound,
                position,
                walkSpeed,
                diagnostics,
                remoteSamples);
        }

        public void Dispose()
        {
            StopRecording();
            if (subscribedPlayer != null)
            {
                subscribedPlayer.PositionCorrected -= onCorrected;
            }

            subscribedPlayer = null;
        }

        private void FollowPlayer()
        {
            var player = Player;
            if (ReferenceEquals(player, subscribedPlayer))
            {
                return;
            }

            if (!ReferenceEquals(subscribedPlayer, null))
            {
                subscribedPlayer.PositionCorrected -= onCorrected;
            }

            subscribedPlayer = player;
            if (player != null)
            {
                player.PositionCorrected += onCorrected;
            }
        }

        // A correction of the local player's prediction, which its controller reports.
        internal void OnPositionCorrected(float meters, bool hard)
        {
            recording?.Corrected(meters, hard);
        }

        private static string SafeLabel(string label)
        {
            var text = string.IsNullOrWhiteSpace(label) ? "client" : label.Trim();
            var characters = text.ToCharArray();
            for (var index = 0; index < characters.Length; index++)
            {
                if (!char.IsLetterOrDigit(characters[index]) && characters[index] != '-' && characters[index] != '_')
                {
                    characters[index] = '-';
                }
            }

            return new string(characters);
        }

        // The values of one period, an interval or the whole recording.
        private sealed class Period
        {
            public readonly MeasurementSamples Frames = new MeasurementSamples();
            public readonly MeasurementSamples RoundTrips = new MeasurementSamples();
            public readonly MeasurementSamples Corrections = new MeasurementSamples();
            public readonly MeasurementSamples IntentSeconds = new MeasurementSamples();
            public readonly Dictionary<string, IntentKind> Intents = new Dictionary<string, IntentKind>(StringComparer.Ordinal);
            public float Start;
            public float WorldSeconds;
            public float LargestSnapshotAge;
            public long OwnerFrames;
            public long OwnerMissing;
            public int Silences;
            public float LongestSilence;
            public int HardSnaps;
            public float LargestJump;
            public int JumpFrames;
            public float JumpMeters;
            public float LargestCorrectionAfterSilence;
            public float LargestJumpAfterSilence;
            public float RemoteObserved;
            public float RemoteStarved;
            public int RemoteStarvations;
            public float LongestRemoteStarvation;
            public int IntentsSent;
            public int IntentsAnswered;

            public Period(float start)
            {
                Start = start;
            }

            public IntentKind Kind(string kind)
            {
                if (!Intents.TryGetValue(kind, out var entry))
                {
                    entry = new IntentKind();
                    Intents[kind] = entry;
                }

                return entry;
            }

            public ClientMeasurementRecord ToRecord(string recordKind, string label, float end)
            {
                var duration = Mathf.Max(0f, end - Start);
                var record = new ClientMeasurementRecord
                {
                    kind = recordKind,
                    label = label,
                    startSeconds = Start,
                    durationSeconds = duration,
                    worldSeconds = WorldSeconds,
                    frameMilliseconds = Frames.ToStatistics(),
                    roundTripMilliseconds = RoundTrips.ToStatistics(),
                    largestSnapshotAgeSeconds = LargestSnapshotAge,
                    ownerSnapshotFrames = OwnerFrames,
                    ownerSnapshotFramesMissing = OwnerMissing,
                    ownerSnapshotLossPercent = EstimateSnapshotLossPercent(OwnerFrames, OwnerMissing),
                    silences = Silences,
                    longestSilenceSeconds = LongestSilence,
                    corrections = Corrections.Count,
                    hardSnaps = HardSnaps,
                    hardSnapsPerMinute = WorldSeconds > 0f ? HardSnaps * 60f / WorldSeconds : 0f,
                    correctionMeters = Corrections.ToStatistics(),
                    largestUnexplainedJumpMeters = LargestJump,
                    framesWithUnexplainedJump = JumpFrames,
                    unexplainedJumpMeters = JumpMeters,
                    largestCorrectionAfterSilenceMeters = LargestCorrectionAfterSilence,
                    largestJumpAfterSilenceMeters = LargestJumpAfterSilence,
                    remoteObservedEntitySeconds = RemoteObserved,
                    remoteStarvedEntitySeconds = RemoteStarved,
                    remoteStarvedPercent = RemoteObserved > 0f ? RemoteStarved * 100f / RemoteObserved : 0f,
                    remoteStarvations = RemoteStarvations,
                    longestRemoteStarvationSeconds = LongestRemoteStarvation,
                    intentsSent = IntentsSent,
                    intentsAnswered = IntentsAnswered,
                    intentSeconds = IntentSeconds.ToStatistics()
                };
                var kinds = new List<ClientIntentStatistics>();
                foreach (var pair in Intents)
                {
                    kinds.Add(new ClientIntentStatistics
                    {
                        kind = pair.Key,
                        sent = pair.Value.Sent,
                        answered = pair.Value.Answered,
                        seconds = pair.Value.Seconds.ToStatistics()
                    });
                }

                kinds.Sort((first, second) => string.CompareOrdinal(first.kind, second.kind));
                record.intents = kinds.ToArray();
                return record;
            }
        }

        private sealed class IntentKind
        {
            public readonly MeasurementSamples Seconds = new MeasurementSamples();
            public int Sent;
            public int Answered;
        }

        // A running recording: the current interval, the whole recording, and the file.
        private sealed class Recording
        {
            private readonly string label;
            private readonly float intervalSeconds;
            private readonly StreamWriter writer;
            private readonly RenderedMotionTracker motion = new RenderedMotionTracker();
            private readonly Dictionary<ulong, float> starvedSince = new Dictionary<ulong, float>();
            private readonly HashSet<ulong> starvedNow = new HashSet<ulong>();
            private readonly List<ulong> starvationEnded = new List<ulong>();
            private Period interval;
            private readonly Period total;
            private bool hasOwnerCounters;
            private long previousOwnerFrames;
            private long previousOwnerMissing;
            private bool inSilence;
            private float silenceStart;
            private float afterSilenceUntil = float.NegativeInfinity;
            private float now;

            public Recording(string label, float intervalSeconds, float start, StreamWriter writer)
            {
                this.label = label;
                this.intervalSeconds = intervalSeconds;
                this.writer = writer;
                interval = new Period(start);
                total = new Period(start);
                now = start;
            }

            public void Frame(
                float time,
                float unscaledDeltaSeconds,
                float deltaSeconds,
                LocalPlayerController player,
                bool bound,
                Vector3 position,
                float walkSpeed,
                IRealtimeDiagnostics diagnostics,
                List<RemoteMotionSample> remote)
            {
                now = time;
                if (unscaledDeltaSeconds > 0f)
                {
                    Both(period => period.Frames.Add(unscaledDeltaSeconds * 1000f));
                }

                if (bound)
                {
                    Both(period => period.WorldSeconds += unscaledDeltaSeconds);
                }

                SampleNetwork(diagnostics, bound);
                var jump = motion.Track(player, bound, position.x, position.z, walkSpeed, deltaSeconds);
                if (jump > 0f)
                {
                    Both(period =>
                    {
                        period.LargestJump = Mathf.Max(period.LargestJump, jump);
                        period.JumpMeters += jump;
                        if (jump >= VisibleJumpMeters)
                        {
                            period.JumpFrames++;
                        }

                        if (time <= afterSilenceUntil)
                        {
                            period.LargestJumpAfterSilence = Mathf.Max(period.LargestJumpAfterSilence, jump);
                        }
                    });
                }

                SampleRemote(remote, deltaSeconds);
                if (time - interval.Start >= intervalSeconds)
                {
                    Write(interval.ToRecord(ClientMeasurementRecord.IntervalKind, label, time));
                    interval = new Period(time);
                }
            }

            public void Corrected(float meters, bool hard)
            {
                Both(period =>
                {
                    period.Corrections.Add(meters);
                    if (hard)
                    {
                        period.HardSnaps++;
                    }

                    // A correction comes from an owner snapshot, so one that arrives while the
                    // line still counts as silent is the snapshot that ends the silence: the
                    // network is sampled later in the frame than the snapshot is handled.
                    if (inSilence || now <= afterSilenceUntil)
                    {
                        period.LargestCorrectionAfterSilence = Mathf.Max(period.LargestCorrectionAfterSilence, meters);
                    }
                });
            }

            public void IntentSent(string kind)
            {
                Both(period =>
                {
                    period.IntentsSent++;
                    period.Kind(kind).Sent++;
                });
            }

            public void IntentAnswered(string kind, float seconds)
            {
                Both(period =>
                {
                    period.IntentsAnswered++;
                    period.IntentSeconds.Add(seconds);
                    var entry = period.Kind(kind);
                    entry.Answered++;
                    entry.Seconds.Add(seconds);
                });
            }

            public ClientMeasurementRecord Summary(float time)
            {
                return total.ToRecord(ClientMeasurementRecord.SummaryKind, label, time);
            }

            public ClientMeasurementRecord Finish(float time)
            {
                if (interval.Frames.Count > 0)
                {
                    Write(interval.ToRecord(ClientMeasurementRecord.IntervalKind, label, time));
                }

                var summary = Summary(time);
                Write(summary);
                writer.Dispose();
                return summary;
            }

            private void Both(Action<Period> apply)
            {
                apply(interval);
                apply(total);
            }

            // The owner snapshot counters run for the whole session, so the recording counts from
            // where they stood at its first sample, and they start again with every session, so a
            // counter that went back counts from zero.
            private void SampleNetwork(IRealtimeDiagnostics diagnostics, bool bound)
            {
                if (diagnostics == null)
                {
                    return;
                }

                // Outside the World the transport may have no line to measure.
                if (bound)
                {
                    Both(period => period.RoundTrips.Add(diagnostics.RoundTripTimeMilliseconds));
                }

                var frames = diagnostics.SnapshotFramesReceived;
                var missing = diagnostics.EstimatedMissingSnapshotFrames;
                if (!hasOwnerCounters)
                {
                    previousOwnerFrames = frames;
                    previousOwnerMissing = missing;
                    hasOwnerCounters = true;
                }
                else if (frames < previousOwnerFrames || missing < previousOwnerMissing)
                {
                    previousOwnerFrames = 0;
                    previousOwnerMissing = 0;
                }

                var newFrames = frames - previousOwnerFrames;
                var newMissing = missing - previousOwnerMissing;
                previousOwnerFrames = frames;
                previousOwnerMissing = missing;
                Both(period =>
                {
                    period.OwnerFrames += newFrames;
                    period.OwnerMissing += newMissing;
                });

                var age = diagnostics.LatestSnapshotAgeSeconds;
                if (!bound || age < 0f)
                {
                    inSilence = false;
                    return;
                }

                Both(period => period.LargestSnapshotAge = Mathf.Max(period.LargestSnapshotAge, age));
                if (age >= SilenceSeconds)
                {
                    if (!inSilence)
                    {
                        inSilence = true;
                        silenceStart = now - age;
                    }

                    return;
                }

                if (inSilence)
                {
                    inSilence = false;
                    var length = now - age - silenceStart;
                    afterSilenceUntil = now + AfterSilenceSeconds;
                    Both(period =>
                    {
                        period.Silences++;
                        period.LongestSilence = Mathf.Max(period.LongestSilence, length);
                    });
                }
            }

            private void SampleRemote(List<RemoteMotionSample> remote, float deltaSeconds)
            {
                starvedNow.Clear();
                var observed = 0f;
                var starved = 0f;
                foreach (var sample in remote)
                {
                    observed += deltaSeconds;
                    if (!sample.IsStarved)
                    {
                        continue;
                    }

                    starved += deltaSeconds;
                    starvedNow.Add(sample.EntityId);
                    if (!starvedSince.ContainsKey(sample.EntityId))
                    {
                        starvedSince[sample.EntityId] = now - deltaSeconds;
                        Both(period => period.RemoteStarvations++);
                    }
                }

                starvationEnded.Clear();
                foreach (var pair in starvedSince)
                {
                    if (!starvedNow.Contains(pair.Key))
                    {
                        starvationEnded.Add(pair.Key);
                    }
                }

                foreach (var entity in starvationEnded)
                {
                    starvedSince.Remove(entity);
                }

                var longest = 0f;
                foreach (var pair in starvedSince)
                {
                    longest = Mathf.Max(longest, now - pair.Value);
                }

                Both(period =>
                {
                    period.RemoteObserved += observed;
                    period.RemoteStarved += starved;
                    period.LongestRemoteStarvation = Mathf.Max(period.LongestRemoteStarvation, longest);
                });
            }

            private void Write(ClientMeasurementRecord record)
            {
                writer.WriteLine(JsonUtility.ToJson(record));
                writer.Flush();
            }
        }
    }
}
