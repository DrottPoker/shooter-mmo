using Mmorpg.Client.Gameplay;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Mmorpg.Client.Ui
{
    // Turns the character preview around its height while the pointer drags across its picture.
    [DisallowMultipleComponent]
    public sealed class CharacterPreviewDrag : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        // A drag across the picture's width of about 150 pixels turns the copy half around.
        public const float DegreesPerPixel = 1.2f;

        public CharacterPreview Preview { get; set; }

        // Taking the drag's start keeps the window's scroll view from taking the drag.
        public void OnBeginDrag(PointerEventData eventData)
        {
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (Preview != null && eventData.button == PointerEventData.InputButton.Left)
            {
                Preview.Yaw -= eventData.delta.x * DegreesPerPixel;
            }
        }
    }
}
