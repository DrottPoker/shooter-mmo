using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // How ClothChainScheduler gives every body in play its detail each frame, so a crowd costs about
    // as much as a few bodies; see docs/EQUIPMENT_VISUALS_DESIGN.md.
    public sealed class ClothChainSchedulerTests
    {
        private GameObject cameraObject;
        private GameObject body;

        [TearDown]
        public void TearDown()
        {
            if (cameraObject != null)
            {
                Object.DestroyImmediate(cameraObject);
            }

            if (body != null)
            {
                Object.DestroyImmediate(body);
            }
        }

        [Test]
        public void BodiesOutOfViewMoveNothingAndFarBodiesRest()
        {
            var plan = new List<ClothChainScheduler.Body>
            {
                new ClothChainScheduler.Body(0, 3f, false, -1),
                new ClothChainScheduler.Body(1, ClothChainSolver.FarDistance + 1f, true, -1),
                new ClothChainScheduler.Body(2, 3f, true, -1),
            };

            ClothChainScheduler.Plan(plan, 4, 4);

            var byId = plan.ToDictionary(body => body.Id);
            Assert.That(byId[0].Detail, Is.EqualTo(ClothChainSolver.Detail.Hidden));
            Assert.That(byId[1].Detail, Is.EqualTo(ClothChainSolver.Detail.Rest));
            Assert.That(byId[2].Detail, Is.EqualTo(ClothChainSolver.Detail.Springs));
            Assert.That(plan.All(body => body.Steps), Is.True);
        }

        [Test]
        public void TheNearestBodiesInViewSpringAndTheOthersFollow()
        {
            var plan = new List<ClothChainScheduler.Body>();
            for (var id = 0; id < 6; id++)
            {
                // From the farthest to the nearest, all within the springs' distance.
                plan.Add(new ClothChainScheduler.Body(id, 20f - id, true, -1));
            }

            ClothChainScheduler.Plan(plan, 2, 10);

            var springs = plan.Where(body => body.Detail == ClothChainSolver.Detail.Springs).Select(body => body.Id);
            Assert.That(springs, Is.EquivalentTo(new[] { 4, 5 }));
            Assert.That(plan.Count(body => body.Detail == ClothChainSolver.Detail.Follow), Is.EqualTo(4));
            Assert.That(plan.All(body => body.Steps), Is.True);
        }

        [Test]
        public void FollowersStepInTurnsTheOnesThatWaitedLongestFirst()
        {
            const int followers = 10;
            const int followSteps = 4;
            var stepped = Enumerable.Repeat(-1, followers).ToArray();
            for (var frame = 0; frame < 3; frame++)
            {
                var plan = Enumerable.Range(0, followers)
                    .Select(id => new ClothChainScheduler.Body(id, ClothChainSolver.NearDistance + 1f + id, true, stepped[id]))
                    .ToList();
                ClothChainScheduler.Plan(plan, 0, followSteps);

                var steps = plan.Where(body => body.Steps).ToArray();
                Assert.That(steps.Length, Is.EqualTo(followSteps), "frame " + frame);
                Assert.That(steps.All(body => body.Detail == ClothChainSolver.Detail.Follow), Is.True);
                // The ones that waited longest step first, the nearest of them before the others.
                var waited = plan.Where(body => !body.Steps).Select(body => stepped[body.Id]).DefaultIfEmpty(int.MaxValue).Min();
                Assert.That(steps.Max(body => stepped[body.Id]), Is.LessThanOrEqualTo(waited), "frame " + frame);
                foreach (var body in steps)
                {
                    stepped[body.Id] = frame;
                }
            }

            // In three frames of four steps, every one of the ten took its turn.
            Assert.That(stepped, Has.All.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void TheCameraSeesABodyInFrontOfItAndNotOneBehindIt()
        {
            cameraObject = new GameObject("ClothChainSchedulerTests Camera") { hideFlags = HideFlags.HideAndDontSave };
            var camera = cameraObject.AddComponent<Camera>();
            camera.transform.SetPositionAndRotation(new Vector3(0f, 1.6f, 0f), Quaternion.identity);
            var planes = GeometryUtility.CalculateFrustumPlanes(camera);
            body = new GameObject("ClothChainSchedulerTests Body") { hideFlags = HideFlags.HideAndDontSave };

            body.transform.position = new Vector3(0f, 0f, 10f);
            Assert.That(GeometryUtility.TestPlanesAABB(planes, ClothChainScheduler.ViewBounds(body.transform)), Is.True);

            body.transform.position = new Vector3(0f, 0f, -10f);
            Assert.That(GeometryUtility.TestPlanesAABB(planes, ClothChainScheduler.ViewBounds(body.transform)), Is.False);

            // A body just behind the camera reaches into the view, so its cloth does not pop at the edge.
            body.transform.position = new Vector3(0f, 0f, -1.5f);
            Assert.That(GeometryUtility.TestPlanesAABB(planes, ClothChainScheduler.ViewBounds(body.transform)), Is.True);
        }
    }
}
