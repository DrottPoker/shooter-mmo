using System;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Keeps the local player's look in step with SimulationWorker: it stores the look the worker
    // sends for the controlled player, and sends the changes the Admin menu and the
    // inventory ask for. A refused change shows its reason in the game messages.
    public sealed class PlayerLookLink : IDisposable
    {
        private readonly RealtimeSimulationClient client;
        private readonly PlayerBodyLookSetting look;
        private readonly GameMessageLog messages;

        public PlayerLookLink(RealtimeSimulationClient realtimeClient, PlayerBodyLookSetting lookSetting, GameMessageLog gameMessages)
        {
            client = realtimeClient ?? throw new ArgumentNullException(nameof(realtimeClient));
            look = lookSetting ?? throw new ArgumentNullException(nameof(lookSetting));
            messages = gameMessages;
            client.EntityAppearanceChanged += OnEntityAppearanceChanged;
            client.AppearanceIntentCompleted += OnAppearanceIntentCompleted;
            client.SessionEnded += OnSessionEnded;
            client.UnexpectedlyDisconnected += OnSessionEnded;
            look.DevelopmentLookRequested += OnDevelopmentLookRequested;
            look.HeadwearRequested += OnHeadwearRequested;
        }

        public void Dispose()
        {
            client.EntityAppearanceChanged -= OnEntityAppearanceChanged;
            client.AppearanceIntentCompleted -= OnAppearanceIntentCompleted;
            client.SessionEnded -= OnSessionEnded;
            client.UnexpectedlyDisconnected -= OnSessionEnded;
            look.DevelopmentLookRequested -= OnDevelopmentLookRequested;
            look.HeadwearRequested -= OnHeadwearRequested;
        }

        private void OnEntityAppearanceChanged(RealtimeEntityAppearanceChanged change)
        {
            var session = client.MovementSession;
            if (session == null || change.EntityId != session.ControlledEntityId)
            {
                return;
            }

            var appearance = change.Appearance;
            look.Receive(appearance.BodyModel, CharacterLooks.LookOf(appearance.Choices), appearance.HeadwearLowered);
        }

        private void OnSessionEnded(RealtimeClientError error)
        {
            look.Reset();
        }

        // Another model keeps the look: the choices are read with the catalog of the body shown now.
        private void OnDevelopmentLookRequested(string bodyModel, BodyLook requested)
        {
            var model = !string.IsNullOrEmpty(bodyModel)
                ? bodyModel
                : look.Models != null && look.Models.Default != null ? look.Models.Default.Name : null;
            if (string.IsNullOrEmpty(model))
            {
                return;
            }

            client.TrySendAppearanceIntent(RealtimeAppearanceIntent.CreateSetDevelopmentLook(
                Guid.NewGuid(),
                model,
                CharacterLooks.ChoicesOf(requested, look.Catalog)));
        }

        private void OnHeadwearRequested(bool lowered)
        {
            client.TrySendAppearanceIntent(lowered
                ? RealtimeAppearanceIntent.CreateLowerHeadwear(Guid.NewGuid())
                : RealtimeAppearanceIntent.CreateRaiseHeadwear(Guid.NewGuid()));
        }

        private void OnAppearanceIntentCompleted(RealtimeAppearanceIntentResult result)
        {
            if (!result.Succeeded && result.Error != null && messages != null)
            {
                messages.Add(result.Error.Message, Time.unscaledTime);
            }
        }
    }
}
