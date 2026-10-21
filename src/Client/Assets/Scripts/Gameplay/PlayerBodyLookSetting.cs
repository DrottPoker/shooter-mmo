using System;

namespace Mmorpg.Client.Gameplay
{
    // The look of the local player's body, chosen in the World Debug panel until the look is
    // chosen at character creation and saved with the character. The composition root keeps it
    // between sessions. The World scene offers the catalog of the local body, whose options the
    // panel lists.
    public sealed class PlayerBodyLookSetting
    {
        private BodyLook look;

        public PlayerBodyLookSetting(BodyLook look)
        {
            this.look = look ?? BodyLook.Default;
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

        // The catalog of the local body in the open World scene, or null.
        public BodyAppearanceCatalog Catalog { get; private set; }

        public void Offer(BodyAppearanceCatalog catalog)
        {
            Catalog = catalog;
        }
    }
}
