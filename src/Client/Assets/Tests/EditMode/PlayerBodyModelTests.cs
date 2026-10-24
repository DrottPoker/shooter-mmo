using System.Collections.Generic;
using Mmorpg.Client.Gameplay;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The body models a player shows: finding one, the body slot that puts one in and replaces it,
    // the model a remote player shows, and the setting the World Debug panel changes. See
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
                Assert.That(empty.Varied("bot-1"), Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(models);
                Object.DestroyImmediate(empty);
            }
        }

        [Test]
        public void APersonShowsTheDefaultModelAndADevelopmentBotOneOfItsOwn()
        {
            var models = Models("Human_Male", "Human_Female");
            try
            {
                Assert.That(
                    RemotePlayerView.ModelFor(RealtimePlayerArchetypeIds.Player, "character-3", models),
                    Is.SameAs(models.Default));
                Assert.That(
                    RemotePlayerView.ModelFor(RealtimePlayerArchetypeIds.DevelopmentBot, "bot-7", models),
                    Is.SameAs(models.Varied("bot-7")));
                Assert.That(models.Varied("bot-7"), Is.SameAs(models.Varied("bot-7")));
                Assert.That(RemotePlayerView.ModelFor(RealtimePlayerArchetypeIds.DevelopmentBot, "bot-7", null), Is.Null);

                // A crowd of bots shows every model.
                var shown = new HashSet<string>();
                for (var index = 0; index < 64; index++)
                {
                    shown.Add(models.Varied("bot-" + index).Name);
                }

                Assert.That(shown, Is.EquivalentTo(new[] { "Human_Male", "Human_Female" }));
            }
            finally
            {
                Object.DestroyImmediate(models);
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
        public void ARemotePlayerPutsInTheBodyOfItsModel()
        {
            var models = AssetDatabase.LoadAssetAtPath<PlayerBodyModels>(BodyModelsPath);
            var player = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player/RemotePlayer.prefab"));
            try
            {
                // A seed whose bot shows the female body.
                var index = 0;
                while (models.Varied("bot-" + index) == models.Default)
                {
                    index++;
                }

                var seed = "bot-" + index;
                var view = player.GetComponent<RemotePlayerView>();
                view.Initialize(7, seed, "Bot", RealtimePlayerArchetypeIds.DevelopmentBot, 30, 4, null, null);
                Assert.That(view.ShowBody(), Is.True);
                Assert.That(view.ShowBody(), Is.False);
                var body = player.GetComponentInChildren<PlayerBodyAppearance>(true);
                Assert.That(body.Catalog, Is.SameAs(models.Varied(seed).Body.Catalog));
                Assert.That(body.transform.parent.name, Is.EqualTo("PlayerVisual"));
                Assert.That(view.Body, Is.SameAs(body.GetComponent<CharacterLocomotionAnimator>()));
            }
            finally
            {
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void TheSettingKeepsTheModelAndTellsWhenItChanges()
        {
            var setting = new PlayerBodyLookSetting(BodyLook.Default, string.Empty);
            Assert.That(setting.Model, Is.Null);
            var changes = 0;
            setting.Changed += () => changes++;

            setting.Model = "Human_Female";
            Assert.That(setting.Model, Is.EqualTo("Human_Female"));
            Assert.That(changes, Is.EqualTo(1));
            setting.Model = "Human_Female";
            Assert.That(changes, Is.EqualTo(1));

            // No name is the default model.
            setting.Model = string.Empty;
            Assert.That(setting.Model, Is.Null);
            Assert.That(changes, Is.EqualTo(2));
            setting.Model = null;
            Assert.That(changes, Is.EqualTo(2));

            // The look stays when the model changes.
            setting.Look = BodyLook.Parse("Face=Clean;Garment=LeatherJerkin");
            setting.Model = "Human_Female";
            Assert.That(setting.Look.Key, Is.EqualTo("Face=Clean;Garment=LeatherJerkin"));
            Assert.That(new PlayerBodyLookSetting(null, "Human_Female").Model, Is.EqualTo("Human_Female"));

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
