using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // When the eyes blink and how far the lids close, planned with a clock and random numbers that
    // the test gives: a blink about every four seconds that lasts a quarter second, sometimes twice,
    // a crowd that does not blink together, only the bodies that may blink blinking, the dead with
    // closed eyes, and a frame that touches only the bodies whose blink is due or under way and
    // allocates nothing. See docs/UNITY_CLIENT_ARCHITECTURE.md, Eyelids.
    public sealed class EyelidScheduleTests
    {
        private const float Exact = 1e-5f;

        [Test]
        public void ABlinkClosesStaysClosedAndOpensEased()
        {
            Assert.That(EyelidSchedule.BlinkSeconds, Is.EqualTo(0.25f).Within(Exact));
            Assert.That(EyelidSchedule.LengthOf(false), Is.EqualTo(0.25f).Within(Exact));
            Assert.That(EyelidSchedule.ShareAt(0f, false), Is.EqualTo(0f));
            Assert.That(EyelidSchedule.ShareAt(0.01f, false), Is.LessThan(0.125f), "eased: slow at the start");
            Assert.That(EyelidSchedule.ShareAt(0.04f, false), Is.EqualTo(0.5f).Within(Exact));
            Assert.That(EyelidSchedule.ShareAt(0.08f, false), Is.EqualTo(1f));
            Assert.That(EyelidSchedule.ShareAt(0.1f, false), Is.EqualTo(1f));
            Assert.That(EyelidSchedule.ShareAt(0.11f + 0.07f, false), Is.EqualTo(0.5f).Within(Exact));
            Assert.That(EyelidSchedule.ShareAt(0.25f, false), Is.EqualTo(0f).Within(Exact));
            Assert.That(EyelidSchedule.ShareAt(0.3f, false), Is.EqualTo(0f));

            // Up while closing, down while opening.
            var previous = 0f;
            for (var time = 0f; time <= 0.08f; time += 0.005f)
            {
                var share = EyelidSchedule.ShareAt(time, false);
                Assert.That(share, Is.GreaterThanOrEqualTo(previous));
                previous = share;
            }

            previous = 1f;
            for (var time = 0.11f; time <= 0.25f; time += 0.005f)
            {
                var share = EyelidSchedule.ShareAt(time, false);
                Assert.That(share, Is.LessThanOrEqualTo(previous + Exact));
                previous = share;
            }
        }

        [Test]
        public void ADoubleBlinkBlinksAgainAfterAShortGap()
        {
            Assert.That(EyelidSchedule.LengthOf(true), Is.EqualTo(0.62f).Within(Exact));
            Assert.That(EyelidSchedule.ShareAt(0.09f, true), Is.EqualTo(1f));
            Assert.That(EyelidSchedule.ShareAt(0.3f, true), Is.EqualTo(0f), "open between the two");
            Assert.That(EyelidSchedule.ShareAt(0.37f + 0.04f, true), Is.EqualTo(0.5f).Within(Exact));
            Assert.That(EyelidSchedule.ShareAt(0.37f + 0.09f, true), Is.EqualTo(1f));
            Assert.That(EyelidSchedule.ShareAt(0.62f, true), Is.EqualTo(0f).Within(Exact));
            Assert.That(EyelidSchedule.IsDouble(0.149f), Is.True);
            Assert.That(EyelidSchedule.IsDouble(0.15f), Is.False);
        }

        [Test]
        public void TheTimesSpreadEvenly()
        {
            Assert.That(EyelidSchedule.FirstBlinkAt(10.0, 0f), Is.EqualTo(10.0));
            Assert.That(EyelidSchedule.FirstBlinkAt(10.0, 0.5f), Is.EqualTo(13.0).Within(Exact));
            Assert.That(EyelidSchedule.FirstBlinkAt(10.0, 1f), Is.EqualTo(16.0).Within(Exact));
            Assert.That(EyelidSchedule.NextBlinkAt(10.0, 0f), Is.EqualTo(12.0).Within(Exact));
            Assert.That(EyelidSchedule.NextBlinkAt(10.0, 0.5f), Is.EqualTo(14.0).Within(Exact));
            Assert.That(EyelidSchedule.NextBlinkAt(10.0, 1f), Is.EqualTo(16.0).Within(Exact));
        }

        [Test]
        public void ABodyBlinksWhenItsTimeComesAndBlinksAgainTwoToSixSecondsLater()
        {
            var schedule = new EyelidSchedule();
            var views = new Views(0.5f);
            var body = new EyelidSchedule.Body();
            schedule.Add(body, 100.0, false, views);
            Assert.That(body.State, Is.EqualTo(EyelidSchedule.BodyState.Waiting));
            Assert.That(body.Due, Is.EqualTo(103.0).Within(Exact));

            schedule.Step(102.9, views);
            Assert.That(schedule.Touched, Is.EqualTo(0), "no work before its time");
            Assert.That(views.Asked, Is.EqualTo(0));

            // Its time comes; 0.9 makes it a single blink.
            views.Next(0.9f);
            schedule.Step(103.0, views);
            Assert.That(body.State, Is.EqualTo(EyelidSchedule.BodyState.Blinking));
            Assert.That(body.Twice, Is.False);
            Assert.That(schedule.Moving, Is.EqualTo(1));
            schedule.Step(103.04, views);
            Assert.That(body.Share, Is.EqualTo(0.5f).Within(1e-4f));
            schedule.Step(103.09, views);
            Assert.That(body.Share, Is.EqualTo(1f));

            // It ends a quarter second in, and the next comes 2 to 6 s after: 0.25 gives 3 s.
            views.Next(0.25f);
            schedule.Step(103.26, views);
            Assert.That(body.Share, Is.EqualTo(0f));
            Assert.That(body.State, Is.EqualTo(EyelidSchedule.BodyState.Waiting));
            Assert.That(body.Due, Is.EqualTo(103.25 + 3.0).Within(Exact));
            Assert.That(schedule.Moving, Is.EqualTo(0));
            schedule.Step(105.0, views);
            Assert.That(schedule.Touched, Is.EqualTo(0));
        }

        [Test]
        public void ABodyThatMayNotBlinkOnlyGetsItsNextTime()
        {
            var schedule = new EyelidSchedule();
            var views = new Views(0f) { May = false };
            var body = new EyelidSchedule.Body();
            schedule.Add(body, 0.0, false, views);
            views.Next(0.5f);
            schedule.Step(0.1, views);
            Assert.That(views.Asked, Is.EqualTo(1));
            Assert.That(body.State, Is.EqualTo(EyelidSchedule.BodyState.Waiting));
            Assert.That(body.Share, Is.EqualTo(0f));
            Assert.That(body.Due, Is.EqualTo(0.1 + 4.0).Within(Exact));
            Assert.That(schedule.Moving, Is.EqualTo(0));
        }

        [Test]
        public void ACrowdBlinksInTheOrderOfItsTimesAndNotTogether()
        {
            var schedule = new EyelidSchedule();
            var views = new Views(0.99f);
            var bodies = new List<EyelidSchedule.Body>();
            // The first times spread over the first six seconds, given out of order.
            for (var index = 0; index < 60; index++)
            {
                var body = new EyelidSchedule.Body();
                views.Next(((index * 37) % 60) / 60f);
                schedule.Add(body, 0.0, false, views);
                bodies.Add(body);
            }

            var firsts = new List<double>();
            var blinked = new HashSet<EyelidSchedule.Body>();
            var mostAtOnce = 0;
            for (var frame = 1; frame <= 6 * 60; frame++)
            {
                var now = frame / 60.0;
                var moving = schedule.Moving;
                var due = bodies.Count(body => body.State == EyelidSchedule.BodyState.Waiting && body.Due <= now);
                schedule.Step(now, views);
                Assert.That(schedule.Touched, Is.EqualTo(moving + due), "only the bodies that blink or are due");
                foreach (var body in bodies)
                {
                    if (body.State == EyelidSchedule.BodyState.Blinking && blinked.Add(body))
                    {
                        firsts.Add(body.Due);
                    }
                }

                // Before the first body blinks again, a tenth of a second apart, at most three at once.
                if (now < EyelidSchedule.MinIntervalSeconds)
                {
                    mostAtOnce = Mathf.Max(mostAtOnce, schedule.Moving);
                }
            }

            Assert.That(firsts.Count, Is.EqualTo(60), "every body blinked once in its first six seconds");
            Assert.That(firsts, Is.Ordered, "in the order of their times");
            Assert.That(mostAtOnce, Is.LessThanOrEqualTo(3), "a crowd does not blink together");
        }

        [Test]
        public void ADyingBodyClosesItsEyesAtTheBlinksSpeedAndKeepsThemClosedWithoutWork()
        {
            var schedule = new EyelidSchedule();
            var views = new Views(0.5f);
            var body = new EyelidSchedule.Body();
            schedule.Add(body, 0.0, false, views);
            schedule.Die(body, 1.0, true);
            Assert.That(body.State, Is.EqualTo(EyelidSchedule.BodyState.Closing));
            Assert.That(schedule.Waiting, Is.EqualTo(0));
            schedule.Step(1.0, views);
            Assert.That(body.Share, Is.EqualTo(0f));
            schedule.Step(1.04, views);
            Assert.That(body.Share, Is.EqualTo(0.5f).Within(1e-4f));
            schedule.Step(1.08, views);
            Assert.That(body.Share, Is.EqualTo(1f));
            Assert.That(body.State, Is.EqualTo(EyelidSchedule.BodyState.Closed));
            Assert.That(schedule.Moving, Is.EqualTo(0));

            // Dead, it never blinks and costs nothing.
            schedule.Step(1000.0, views);
            Assert.That(schedule.Touched, Is.EqualTo(0));
            Assert.That(views.Asked, Is.EqualTo(0));
            Assert.That(body.Share, Is.EqualTo(1f));
        }

        [Test]
        public void ABodyThatDiesInTheMiddleOfABlinkClosesFromWhereItsLidsAre()
        {
            var schedule = new EyelidSchedule();
            var views = new Views(0f);
            var body = new EyelidSchedule.Body();
            schedule.Add(body, 0.0, false, views);
            views.Next(0.9f);
            schedule.Step(0.0, views);
            schedule.Step(0.2, views);
            var from = body.Share;
            Assert.That(from, Is.GreaterThan(0f).And.LessThan(1f));
            schedule.Die(body, 0.2, true);
            schedule.Step(0.24, views);
            Assert.That(body.Share, Is.EqualTo(Mathf.Lerp(from, 1f, 0.5f)).Within(1e-4f));
            schedule.Step(0.3, views);
            Assert.That(body.Share, Is.EqualTo(1f));
            Assert.That(body.State, Is.EqualTo(EyelidSchedule.BodyState.Closed));
        }

        [Test]
        public void ABodyThatComesIntoPlayDeadHasItsEyesClosedAtOnce()
        {
            var schedule = new EyelidSchedule();
            var views = new Views(0.5f);
            var body = new EyelidSchedule.Body();
            schedule.Add(body, 0.0, true, views);
            Assert.That(body.Share, Is.EqualTo(1f));
            Assert.That(body.State, Is.EqualTo(EyelidSchedule.BodyState.Closed));
            Assert.That(schedule.Count, Is.EqualTo(1));
            schedule.Step(100.0, views);
            Assert.That(schedule.Touched, Is.EqualTo(0));

            // A body seen dying when it was dead already, as LieDead, closes at once.
            var other = new EyelidSchedule.Body();
            schedule.Add(other, 0.0, false, views);
            schedule.Die(other, 1.0, false);
            Assert.That(other.Share, Is.EqualTo(1f));
            Assert.That(other.State, Is.EqualTo(EyelidSchedule.BodyState.Closed));
        }

        [Test]
        public void ABodyThatStandsUpOpensItsEyesAtOnceAndBlinksAsBefore()
        {
            var schedule = new EyelidSchedule();
            var views = new Views(0f);
            var body = new EyelidSchedule.Body();
            schedule.Add(body, 0.0, true, views);
            schedule.Live(body, 5.0, views);
            Assert.That(body.Share, Is.EqualTo(0f));
            Assert.That(body.State, Is.EqualTo(EyelidSchedule.BodyState.Waiting));
            Assert.That(body.Due, Is.EqualTo(7.0).Within(Exact));
            views.Next(0.9f);
            schedule.Step(7.0, views);
            Assert.That(body.State, Is.EqualTo(EyelidSchedule.BodyState.Blinking));
        }

        [Test]
        public void ABodyThatLeavesInTheMiddleOfABlinkOpensItsEyes()
        {
            var schedule = new EyelidSchedule();
            var views = new Views(0f);
            var body = new EyelidSchedule.Body();
            schedule.Add(body, 0.0, false, views);
            views.Next(0.9f);
            schedule.Step(0.0, views);
            schedule.Step(0.09, views);
            Assert.That(body.Share, Is.EqualTo(1f));
            schedule.Remove(body);
            Assert.That(body.Share, Is.EqualTo(0f));
            Assert.That(body.State, Is.EqualTo(EyelidSchedule.BodyState.None));
            Assert.That(schedule.Count, Is.EqualTo(0));
            Assert.That(schedule.Moving, Is.EqualTo(0));
            schedule.Step(10.0, views);
            Assert.That(schedule.Touched, Is.EqualTo(0));
        }

        // The scheduler's own decision when a body's time comes: the body blinks only when a camera
        // drew its renderer and a view the cloth is laid for, here the main view, is within 25 m of
        // its eyes; without a view no body blinks.
        [Test]
        public void TheSchedulerLetsABodyBlinkOnlyWhenItIsDrawnAndNearAView()
        {
            var objects = new List<GameObject>();
            var view = Hidden("View").AddComponent<Camera>();
            objects.Add(view.gameObject);
            var scheduler = Hidden("EyelidScheduler").AddComponent<EyelidScheduler>();
            objects.Add(scheduler.gameObject);
            try
            {
                var drawn = new HashSet<Renderer>();
                scheduler.IsDrawn = renderer => drawn.Contains(renderer);
                view.transform.position = new Vector3(0f, 1.6f, 0f);
                var near = LidsAt(objects, new Vector3(0f, 1.6f, 5f));
                var far = LidsAt(objects, new Vector3(0f, 1.6f, 40f));
                var unseen = LidsAt(objects, new Vector3(0f, 1.6f, 6f));
                drawn.Add(near.Renderer);
                drawn.Add(far.Renderer);
                foreach (var lids in new[] { near, far, unseen })
                {
                    scheduler.Add(lids, false, 0.0);
                }

                // Every first time lies within 6 s, so all come due; without a view none blinks.
                scheduler.Step(10.0);
                foreach (var lids in new[] { near, far, unseen })
                {
                    Assert.That(scheduler.BodyOf(lids).State, Is.EqualTo(EyelidSchedule.BodyState.Waiting));
                    Assert.That(scheduler.BodyOf(lids).Due, Is.GreaterThan(10.0));
                }

                // The next times lie within 16 s. With the main view near, only the body that was
                // drawn and is near blinks.
                ClothChainScheduler.UseMainView(view);
                scheduler.Step(20.0);
                Assert.That(scheduler.BodyOf(near).State, Is.EqualTo(EyelidSchedule.BodyState.Blinking));
                Assert.That(scheduler.BodyOf(far).State, Is.EqualTo(EyelidSchedule.BodyState.Waiting), "40 m away");
                Assert.That(scheduler.BodyOf(unseen).State, Is.EqualTo(EyelidSchedule.BodyState.Waiting), "not drawn");
            }
            finally
            {
                ClothChainScheduler.ForgetMainView(view);
                foreach (var created in objects)
                {
                    Object.DestroyImmediate(created);
                }
            }
        }

        [Test]
        public void IsNearFindsACameraWithinTwentyFiveMeters()
        {
            var near = Hidden("Near").AddComponent<Camera>();
            var far = Hidden("Far").AddComponent<Camera>();
            try
            {
                near.transform.position = new Vector3(0f, 1.6f, 24.9f);
                far.transform.position = new Vector3(0f, 1.6f, 25.1f);
                var eyes = new Vector3(0f, 1.6f, 0f);
                Assert.That(EyelidScheduler.MaxDistance, Is.EqualTo(25f));
                Assert.That(EyelidScheduler.IsNear(eyes, new[] { near }), Is.True);
                Assert.That(EyelidScheduler.IsNear(eyes, new[] { far }), Is.False);
                Assert.That(EyelidScheduler.IsNear(eyes, new[] { far, near }), Is.True, "any view near enough");
                Assert.That(EyelidScheduler.IsNear(eyes, new Camera[0]), Is.False, "without a view no body blinks");
            }
            finally
            {
                Object.DestroyImmediate(near.gameObject);
                Object.DestroyImmediate(far.gameObject);
            }
        }

        // 400 bodies with real lid bones, all near a view: the worst case of a crowd. Every frame
        // touches only the bodies whose blink is due or under way and allocates nothing; the same
        // crowd where no body may blink, as out of view or far, touches only the bodies that come due,
        // fewer than two a frame. The random numbers come from a fixed seed, so every run steps the
        // same frames.
        [Test]
        public void AFrameWith400BodiesTouchesOnlyTheBlinkingAndAllocatesNothing()
        {
            const int count = 400;
            var roots = new List<GameObject>();
            try
            {
                var lids = new List<BodyEyelids>();
                for (var index = 0; index < count; index++)
                {
                    var root = Hidden("Body" + index);
                    var head = Hidden("Head").transform;
                    head.SetParent(root.transform, false);
                    foreach (var eye in EyelidRig.Eyes)
                    {
                        Hidden(eye.Upper).transform.SetParent(head, false);
                        Hidden(eye.Lower).transform.SetParent(head, false);
                    }

                    roots.Add(root);
                    lids.Add(BodyEyelids.Find(root.transform, null));
                }

                // Every body near a view, the worst case of a crowd.
                var near = NewCrowd(lids, true);
                // A minute settles the crowd into its rhythm.
                near.Run(3600);
                Assert.That(ManagedAllocations.Of(() => near.Run(600)), Is.EqualTo(0));
                near.Measure();
                var watch = Stopwatch.StartNew();
                near.Run(3600);
                watch.Stop();
                var perFrame = watch.Elapsed.TotalMilliseconds / 3600.0;
                TestContext.WriteLine("400 bodies near a view: " + (perFrame * 1000.0).ToString("F1")
                    + " microseconds per frame on average, " + near.MeanMoving.ToString("F1") + " blinking on average and "
                    + near.MostMoving + " at most, " + near.MostTouched + " touched in a frame at most");

                // About 400 * 0.3 s / 4.3 s, near 28, blink at a time; none of the others is touched.
                Assert.That(near.MostMoving, Is.LessThan(80));
                Assert.That(near.MostTouched, Is.LessThan(90));
                Assert.That(perFrame, Is.LessThan(1.0), "milliseconds per frame");

                // No body may blink: 400 bodies come due every 4 s on average, about 1.7 a frame.
                var far = NewCrowd(lids, false);
                far.Run(3600);
                Assert.That(ManagedAllocations.Of(() => far.Run(600)), Is.EqualTo(0));
                far.Measure();
                far.Run(3600);
                TestContext.WriteLine("400 bodies none of which may blink: " + far.MeanTouched.ToString("F2")
                    + " touched in a frame on average, " + far.MostTouched + " at most");
                Assert.That(far.MostMoving, Is.EqualTo(0));
                Assert.That(far.MeanTouched, Is.LessThan(2.0));
                Assert.That(far.MostTouched, Is.LessThan(12));
            }
            finally
            {
                foreach (var root in roots)
                {
                    Object.DestroyImmediate(root);
                }
            }
        }

        private static Crowd NewCrowd(List<BodyEyelids> lids, bool may)
        {
            var crowd = new Crowd(new Views(0.5f) { Uniform = true, May = may });
            foreach (var body in lids)
            {
                crowd.Schedule.Add(new EyelidSchedule.Body(body), 0.0, false, crowd.Views);
            }

            Assert.That(crowd.Schedule.Count, Is.EqualTo(lids.Count));
            return crowd;
        }

        // A schedule stepped at 60 frames a second, with what its frames touched since Measure.
        private sealed class Crowd
        {
            private int frame;
            private int measured;
            private long touched;
            private long moved;

            public Crowd(Views views)
            {
                Views = views;
            }

            public EyelidSchedule Schedule { get; } = new EyelidSchedule();

            public Views Views { get; }

            public int MostTouched { get; private set; }

            public int MostMoving { get; private set; }

            public double MeanTouched
            {
                get { return measured > 0 ? (double)touched / measured : 0.0; }
            }

            public double MeanMoving
            {
                get { return measured > 0 ? (double)moved / measured : 0.0; }
            }

            public void Measure()
            {
                measured = 0;
                touched = 0;
                moved = 0;
                MostTouched = 0;
                MostMoving = 0;
            }

            public void Run(int frames)
            {
                for (var step = 0; step < frames; step++)
                {
                    frame++;
                    Schedule.Step(frame / 60.0, Views);
                    measured++;
                    touched += Schedule.Touched;
                    moved += Schedule.Moving;
                    MostTouched = Mathf.Max(MostTouched, Schedule.Touched);
                    MostMoving = Mathf.Max(MostMoving, Schedule.Moving);
                }
            }
        }

        // An object that the open scene neither shows nor saves.
        private static GameObject Hidden(string name)
        {
            return EditorUtility.CreateGameObjectWithHideFlags(name, HideFlags.HideAndDontSave);
        }

        // The lids of a body whose eyes are at a place, drawn by a renderer of its own.
        private static BodyEyelids LidsAt(List<GameObject> objects, Vector3 eyes)
        {
            var root = Hidden("Body");
            objects.Add(root);
            root.transform.position = eyes;
            var renderer = root.AddComponent<MeshRenderer>();
            var head = Hidden("Head").transform;
            head.SetParent(root.transform, false);
            foreach (var eye in EyelidRig.Eyes)
            {
                Hidden(eye.Upper).transform.SetParent(head, false);
                Hidden(eye.Lower).transform.SetParent(head, false);
            }

            return BodyEyelids.Find(root.transform, renderer);
        }

        // The views of a test: whether every body may blink, and the random numbers it gives, from
        // a queue, then the last one again, or evenly spread from a fixed seed without allocating.
        private sealed class Views : EyelidSchedule.IViews
        {
            private readonly Queue<float> queued = new Queue<float>();
            private float last;
            private uint seed = 12345u;

            public Views(float number)
            {
                last = number;
            }

            public bool May { get; set; } = true;

            public bool Uniform { get; set; }

            public int Asked { get; private set; }

            public void Next(float number)
            {
                queued.Enqueue(number);
            }

            public bool MayBlink(EyelidSchedule.Body body)
            {
                Asked++;
                return May;
            }

            public float NextRandom()
            {
                if (Uniform)
                {
                    seed = seed * 1664525u + 1013904223u;
                    return (seed >> 8) / 16777216f;
                }

                if (queued.Count > 0)
                {
                    last = queued.Dequeue();
                }

                return last;
            }
        }
    }
}
