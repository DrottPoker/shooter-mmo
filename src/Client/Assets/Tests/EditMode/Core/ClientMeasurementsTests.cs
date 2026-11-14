using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Ui;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The client measurements: their statistics, the jump detector on constructed tracks, a
    // recording that writes only on request, and the Debug menu's numbers, which are theirs.
    public sealed class ClientMeasurementsTests
    {
        private const float Frame = 1f / 60f;
        private string folder;

        [SetUp]
        public void CreateFolder()
        {
            folder = Path.Combine(Path.GetTempPath(), "client-measurements-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void DeleteFolder()
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void SamplesGiveThePercentilesOfTheSortedValues()
        {
            var samples = new MeasurementSamples();
            for (var value = 100; value >= 1; value--)
            {
                samples.Add(value);
            }

            samples.Add(float.NaN);
            var statistics = samples.ToStatistics();

            Assert.That(statistics.count, Is.EqualTo(100));
            Assert.That(statistics.p50, Is.EqualTo(50f));
            Assert.That(statistics.p95, Is.EqualTo(95f));
            Assert.That(statistics.p99, Is.EqualTo(99f));
            Assert.That(statistics.maximum, Is.EqualTo(100f));
            Assert.That(statistics.average, Is.EqualTo(50.5f).Within(0.001f));
            Assert.That(new MeasurementSamples().ToStatistics().count, Is.Zero);
        }

        // A walk at its speed explains every frame's movement; a snap of 3 m in one frame does not,
        // and neither does the smooth pull of a correction beyond the walk.
        [Test]
        public void TheJumpDetectorSeesASnapButNotAWalk()
        {
            var tracker = new RenderedMotionTracker();
            var owner = new object();
            var x = 0f;
            Assert.That(tracker.Track(owner, true, x, 0f, 5f, Frame), Is.Zero, "The first frame starts the track.");
            for (var frame = 0; frame < 120; frame++)
            {
                x += 5f * Frame;
                Assert.That(tracker.Track(owner, true, x, 0f, 5f, Frame), Is.Zero.Within(1e-4f), "A walk is explained.");
            }

            x += 3f;
            Assert.That(tracker.Track(owner, true, x, 0f, 5f, Frame), Is.EqualTo(3f - (5f * Frame)).Within(1e-4f));
            Assert.That(tracker.Track(owner, true, x, 0f, 5f, Frame), Is.Zero, "Standing explains itself.");
            Assert.That(tracker.Track(owner, true, x + 0.5f, 0.5f, 0f, Frame), Is.EqualTo(Mathf.Sqrt(0.5f)).Within(1e-4f), "A pull of a standing player.");
        }

        // A frame without a bound player, or with another player, starts the track over, so the
        // placement of a join or a reconnect counts no jump.
        [Test]
        public void TheJumpDetectorStartsOverAfterARebindOrAnotherPlayer()
        {
            var tracker = new RenderedMotionTracker();
            var first = new object();
            tracker.Track(first, true, 0f, 0f, 5f, Frame);
            Assert.That(tracker.Track(first, false, 50f, 0f, 5f, Frame), Is.Zero);
            Assert.That(tracker.Track(first, true, 50f, 0f, 5f, Frame), Is.Zero);
            Assert.That(tracker.Track(new object(), true, 90f, 0f, 5f, Frame), Is.Zero);
        }

        // Without a recording nothing is written, no intent is timed, and no recording costs
        // anything; a recording writes one line per interval and a summary, and stops timing.
        [Test]
        public void NothingIsWrittenUnlessARecordingWasRequested()
        {
            var diagnostics = new FakeDiagnostics();
            var measurements = new ClientMeasurements(diagnostics, null, new LocalPlayerSource(), folder);
            for (var frame = 0; frame < 300; frame++)
            {
                measurements.Frame(frame * Frame, Frame, Frame);
            }

            Assert.That(Directory.Exists(folder), Is.False);
            Assert.That(diagnostics.IntentTimingEnabled, Is.False);
            Assert.That(measurements.IsRecording, Is.False);
            Assert.That(measurements.LatestSummary, Is.Null);

            var path = measurements.StartRecording("test run", 1f);
            Assert.That(path, Does.StartWith(folder));
            Assert.That(Path.GetFileName(path), Does.EndWith("-test-run.jsonl"));
            Assert.That(diagnostics.IntentTimingEnabled, Is.True);
            var start = 300 * Frame;
            for (var frame = 0; frame < 150; frame++)
            {
                measurements.Frame(start + (frame * Frame), Frame, Frame);
            }

            diagnostics.Send(RealtimeIntentKinds.WorldInteraction);
            diagnostics.Answer(RealtimeIntentKinds.WorldInteraction, 0.2f);
            diagnostics.Send(RealtimeIntentKinds.Combat);
            measurements.OnPositionCorrected(0.5f, false);
            measurements.OnPositionCorrected(4f, true);
            var summary = measurements.StopRecording();

            Assert.That(diagnostics.IntentTimingEnabled, Is.False);
            var lines = File.ReadAllLines(path);
            var records = lines.Select(JsonUtility.FromJson<ClientMeasurementRecord>).ToArray();
            Assert.That(records.Count(record => record.kind == ClientMeasurementRecord.IntervalKind), Is.EqualTo(3));
            Assert.That(records.Last().kind, Is.EqualTo(ClientMeasurementRecord.SummaryKind));
            Assert.That(summary.frameMilliseconds.count, Is.EqualTo(150));
            Assert.That(summary.intentsSent, Is.EqualTo(2));
            Assert.That(summary.intentsAnswered, Is.EqualTo(1));
            Assert.That(summary.intentSeconds.maximum, Is.EqualTo(0.2f).Within(1e-5f));
            var interaction = summary.intents.Single(entry => entry.kind == RealtimeIntentKinds.WorldInteraction);
            Assert.That((interaction.sent, interaction.answered), Is.EqualTo((1, 1)));
            Assert.That(summary.corrections, Is.EqualTo(2));
            Assert.That(summary.hardSnaps, Is.EqualTo(1));
            Assert.That(summary.correctionMeters.maximum, Is.EqualTo(4f));
            Assert.That(records.Last().corrections, Is.EqualTo(2));
            Assert.That(summary.worldSeconds, Is.Zero, "Without a local player in the World no time counts for the rates.");
            Assert.That(summary.hardSnapsPerMinute, Is.Zero);
            Assert.That(summary.roundTripMilliseconds.count, Is.Zero, "The round trip counts only in the World.");

            // After the recording ends, intents and frames change nothing.
            diagnostics.Send(RealtimeIntentKinds.Item);
            measurements.Frame(start + 10f, Frame, Frame);
            Assert.That(File.ReadAllLines(path).Length, Is.EqualTo(lines.Length));
            Assert.That(measurements.LatestSummary.intentsSent, Is.EqualTo(2));
        }

        // A line that falls silent for longer than a second counts as a silence, and remote
        // entities that ran out of snapshots while they moved count by entity-seconds.
        [Test]
        public void ARecordingCountsSilencesAndStarvedRemoteEntities()
        {
            var diagnostics = new FakeDiagnostics { SnapshotAge = 0.05f };
            var source = new FakeRemoteSource();
            var measurements = new ClientMeasurements(diagnostics, null, new LocalPlayerSource(), folder);
            measurements.SetRemoteSources(source, null);
            measurements.StartRecording("silence", 60f);
            var time = 0f;
            for (var frame = 0; frame < 240; frame++)
            {
                time += Frame;
                source.Starved = frame >= 60 && frame < 120;
                measurements.Frame(time, Frame, Frame);
            }

            var summary = measurements.StopRecording();

            Assert.That(summary.remoteObservedEntitySeconds, Is.EqualTo(240 * 2 * Frame).Within(1e-3f));
            Assert.That(summary.remoteStarvedEntitySeconds, Is.EqualTo(60 * Frame).Within(1e-3f));
            Assert.That(summary.remoteStarvations, Is.EqualTo(1));
            Assert.That(summary.longestRemoteStarvationSeconds, Is.EqualTo(1f).Within(0.05f));
            Assert.That(summary.silences, Is.Zero, "A bound player is needed for a silence.");
        }

        // The session's snapshot counters run from the join, so a recording that starts later
        // counts only the frames of its own time.
        [Test]
        public void ARecordingCountsOnlyTheSnapshotsOfItsOwnTime()
        {
            var diagnostics = new FakeDiagnostics { SnapshotAge = 0.05f, SnapshotFrames = 950, MissingFrames = 50 };
            var measurements = new ClientMeasurements(diagnostics, null, new LocalPlayerSource(), folder);
            measurements.Frame(1f, Frame, Frame);
            measurements.StartRecording("later", 60f);
            measurements.Frame(1f + Frame, Frame, Frame);
            diagnostics.SnapshotFrames = 980;
            diagnostics.MissingFrames = 55;
            measurements.Frame(1f + (2f * Frame), Frame, Frame);

            var summary = measurements.StopRecording();

            Assert.That(summary.ownerSnapshotFrames, Is.EqualTo(30));
            Assert.That(summary.ownerSnapshotFramesMissing, Is.EqualTo(5));

            // A new session starts its counters again; the recording then counts from zero.
            measurements.StartRecording("rejoin", 60f);
            measurements.Frame(2f, Frame, Frame);
            diagnostics.SnapshotFrames = 12;
            diagnostics.MissingFrames = 1;
            measurements.Frame(2f + Frame, Frame, Frame);
            summary = measurements.StopRecording();

            Assert.That(summary.ownerSnapshotFrames, Is.EqualTo(12));
            Assert.That(summary.ownerSnapshotFramesMissing, Is.EqualTo(1));
        }

        // The Debug menu shows the recorder's numbers and computes none of its own.
        [Test]
        public void TheDebugMenuNumbersAreTheRecordersNumbers()
        {
            var diagnostics = new FakeDiagnostics
            {
                RoundTrip = 187,
                SnapshotAge = 0.125f,
                SnapshotFrames = 950,
                MissingFrames = 50
            };
            var measurements = new ClientMeasurements(diagnostics, null, new LocalPlayerSource(), folder);
            for (var frame = 0; frame < 60; frame++)
            {
                measurements.Frame(frame * 0.02f, 0.02f, 0.02f);
            }

            Assert.That(DebugMenuReadout.RoundTrip(measurements), Is.EqualTo(measurements.RoundTripMilliseconds + " ms"));
            Assert.That(DebugMenuReadout.RoundTrip(measurements), Is.EqualTo("187 ms"));
            Assert.That(DebugMenuReadout.SnapshotAge(measurements), Is.EqualTo(WorldDebugDiagnostics.Age(0.125f)));
            Assert.That(measurements.EstimatedSnapshotLossPercent, Is.EqualTo(5f));
            Assert.That(DebugMenuReadout.SnapshotLoss(measurements), Is.EqualTo(WorldDebugDiagnostics.Percent(5f) + " (50 missing)"));
            Assert.That(measurements.Telemetry.AverageFrameMilliseconds, Is.EqualTo(20f).Within(0.01f));
            Assert.That(
                DebugMenuReadout.FrameGlance(measurements),
                Is.EqualTo(WorldDebugDiagnostics.Rate(measurements.Telemetry.FramesPerSecond, "FPS") + ", P95 "
                    + WorldDebugDiagnostics.Milliseconds(measurements.Telemetry.P95FrameMilliseconds)));
            Assert.That(DebugMenuReadout.Reconciliations(measurements), Is.EqualTo("0 / 0"));

            measurements.StartRecording("readout", 60f);
            measurements.OnPositionCorrected(0.75f, false);
            var summary = measurements.LatestSummary;
            Assert.That(DebugMenuReadout.RecordedCorrections(summary), Is.EqualTo("0.750 m / 0"));
            measurements.StopRecording();
        }

        private sealed class FakeRemoteSource : IRemoteMotionSource
        {
            public bool Starved { get; set; }

            public void CollectRemoteMotion(List<RemoteMotionSample> samples)
            {
                samples.Add(new RemoteMotionSample(7, Starved));
                samples.Add(new RemoteMotionSample(8, false));
            }
        }

        private sealed class FakeDiagnostics : IRealtimeDiagnostics
        {
            public int RoundTrip { get; set; }

            public float SnapshotAge { get; set; } = -1f;

            public long SnapshotFrames { get; set; }

            public long MissingFrames { get; set; }

            public string ConnectedHost
            {
                get { return "127.0.0.1"; }
            }

            public int ConnectedPort
            {
                get { return 27015; }
            }

            public int RoundTripTimeMilliseconds
            {
                get { return RoundTrip; }
            }

            public uint LatestServerTick
            {
                get { return 10; }
            }

            public float LatestSnapshotAgeSeconds
            {
                get { return SnapshotAge; }
            }

            public long SnapshotPacketsReceived
            {
                get { return SnapshotFrames; }
            }

            public long SnapshotFramesReceived
            {
                get { return SnapshotFrames; }
            }

            public long EstimatedMissingSnapshotFrames
            {
                get { return MissingFrames; }
            }

            public long RealtimePacketsReceived
            {
                get { return 0; }
            }

            public long RealtimeBytesReceived
            {
                get { return 0; }
            }

            public long RealtimePacketsSent
            {
                get { return 0; }
            }

            public long RealtimeBytesSent
            {
                get { return 0; }
            }

            public bool IntentTimingEnabled { get; set; }

            public event Action<string> IntentSent;

            public event Action<string, float> IntentAnswered;

            public void Send(string kind)
            {
                if (IntentTimingEnabled)
                {
                    IntentSent?.Invoke(kind);
                }
            }

            public void Answer(string kind, float seconds)
            {
                if (IntentTimingEnabled)
                {
                    IntentAnswered?.Invoke(kind, seconds);
                }
            }
        }
    }
}
