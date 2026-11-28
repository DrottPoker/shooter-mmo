using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The layers of the game's interface, from the bottom up. Every canvas takes its sorting
    // order from UiLayers, so the order of the whole interface stands in one table.
    public enum UiLayer
    {
        // The content check, LoginMenu, and CharacterSelect, which never share a scene with the
        // World's interface.
        EntryScreen,
        WorldNameplates,
        WorldQuestMarkers,
        Hud,

        // The experience bar and the level-up banner, each over the HUD on a canvas of its own.
        ExperienceBar,
        LevelUpBanner,
        QuestTracker,
        GameMessages,
        Notices,

        // Stacked: the world interaction panel.
        Panels,

        // Stacked: the inventory and quest windows.
        Windows,

        // Stacked: the Debug and Admin menus.
        Menus,

        // The item tooltip and the dragged item, over every window they belong to.
        Tooltips,
        ContextMenu,
        Dialogs,
        DeathOverlay,
        ReconnectNotice
    }

    // The sorting order of every layer. A stacked layer sorts each open window, menu, or panel on
    // a canvas of its own above the layer's order, the newest highest, within StackBand.
    public static class UiLayers
    {
        public const int StackBand = 50;

        public static readonly UiLayer[] All =
        {
            UiLayer.EntryScreen,
            UiLayer.WorldNameplates,
            UiLayer.WorldQuestMarkers,
            UiLayer.Hud,
            UiLayer.ExperienceBar,
            UiLayer.LevelUpBanner,
            UiLayer.QuestTracker,
            UiLayer.GameMessages,
            UiLayer.Notices,
            UiLayer.Panels,
            UiLayer.Windows,
            UiLayer.Menus,
            UiLayer.Tooltips,
            UiLayer.ContextMenu,
            UiLayer.Dialogs,
            UiLayer.DeathOverlay,
            UiLayer.ReconnectNotice
        };

        public static int SortingOrder(UiLayer layer)
        {
            switch (layer)
            {
                case UiLayer.EntryScreen:
                    return 100;
                case UiLayer.WorldNameplates:
                    return 200;
                case UiLayer.WorldQuestMarkers:
                    return 300;
                case UiLayer.Hud:
                    return 400;
                case UiLayer.ExperienceBar:
                    return 430;
                case UiLayer.LevelUpBanner:
                    return 460;
                case UiLayer.QuestTracker:
                    return 500;
                case UiLayer.GameMessages:
                    return 600;
                case UiLayer.Notices:
                    return 700;
                case UiLayer.Panels:
                    return 800;
                case UiLayer.Windows:
                    return 900;
                case UiLayer.Menus:
                    return 1000;
                case UiLayer.Tooltips:
                    return 1100;
                case UiLayer.ContextMenu:
                    return 1200;
                case UiLayer.Dialogs:
                    return 1300;
                case UiLayer.DeathOverlay:
                    return 1400;
                case UiLayer.ReconnectNotice:
                    return 1500;
                default:
                    throw new ArgumentOutOfRangeException(nameof(layer), layer, "A layer needs a sorting order.");
            }
        }

        // Whether the layer holds windows, menus, or panels that the window manager stacks.
        public static bool IsStacked(UiLayer layer)
        {
            return layer is UiLayer.Panels or UiLayer.Windows or UiLayer.Menus;
        }

        // The order of the open entry of a stacked layer at a rank, the oldest 0.
        public static int StackedOrder(UiLayer layer, int rank)
        {
            return SortingOrder(layer) + 1 + Mathf.Clamp(rank, 0, StackBand - 2);
        }
    }

    // Builds every canvas of the game's interface: a screen-space overlay at the order of its
    // layer, with the one scaler setting of the permanent windows, 1920 by 1080 matched halfway
    // between width and height.
    public static class UiCanvas
    {
        public const float MatchWidthOrHeight = 0.5f;

        public static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);

        // A canvas that takes the pointer gets a raycaster, and the scene an EventSystem.
        public static Canvas Create(Transform parent, string name, UiLayer layer, bool takesPointer)
        {
            if (takesPointer)
            {
                EnsureEventSystem();
            }

            var canvasObject = takesPointer
                ? new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster))
                : new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            canvasObject.transform.SetParent(parent, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = UiLayers.SortingOrder(layer);
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;
            scaler.matchWidthOrHeight = MatchWidthOrHeight;
            return canvas;
        }

        // A canvas inside another that sorts on its own, such as a window of a stacked layer or
        // the death overlay of the combat HUD.
        public static Canvas AddNested(GameObject target, int sortingOrder, bool takesPointer)
        {
            if (!target.TryGetComponent<Canvas>(out var canvas))
            {
                canvas = target.AddComponent<Canvas>();
            }

            canvas.overrideSorting = true;
            canvas.sortingOrder = sortingOrder;
            if (takesPointer)
            {
                EnsureEventSystem();
                if (!target.TryGetComponent<GraphicRaycaster>(out _))
                {
                    target.AddComponent<GraphicRaycaster>();
                }
            }

            return canvas;
        }

        // Every canvas shares one EventSystem: the first canvas that takes the pointer makes it,
        // and it stays across scene loads, since no scene authors one.
        public static void EnsureEventSystem()
        {
            if (EventSystem.current != null || UnityEngine.Object.FindAnyObjectByType<EventSystem>() != null)
            {
                return;
            }

            var eventSystem = new GameObject(
                "UiEventSystem",
                typeof(EventSystem),
                typeof(InputSystemUIInputModule));
            if (Application.isPlaying)
            {
                UnityEngine.Object.DontDestroyOnLoad(eventSystem);
            }
        }
    }
}
