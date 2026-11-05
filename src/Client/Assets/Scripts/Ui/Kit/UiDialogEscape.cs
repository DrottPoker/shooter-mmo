using System;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.WorldActors;
using UnityEngine;

namespace Mmorpg.Client.Ui
{
    // Closes an open dialog on Escape before the inventory windows and the features see the key,
    // and marks the Escape as taken, so it closes the dialog alone. The world menu runs earlier
    // and takes an Escape that closes it.
    [DefaultExecutionOrder(ExecutionOrder)]
    [DisallowMultipleComponent]
    public sealed class UiDialogEscape : MonoBehaviour
    {
        public const int ExecutionOrder = InventoryWindows.ExecutionOrder - 1;

        private UiDialog dialog;
        private WorldMenuController worldMenu;
        private Func<LocalPlayerInput> input;

        public void Initialize(UiDialog value, WorldMenuController menu, Func<LocalPlayerInput> playerInput)
        {
            dialog = value;
            worldMenu = menu;
            input = playerInput;
        }

        // Returns whether the Escape closed the dialog.
        public bool HandleCancel(bool cancelPressed)
        {
            if (!cancelPressed
                || dialog == null
                || !dialog.IsOpen
                || (worldMenu != null && worldMenu.CancelConsumedThisFrame))
            {
                return false;
            }

            dialog.Close();
            if (worldMenu != null)
            {
                worldMenu.ConsumeCancel();
            }

            return true;
        }

        private void Update()
        {
            var playerInput = input?.Invoke();
            HandleCancel(playerInput != null && playerInput.CancelPressedThisFrame);
        }
    }
}
