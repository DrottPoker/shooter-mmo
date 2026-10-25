using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Cloth;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // How ClothChainScheduler gives every body in play its detail each frame, so a crowd costs no
    // more than the bodies in view; see docs/EQUIPMENT_VISUALS_DESIGN.md.
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
                new ClothChainScheduler.Body(3f, false),
                new ClothChainScheduler.Body(ClothChainSolver.FarDistance + 1f, true),
                new ClothChainScheduler.Body(3f, true),
                new ClothChainScheduler.Body(ClothChainSolver.FarDistance - 1f, true),
            };

            ClothChainScheduler.Plan(plan);

            Assert.That(plan.Select(body => body.Detail),
                Is.EqualTo(new[] { ClothDetail.Hidden, ClothDetail.Rest, ClothDetail.Pose, ClothDetail.Pose }));
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
