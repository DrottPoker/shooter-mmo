using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Cloth;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // How ClothChainScheduler gives every body in play its detail each frame, so a crowd costs no
    // more than the bodies in view and only the largest on the screen have springs; see
    // docs/EQUIPMENT_VISUALS_DESIGN.md.
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
        public void EveryBodyInViewIsLaidOutOfViewKeepsItsPoseAndFarAwayRests()
        {
            var plan = new List<ClothChainScheduler.Body>
            {
                new ClothChainScheduler.Body(3f, false, 0.6f),
                new ClothChainScheduler.Body(ClothChainSolver.FarDistance + 1f, true, 0.03f),
                new ClothChainScheduler.Body(3f, true, 0.6f),
                new ClothChainScheduler.Body(ClothChainSolver.FarDistance - 1f, true, 0.03f),
            };

            ClothChainScheduler.Plan(plan);

            Assert.That(plan.Select(body => body.Detail),
                Is.EqualTo(new[] { ClothDetail.Hidden, ClothDetail.Rest, ClothDetail.Spring, ClothDetail.Spring }));
        }

        [Test]
        public void TheLargestBodiesOnTheScreenHaveSpringsAsFarAsTheBudgetHolds()
        {
            var plan = new List<ClothChainScheduler.Body>
            {
                new ClothChainScheduler.Body(20f, true, 0.1f),
                new ClothChainScheduler.Body(4f, true, 0.5f, cost: 2),
                new ClothChainScheduler.Body(7f, true, 0.3f),
                new ClothChainScheduler.Body(10f, true, 0.2f),
                new ClothChainScheduler.Body(2f, false, 0.9f),
            };

            // A robe and a cloak count two of the budget.
            ClothChainScheduler.Plan(plan, 3);

            Assert.That(plan.Select(body => body.Detail),
                Is.EqualTo(new[] { ClothDetail.Pose, ClothDetail.Spring, ClothDetail.Spring, ClothDetail.Pose, ClothDetail.Hidden }));
        }

        [Test]
        public void ABodyKeepsItsSpringsAgainstASlightlyLargerOne()
        {
            var plan = new List<ClothChainScheduler.Body>
            {
                new ClothChainScheduler.Body(10f, true, 0.30f, springing: true),
                new ClothChainScheduler.Body(9f, true, 0.33f),
            };

            ClothChainScheduler.Plan(plan, 1);
            Assert.That(plan.Select(body => body.Detail), Is.EqualTo(new[] { ClothDetail.Spring, ClothDetail.Pose }));

            // One clearly larger takes them.
            plan[1] = new ClothChainScheduler.Body(6f, true, 0.30f * ClothChainScheduler.SpringHysteresis + 0.01f);
            ClothChainScheduler.Plan(plan, 1);
            Assert.That(plan.Select(body => body.Detail), Is.EqualTo(new[] { ClothDetail.Pose, ClothDetail.Spring }));
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

        // Another view, such as the character preview's camera far below the world, sees the body
        // on its stage; a body counts as seen by the camera it looks largest in, and one no camera
        // sees is measured from the main camera.
        [Test]
        public void ABodyIsMeasuredFromTheCameraItLooksLargestIn()
        {
            cameraObject = new GameObject("ClothChainSchedulerTests Cameras") { hideFlags = HideFlags.HideAndDontSave };
            var main = new GameObject("Main", typeof(Camera)).GetComponent<Camera>();
            main.transform.SetParent(cameraObject.transform, false);
            main.transform.SetPositionAndRotation(new Vector3(0f, 1.6f, -20f), Quaternion.identity);
            var stage = new Vector3(0f, -10000f, 0f);
            var view = new GameObject("View", typeof(Camera)).GetComponent<Camera>();
            view.transform.SetParent(cameraObject.transform, false);
            view.transform.SetPositionAndRotation(stage + new Vector3(0f, 0.9f, 4.4f), Quaternion.Euler(0f, 180f, 0f));
            view.fieldOfView = 28f;
            var cameras = new List<Camera> { main, view };
            var planes = cameras.Select(GeometryUtility.CalculateFrustumPlanes).ToList();
            body = new GameObject("ClothChainSchedulerTests Body") { hideFlags = HideFlags.HideAndDontSave };

            body.transform.position = stage;
            ClothChainScheduler.Measure(body.transform, main.transform.position, cameras, planes, out var inView, out var distance, out var size);
            Assert.That(inView, Is.True);
            Assert.That(distance, Is.EqualTo(Vector3.Distance(stage, view.transform.position)).Within(1e-3f));
            Assert.That(size, Is.GreaterThan(0.8f), "the body nearly fills the preview's height");

            body.transform.position = Vector3.zero;
            ClothChainScheduler.Measure(body.transform, main.transform.position, cameras, planes, out inView, out distance, out size);
            Assert.That(inView, Is.True);
            Assert.That(distance, Is.EqualTo(Vector3.Distance(Vector3.zero, main.transform.position)).Within(1e-3f));
            Assert.That(size, Is.LessThan(0.3f));

            body.transform.position = new Vector3(0f, 0f, -200f);
            ClothChainScheduler.Measure(body.transform, main.transform.position, cameras, planes, out inView, out distance, out size);
            Assert.That(inView, Is.False);
            Assert.That(distance, Is.EqualTo(Vector3.Distance(body.transform.position, main.transform.position)).Within(1e-3f));
        }
    }
}
