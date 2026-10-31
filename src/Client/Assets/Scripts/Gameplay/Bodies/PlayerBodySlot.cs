using System;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Where a player prefab shows its body, on PlayerVisual: an instance of one of the body models,
    // named Body, put in when the player spawns and replaced when the player shows another model.
    // The player prefabs hold no body of their own, so a player instantiates only the body it shows.
    [DisallowMultipleComponent]
    public sealed class PlayerBodySlot : MonoBehaviour
    {
        public const string BodyName = "Body";

        [SerializeField] private PlayerBodyModels models;

        private PlayerBodyAppearance body;
        private PlayerBodyModels.Model shown;

        public PlayerBodyModels Models
        {
            get { return models; }
        }

        // The body shown, or null before Show.
        public PlayerBodyAppearance Body
        {
            get { return body; }
        }

        // The model of the body shown, or null before Show.
        public PlayerBodyModels.Model Model
        {
            get { return shown; }
        }

        // Shows the body of the model; true when it put in a new body, which has no combined
        // renderer, look, or tint yet. The body it replaces leaves the player at once.
        public bool Show(PlayerBodyModels.Model model)
        {
            if (model == null || model.Body == null
                || (body != null && string.Equals(model.Name, shown.Name, StringComparison.Ordinal)))
            {
                return false;
            }

            Remove();
            body = Instantiate(model.Body, transform, false);
            body.name = BodyName;
            shown = model;
            return true;
        }

        private void Remove()
        {
            if (body == null)
            {
                return;
            }

            var old = body.gameObject;
            body = null;
            shown = null;
            // Destroy waits for the end of the frame; until then the old body must neither draw nor
            // be found below the player.
            old.SetActive(false);
            old.transform.SetParent(null, false);
            if (Application.isPlaying)
            {
                Destroy(old);
            }
            else
            {
                DestroyImmediate(old);
            }
        }
    }
}
