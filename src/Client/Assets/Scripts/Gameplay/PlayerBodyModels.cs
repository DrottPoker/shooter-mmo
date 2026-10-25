using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The body models a player can show, such as Human_Male and Human_Female: each a body prefab
    // with its own look catalog. The first is the default, which every player shows until body type
    // is chosen at character creation, saved with the character, and sent to other clients. Until
    // then the local player picks a model in the World Debug panel (F2) and a development bot shows
    // one picked from its persistent id.
    public sealed class PlayerBodyModels : ScriptableObject
    {
        [SerializeField] private Model[] models = Array.Empty<Model>();

        public IReadOnlyList<Model> Models
        {
            get { return models; }
        }

        // The first model, or null without one.
        public Model Default
        {
            get { return models.Length > 0 ? models[0] : null; }
        }

        public void Configure(Model[] bodyModels)
        {
            models = bodyModels ?? Array.Empty<Model>();
        }

        // The model of the name, or the default for null or a name it does not have.
        public Model NamedOrDefault(string name)
        {
            foreach (var model in models)
            {
                if (model != null && string.Equals(model.Name, name, StringComparison.Ordinal))
                {
                    return model;
                }
            }

            return Default;
        }

        [Serializable]
        public sealed class Model
        {
            [SerializeField] private string name;
            [SerializeField] private PlayerBodyAppearance body;

            public Model(string name, PlayerBodyAppearance body)
            {
                this.name = name;
                this.body = body;
            }

            // Such as Human_Female, the name of its character art asset.
            public string Name
            {
                get { return name; }
            }

            // The body prefab, with its Animator, locomotion driver, look catalog, and cloth.
            public PlayerBodyAppearance Body
            {
                get { return body; }
            }
        }
    }
}
