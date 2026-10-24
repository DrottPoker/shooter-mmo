using System;

namespace Mmorpg.Client.Gameplay
{
    // The body model and the look of the local player's body, chosen in the World Debug panel until
    // both are chosen at character creation and saved with the character. The composition root keeps
    // them between sessions. One look serves every model: a slot option, paint layer, or garment that
    // the shown model's catalog lacks is not shown, and comes back with a model that has it. The
    // World scene offers the body models and the catalog of the local body, whose options the panel
    // lists.
    public sealed class PlayerBodyLookSetting
    {
        private BodyLook look;
        private string model;

        public PlayerBodyLookSetting(BodyLook look, string model = null)
        {
            this.look = look ?? BodyLook.Default;
            this.model = string.IsNullOrEmpty(model) ? null : model;
        }

        public event Action Changed;

        public BodyLook Look
        {
            get { return look; }
            set
            {
                var next = value ?? BodyLook.Default;
                if (next.Equals(look))
                {
                    return;
                }

                look = next;
                Changed?.Invoke();
            }
        }

        // The name of the body model, such as Human_Female, or null for the default model.
        public string Model
        {
            get { return model; }
            set
            {
                var next = string.IsNullOrEmpty(value) ? null : value;
                if (string.Equals(next, model, StringComparison.Ordinal))
                {
                    return;
                }

                model = next;
                Changed?.Invoke();
            }
        }

        // The body models of the open World scene, or null.
        public PlayerBodyModels Models { get; private set; }

        // The catalog of the local body in the open World scene, or null.
        public BodyAppearanceCatalog Catalog { get; private set; }

        public void Offer(PlayerBodyModels models, BodyAppearanceCatalog catalog)
        {
            Models = models;
            Catalog = catalog;
        }
    }
}
