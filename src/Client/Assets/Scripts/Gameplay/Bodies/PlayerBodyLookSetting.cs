using System;

namespace Mmorpg.Client.Gameplay
{
    // The body model, the look, and the headwear of the local player's character as the server
    // stores them; the composition root receives them from SimulationWorker. Character creation
    // chooses them; in Development the Admin menu asks for another body model or look, and
    // the inventory lowers and raises the headwear. One look serves every model: a slot option,
    // paint layer, or garment that the shown model's catalog lacks is not shown, and comes back
    // with a model that has it. The World scene offers the body models and the catalog of the
    // local body, whose options the panel lists.
    public sealed class PlayerBodyLookSetting
    {
        private BodyLook look = BodyLook.Default;
        private string model;
        private bool headwearLowered;

        // The stored body model, look, or headwear changed.
        public event Action Changed;

        // The panel asks for another body model and look; the composition root sends it.
        public event Action<string, BodyLook> DevelopmentLookRequested;

        // The inventory asks to lower or raise the headwear; the composition root sends it.
        public event Action<bool> HeadwearRequested;

        public BodyLook Look
        {
            get { return look; }
        }

        // The name of the body model, such as Human_Female, or null for the default model.
        public string Model
        {
            get { return model; }
        }

        // Whether the headwear is lowered, such as a hood pushed back.
        public bool HeadwearLowered
        {
            get { return headwearLowered; }
        }

        // Whether the server sent the look of the joined character.
        public bool Received { get; private set; }

        // The body models of the open World scene, or null.
        public PlayerBodyModels Models { get; private set; }

        // The catalog of the local body in the open World scene, or null.
        public BodyAppearanceCatalog Catalog { get; private set; }

        public void Offer(PlayerBodyModels models, BodyAppearanceCatalog catalog)
        {
            Models = models;
            Catalog = catalog;
        }

        // Stores what the server sent.
        public void Receive(string bodyModel, BodyLook bodyLook, bool lowered)
        {
            var nextModel = string.IsNullOrEmpty(bodyModel) ? null : bodyModel;
            var nextLook = bodyLook ?? BodyLook.Default;
            if (Received
                && string.Equals(nextModel, model, StringComparison.Ordinal)
                && nextLook.Equals(look)
                && lowered == headwearLowered)
            {
                return;
            }

            model = nextModel;
            look = nextLook;
            headwearLowered = lowered;
            Received = true;
            Changed?.Invoke();
        }

        // Forgets the look of a session that ended.
        public void Reset()
        {
            if (!Received)
            {
                return;
            }

            model = null;
            look = BodyLook.Default;
            headwearLowered = false;
            Received = false;
            Changed?.Invoke();
        }

        // Asks for another body model, null for the current one, and look.
        public void RequestDevelopmentLook(string bodyModel, BodyLook bodyLook)
        {
            DevelopmentLookRequested?.Invoke(
                string.IsNullOrEmpty(bodyModel) ? model : bodyModel,
                bodyLook ?? BodyLook.Default);
        }

        public void RequestHeadwear(bool lowered)
        {
            HeadwearRequested?.Invoke(lowered);
        }
    }
}
