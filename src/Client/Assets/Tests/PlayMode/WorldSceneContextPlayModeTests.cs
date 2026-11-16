using System.Collections;
using Mmorpg.Client.Gameplay;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mmorpg.Client.Tests.PlayMode
{
    // What a World scene's context counts of its shared body caches, which the crowd measurement
    // reads after a crowd left: every lying body that the context bakes puts the combined mesh
    // and the material copies of its look into the caches, and the counts follow them.
    public sealed class WorldSceneContextPlayModeTests
    {
        private const string RemotePlayerPrefabPath = "Assets/Prefabs/Player/RemotePlayer.prefab";

        private GameObject root;

        [TearDown]
        public void DestroyContext()
        {
            if (root != null)
            {
                Object.Destroy(root);
            }
        }

        [UnityTest]
        public IEnumerator TheBodyCacheCountsFollowTheBodiesTheContextPutsTogether()
        {
            root = new GameObject("World scene context");
            root.SetActive(false);
            var context = root.AddComponent<WorldSceneContext>();
            // Without a joined session the context would refuse to enter a World, so it never
            // starts; it was active, so its OnDestroy still disposes the caches.
            context.enabled = false;
#if UNITY_EDITOR
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<RemotePlayerView>(RemotePlayerPrefabPath);
            Assert.That(prefab, Is.Not.Null, RemotePlayerPrefabPath);
            var serialized = new UnityEditor.SerializedObject(context);
            serialized.FindProperty("remotePlayerPrefab").objectReferenceValue = prefab;
            serialized.ApplyModifiedPropertiesWithoutUndo();
#else
            Assert.Fail("The test reads the remote player prefab through the Editor's asset database.");
#endif
            root.SetActive(true);
            yield return null;
            Assert.That(context.BodyMeshCount, Is.Zero);
            Assert.That(context.BodyMaterialCount, Is.Zero);

            // The default body in the default look: its combined mesh, and the copies of its skin
            // materials that draw its underwear.
            Assert.That(context.TryGetCorpseBody(null, out _, out _, out _), Is.True);
            var meshes = context.BodyMeshCount;
            var materials = context.BodyMaterialCount;
            Assert.That(meshes, Is.GreaterThan(0));
            Assert.That(materials, Is.GreaterThan(0));

            // The same body again adds nothing; the other body model adds its own mesh and
            // materials.
            Assert.That(context.TryGetCorpseBody(null, out _, out _, out _), Is.True);
            Assert.That(context.BodyMeshCount, Is.EqualTo(meshes));
            Assert.That(context.BodyMaterialCount, Is.EqualTo(materials));
            var female = new RealtimeCharacterAppearance("Human_Female", null, null, false, null);
            Assert.That(context.TryGetCorpseBody(female, out _, out _, out _), Is.True);
            Assert.That(context.BodyMeshCount, Is.GreaterThan(meshes));
            Assert.That(context.BodyMaterialCount, Is.GreaterThan(materials));
        }
    }
}
