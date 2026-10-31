using System.Collections.Generic;
using Mmorpg.Client.Gameplay;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The body models a player shows: finding one, the body slot that puts one in and replaces it,
    // the model the server names for a remote player, and the local player's look setting. See
    // docs/UNITY_CLIENT_ARCHITECTURE.md under Player Bodies.
    public sealed class PlayerBodyModelTests
    {
        private const string BodyModelsPath = "Assets/Content/Player/PlayerBodyModels.asset";

        [Test]
        public void AModelIsFoundByNameAndOtherwiseTheDefault()
        {
            var models = Models("Human_Male", "Human_Female");
            var empty = Models();
            try
            {
                Assert.That(models.Default.Name, Is.EqualTo("Human_Male"));
                Assert.That(models.NamedOrDefault("Human_Female").Name, Is.EqualTo("Human_Female"));
                Assert.That(models.NamedOrDefault("Orc_Male"), Is.SameAs(models.Default));
                Assert.That(models.NamedOrDefault(null), Is.SameAs(models.Default));
                Assert.That(empty.Default, Is.Null);
                Assert.That(empty.NamedOrDefault("Human_Male"), Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(models);
                Object.DestroyImmediate(empty);
            }
        }

        [Test]
        public void TheSlotPutsInTheBodyOfAModelAndAnotherModelReplacesIt()
        {
            var models = AssetDatabase.LoadAssetAtPath<PlayerBodyModels>(BodyModelsPath);
            var root = new GameObject("RemotePlayer");
            try
            {
                var visual = new GameObject("PlayerVisual");
                visual.transform.SetParent(root.transform, false);
                var slot = visual.AddComponent<PlayerBodySlot>();
                var serialized = new SerializedObject(slot);
                serialized.FindProperty("models").objectReferenceValue = models;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var view = root.AddComponent<RemotePlayerView>();
                Assert.That(slot.Body == null, Is.True);
                Assert.That(view.Body == null, Is.True);

                Assert.That(slot.Show(models.Default), Is.True);
                var male = slot.Body;
                Assert.That(male.name, Is.EqualTo(PlayerBodySlot.BodyName));
                Assert.That(male.transform.parent, Is.SameAs(visual.transform));
                Assert.That(male.transform.localPosition, Is.EqualTo(Vector3.zero));
                Assert.That(male.transform.localRotation, Is.EqualTo(Quaternion.identity));
                Assert.That(male.Catalog, Is.SameAs(models.Default.Body.Catalog));
                Assert.That(slot.Model.Name, Is.EqualTo("Human_Male"));
                Assert.That(view.Body, Is.SameAs(male.GetComponent<CharacterLocomotionAnimator>()));

                // The model it shows changes nothing.
                Assert.That(slot.Show(models.NamedOrDefault("Human_Male")), Is.False);
                Assert.That(slot.Body, Is.SameAs(male));
                Assert.That(slot.Show(null), Is.False);

                var female = models.NamedOrDefault("Human_Female");
                Assert.That(slot.Show(female), Is.True);
                Assert.That(male == null, Is.True, "the male body is gone");
                Assert.That(root.GetComponentsInChildren<PlayerBodyAppearance>(true), Is.EqualTo(new[] { slot.Body }));
                Assert.That(slot.Body.Catalog, Is.SameAs(female.Body.Catalog));
                Assert.That(slot.Model, Is.SameAs(female));
                Assert.That(view.Body, Is.SameAs(slot.Body.GetComponent<CharacterLocomotionAnimator>()));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ARemotePlayerPutsInTheBodyOfTheModelTheServerNames()
        {
            var models = AssetDatabase.LoadAssetAtPath<PlayerBodyModels>(BodyModelsPath);
            var player = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player/RemotePlayer.prefab"));
            try
            {
                var view = player.GetComponent<RemotePlayerView>();
                view.Initialize(7, "character-7", "Hero", RealtimePlayerArchetypeIds.Player, 30, 4, null, null);
                Assert.That(view.ShowBody("Human_Female"), Is.True);
                Assert.That(view.ShowBody("Human_Female"), Is.False);
                var body = player.GetComponentInChildren<PlayerBodyAppearance>(true);
                Assert.That(body.Catalog, Is.SameAs(models.NamedOrDefault("Human_Female").Body.Catalog));
                Assert.That(body.transform.parent.name, Is.EqualTo("PlayerVisual"));
                Assert.That(view.Body, Is.SameAs(body.GetComponent<CharacterLocomotionAnimator>()));

                // An unknown model shows the default body.
                Assert.That(view.ShowBody("Orc_Male"), Is.True);
                Assert.That(
                    player.GetComponentInChildren<PlayerBodyAppearance>(true).Catalog,
                    Is.SameAs(models.Default.Body.Catalog));
            }
            finally
            {
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void TheSettingKeepsWhatTheServerSentAndPassesRequestsOn()
        {
            var setting = new PlayerBodyLookSetting();
            Assert.That(setting.Received, Is.False);
            Assert.That(setting.Model, Is.Null);
            var changes = 0;
            setting.Changed += () => changes++;
            string requestedModel = null;
            BodyLook requestedLook = null;
            setting.DevelopmentLookRequested += (model, look) =>
            {
                requestedModel = model;
                requestedLook = look;
            };
            var headwear = new List<bool>();
            setting.HeadwearRequested += headwear.Add;

            var look = BodyLook.Parse("Face=Clean;Garment=DarkBrownHair");
            setting.Receive("Human_Female", look, true);
            Assert.That(setting.Received, Is.True);
            Assert.That(setting.Model, Is.EqualTo("Human_Female"));
            Assert.That(setting.Look, Is.EqualTo(look));
            Assert.That(setting.HeadwearLowered, Is.True);
            Assert.That(changes, Is.EqualTo(1));
            setting.Receive("Human_Female", BodyLook.Parse(look.Key), true);
            Assert.That(changes, Is.EqualTo(1));

            // A request changes nothing until the server sends the stored look.
            setting.RequestDevelopmentLook(null, look.With("Face", "Stubble"));
            Assert.That(requestedModel, Is.EqualTo("Human_Female"));
            Assert.That(requestedLook.OptionFor("Face"), Is.EqualTo("Stubble"));
            Assert.That(setting.Look, Is.EqualTo(look));
            setting.RequestDevelopmentLook("Human_Male", look);
            Assert.That(requestedModel, Is.EqualTo("Human_Male"));
            setting.RequestHeadwear(false);
            Assert.That(headwear, Is.EqualTo(new[] { false }));
            Assert.That(changes, Is.EqualTo(1));

            // No name is the default model, and a session that ended forgets the look.
            setting.Receive(string.Empty, look, false);
            Assert.That(setting.Model, Is.Null);
            Assert.That(changes, Is.EqualTo(2));
            setting.Reset();
            Assert.That(setting.Received, Is.False);
            Assert.That(setting.Look, Is.EqualTo(BodyLook.Default));
            Assert.That(changes, Is.EqualTo(3));
            setting.Reset();
            Assert.That(changes, Is.EqualTo(3));

            var models = Models("Human_Male", "Human_Female");
            var catalog = ScriptableObject.CreateInstance<BodyAppearanceCatalog>();
            try
            {
                setting.Offer(models, catalog);
                Assert.That(setting.Models, Is.SameAs(models));
                Assert.That(setting.Catalog, Is.SameAs(catalog));
                setting.Offer(null, null);
                Assert.That(setting.Models, Is.Null);
                Assert.That(setting.Catalog, Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(models);
                Object.DestroyImmediate(catalog);
            }
        }

        // Models without bodies, by name.
        private static PlayerBodyModels Models(params string[] names)
        {
            var models = ScriptableObject.CreateInstance<PlayerBodyModels>();
            var entries = new PlayerBodyModels.Model[names.Length];
            for (var index = 0; index < names.Length; index++)
            {
                entries[index] = new PlayerBodyModels.Model(names[index], null);
            }

            models.Configure(entries);
            return models;
        }
    }
}
