using System.Collections;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Mmorpg.Client.Tests.PlayMode
{
    // In the World another player's body beyond 44 m from the camera casts no shadow, decided a
    // few times a second, until it comes within 40 m again, while the local player and a near body
    // cast theirs, as docs/UNITY_CLIENT_ARCHITECTURE.md describes under Crowd Performance.
    public sealed class FarBodiesPlayModeTests
    {
        private const ulong FarPlayer = 91;
        private const ulong NearPlayer = 92;

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator AFarBodyCastsNoShadowWhileTheLocalPlayerAndANearBodyCastTheirs()
        {
            var world = new ScriptedWorld();
            yield return world.Load(null);
            try
            {
                yield return world.Enter();
                var eye = world.Context.LocalPlayer.PlayerCamera.Camera.transform.position;
                world.Spawn(FarPlayer, ScriptedWorld.Appearance(), Ahead(eye, 70f));
                world.Spawn(NearPlayer, ScriptedWorld.Appearance(), Ahead(eye, 15f));
                RemotePlayerView far = null;
                RemotePlayerView near = null;
                yield return world.UntilBody(FarPlayer, view => far = view);
                yield return world.UntilBody(NearPlayer, view => near = view);

                yield return ScriptedWorld.Until(() => !far.CastsShadow, "the far body kept its shadow");
                Assert.That(ShadowOf(far.Body), Is.EqualTo(ShadowCastingMode.Off));
                Assert.That(near.CastsShadow, Is.True);
                Assert.That(ShadowOf(near.Body), Is.EqualTo(ShadowCastingMode.On));
                Assert.That(ShadowOf(world.Context.LocalPlayer.Body), Is.EqualTo(ShadowCastingMode.On), "the local player always casts its shadow");

                // Within the boundaries the far body stays without its shadow, checks or not.
                far.transform.position = Ahead(eye, 42f);
                yield return new WaitForSeconds(0.6f);
                Assert.That(far.CastsShadow, Is.False, "a body at the boundary flickers");

                // Within 40 m it casts its shadow again, at its next check.
                far.transform.position = Ahead(eye, 35f);
                var came = Time.time;
                yield return ScriptedWorld.Until(() => far.CastsShadow, "the body that came near never cast its shadow");
                Assert.That(Time.time - came, Is.LessThanOrEqualTo((1f / FarBodies.ChecksPerSecond) + 0.1f));
                Assert.That(ShadowOf(far.Body), Is.EqualTo(ShadowCastingMode.On));
                Assert.That(ShadowOf(world.Context.LocalPlayer.Body), Is.EqualTo(ShadowCastingMode.On));
            }
            finally
            {
                world.Dispose();
            }
        }

        // A place on the ground the distance ahead of the camera.
        private static Vector3 Ahead(Vector3 eye, float meters)
        {
            var across = Mathf.Sqrt(Mathf.Max(0f, (meters * meters) - (eye.y * eye.y)));
            return new Vector3(eye.x, 0f, eye.z + across);
        }

        private static ShadowCastingMode ShadowOf(Component body)
        {
            return body.GetComponent<PlayerBodyAppearance>().CombinedRenderer.shadowCastingMode;
        }
    }
}
