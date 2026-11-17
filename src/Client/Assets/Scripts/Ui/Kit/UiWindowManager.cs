using System;
using System.Collections.Generic;
using Mmorpg.Client.Gameplay;
using Mmorpg.WorldData.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Mmorpg.Client.Ui
{
    // What a key of the interface asks for. B and I both toggle the Inventory window.
    public enum UiCommand
    {
        Cancel,
        ToggleInventory,
        ToggleEquipment,
        ToggleQuestLog,
        ToggleDebugMenu,
        ToggleAdminMenu
    }

    // Where an Escape handler stands in the order in which Escape closes things.
    public enum UiEscapeLevel
    {
        // The open world menu.
        ContextMenu,

        // What rides on the pointer, such as a carried stack part or an Admin teleport pick.
        PointerMode,

        // What a scene does with Escape while nothing of its interface is open, such as ending
        // the attack. Every handler of this level runs.
        NothingOpen
    }

    // The one window manager of a scene. The composition root creates it and hands it to every
    // window and panel. It keeps the open windows, menus, and panels as a stack in the order
    // they were opened or focused and sorts their canvases by it, closes one thing per Escape,
    // runs the interface's keys behind one typing guard, and hosts the one dialog.
    [DisallowMultipleComponent]
    public sealed class UiWindowManager : MonoBehaviour
    {
        private const int CommandCount = (int)UiCommand.ToggleAdminMenu + 1;

        private readonly List<UiStackEntry> entries = new List<UiStackEntry>();
        private readonly List<UiStackEntry> open = new List<UiStackEntry>();
        private readonly List<EscapeHandler> escapeHandlers = new List<EscapeHandler>();
        private readonly Action[] commands = new Action[CommandCount];
        private readonly int[] layerRanks = new int[UiLayers.All.Length];
        private readonly List<RaycastResult> pointerHits = new List<RaycastResult>();
        private PointerEventData pointerEvent;
        private UiTheme theme;
        private TextCatalog texts = TextCatalog.Empty(TextTableFormat.SourceLocale);
        private LocalPlayerSource localPlayers;
        private UiDialog dialog;
        private TMP_InputField fieldAtFrameEnd;

        // The open windows, menus, and panels, the oldest first and the top last.
        public IReadOnlyList<UiStackEntry> OpenEntries
        {
            get { return open; }
        }

        // The newest open window, menu, or panel, which the next Escape closes, or null.
        public UiStackEntry Top
        {
            get { return open.Count > 0 ? open[open.Count - 1] : null; }
        }

        // The dialog host: the one dialog of the scene, on a canvas of the Dialogs layer, built
        // when it is first asked for.
        public UiDialog Dialog
        {
            get
            {
                if (dialog == null)
                {
                    BuildDialog();
                }

                return dialog;
            }
        }

        // The typing guard: true while the player types in a text field, so every key of the
        // interface is text.
        public bool IsTyping
        {
            get { return TypingField() != null; }
        }

        // Called by the composition root before the component is activated. Without a local
        // player, as on the entry screens, the keyboard's Escape is the only key.
        public void Initialize(UiTheme uiTheme, TextCatalog uiTexts, LocalPlayerSource localPlayerSource)
        {
            theme = uiTheme;
            texts = uiTexts ?? throw new ArgumentNullException(nameof(uiTexts));
            localPlayers = localPlayerSource;
        }

        // Puts a window, menu, or panel of a stacked layer on the stack, closed. Its canvas
        // takes its sorting order from the manager, and Escape closes it through close.
        public UiStackEntry Register(string name, UiLayer layer, Canvas canvas, Action close)
        {
            if (!UiLayers.IsStacked(layer))
            {
                throw new ArgumentException("Only a stacked layer holds windows, menus, and panels.", nameof(layer));
            }

            var entry = new UiStackEntry(
                this,
                name,
                layer,
                canvas != null ? canvas : throw new ArgumentNullException(nameof(canvas)),
                close);
            entries.Add(entry);
            canvas.sortingOrder = UiLayers.StackedOrder(layer, 0);
            return entry;
        }

        // A key of the interface went down: Escape, or a hotkey, which does nothing while the
        // player types.
        public void Press(UiCommand command)
        {
            Press(command, TypingField());
        }

        // The action of a hotkey; one action per command.
        public void SetCommand(UiCommand command, Action action)
        {
            if (command == UiCommand.Cancel)
            {
                throw new ArgumentException("Escape runs through the Escape handlers.", nameof(command));
            }

            commands[(int)command] = action;
        }

        // Forgets the action of a hotkey, if it is still the given one.
        public void ClearCommand(UiCommand command, Action action)
        {
            if (commands[(int)command] == action)
            {
                commands[(int)command] = null;
            }
        }

        // A handler returns whether it took the Escape; of the NothingOpen level every one runs.
        public void AddEscapeHandler(UiEscapeLevel level, Func<bool> handler)
        {
            if (handler != null)
            {
                escapeHandlers.Add(new EscapeHandler(level, handler));
            }
        }

        public void RemoveEscapeHandler(Func<bool> handler)
        {
            for (var index = escapeHandlers.Count - 1; index >= 0; index--)
            {
                if (escapeHandlers[index].Handler == handler)
                {
                    escapeHandlers.RemoveAt(index);
                }
            }
        }

        // Brings the open window, menu, or panel under a screen point to the top, as a press on
        // it does.
        public void FocusAt(Vector2 screenPosition)
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null || open.Count == 0)
            {
                return;
            }

            pointerEvent ??= new PointerEventData(eventSystem);
            pointerEvent.position = screenPosition;
            pointerHits.Clear();
            eventSystem.RaycastAll(pointerEvent, pointerHits);
            var hit = pointerHits.Count > 0 ? pointerHits[0].gameObject : null;
            pointerHits.Clear();
            var entry = hit != null ? OpenEntryOf(hit.transform) : null;
            if (entry != null)
            {
                Focus(entry);
            }
        }

        internal void Press(UiCommand command, TMP_InputField typingField)
        {
            if (command == UiCommand.Cancel)
            {
                Cancel(typingField);
                return;
            }

            // A key typed into a text field is text.
            if (typingField == null)
            {
                commands[(int)command]?.Invoke();
            }
        }

        internal void SetOpen(UiStackEntry entry, bool value)
        {
            if (entry.IsOpen == value || !entries.Contains(entry))
            {
                return;
            }

            entry.IsOpen = value;
            if (value)
            {
                open.Add(entry);
            }
            else
            {
                open.Remove(entry);
            }

            SortStack();
        }

        internal void Focus(UiStackEntry entry)
        {
            if (!entry.IsOpen || ReferenceEquals(Top, entry))
            {
                return;
            }

            open.Remove(entry);
            open.Add(entry);
            SortStack();
        }

        internal void Remove(UiStackEntry entry)
        {
            entries.Remove(entry);
            if (open.Remove(entry))
            {
                SortStack();
            }

            entry.IsOpen = false;
        }

        // Escape closes one thing per press, the top-most first: the dialog; the text field the
        // player types in, when a window, menu, or panel holds it; the world menu; what rides on
        // the pointer; the newest window, menu, or panel; and only with nothing open what the
        // scene does with nothing open. A field outside them, such as a field of an entry
        // screen's form, leaves Escape to its screen.
        private void Cancel(TMP_InputField typingField)
        {
            if (dialog != null && dialog.IsOpen)
            {
                dialog.Close();
                return;
            }

            if (typingField != null && OpenEntryOf(typingField.transform) != null)
            {
                LeaveField(typingField);
                return;
            }

            if (RunEscapeHandlers(UiEscapeLevel.ContextMenu) || RunEscapeHandlers(UiEscapeLevel.PointerMode))
            {
                return;
            }

            var top = Top;
            if (top != null)
            {
                top.Close();
                return;
            }

            RunEscapeHandlers(UiEscapeLevel.NothingOpen);
        }

        private bool RunEscapeHandlers(UiEscapeLevel level)
        {
            var taken = false;
            for (var index = 0; index < escapeHandlers.Count; index++)
            {
                var handler = escapeHandlers[index];
                if (handler.Level != level)
                {
                    continue;
                }

                taken |= handler.Handler();
                if (taken && level != UiEscapeLevel.NothingOpen)
                {
                    return true;
                }
            }

            return taken;
        }

        private void LeaveField(TMP_InputField field)
        {
            var eventSystem = EventSystem.current;
            if (eventSystem != null && eventSystem.currentSelectedGameObject == field.gameObject)
            {
                eventSystem.SetSelectedGameObject(null);
            }

            field.DeactivateInputField();
            fieldAtFrameEnd = null;
        }

        // Each open entry of a stacked layer sorts above the older ones of its layer.
        private void SortStack()
        {
            Array.Clear(layerRanks, 0, layerRanks.Length);
            for (var index = 0; index < open.Count; index++)
            {
                var entry = open[index];
                var rank = layerRanks[(int)entry.Layer]++;
                if (entry.Canvas != null)
                {
                    entry.Canvas.overrideSorting = true;
                    entry.Canvas.sortingOrder = UiLayers.StackedOrder(entry.Layer, rank);
                }
            }
        }

        // The open entry whose canvas holds a transform, or null.
        private UiStackEntry OpenEntryOf(Transform target)
        {
            for (var current = target; current != null; current = current.parent)
            {
                for (var index = 0; index < open.Count; index++)
                {
                    if (open[index].Canvas != null && open[index].Canvas.transform == current)
                    {
                        return open[index];
                    }
                }
            }

            return null;
        }

        // The text field the player types in: the focused one, or the one that was focused when
        // the last frame ended, so a key that the field took this frame, such as the Escape that
        // ended its editing, is still text, whichever of the two updates ran first.
        private TMP_InputField TypingField()
        {
            var focused = FocusedField();
            if (focused != null)
            {
                return focused;
            }

            return fieldAtFrameEnd != null && fieldAtFrameEnd.isActiveAndEnabled ? fieldAtFrameEnd : null;
        }

        private static TMP_InputField FocusedField()
        {
            var eventSystem = EventSystem.current;
            var selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
            return selected != null && selected.TryGetComponent<TMP_InputField>(out var field) && field.isFocused
                ? field
                : null;
        }

        private void Update()
        {
            var typing = TypingField();
            var input = localPlayers != null ? localPlayers.Input : null;
            if (input == null || !input.IsConfigured)
            {
                var keyboard = Keyboard.current;
                if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                {
                    Press(UiCommand.Cancel, typing);
                }

                return;
            }

            if (input.SelectPressedThisFrame || input.ContextMenuPressedThisFrame)
            {
                FocusAt(input.PointerPosition);
            }

            if (input.CancelPressedThisFrame)
            {
                Press(UiCommand.Cancel, typing);
            }

            if (input.ToggleInventoryPressedThisFrame || input.ToggleCharacterInventoryPressedThisFrame)
            {
                Press(UiCommand.ToggleInventory, typing);
            }

            if (input.ToggleEquipmentInventoryPressedThisFrame)
            {
                Press(UiCommand.ToggleEquipment, typing);
            }

            if (input.ToggleQuestLogPressedThisFrame)
            {
                Press(UiCommand.ToggleQuestLog, typing);
            }

            if (input.ToggleDebugMenuPressedThisFrame)
            {
                Press(UiCommand.ToggleDebugMenu, typing);
            }

            if (input.ToggleAdminMenuPressedThisFrame)
            {
                Press(UiCommand.ToggleAdminMenu, typing);
            }
        }

        private void LateUpdate()
        {
            fieldAtFrameEnd = FocusedField();
        }

        private void BuildDialog()
        {
            if (theme == null)
            {
                theme = UiTheme.Load(out _);
            }

            var canvas = UiCanvas.Create(transform, "DialogCanvas", UiLayer.Dialogs, true);
            var root = UiFactory.Create("DialogRoot", canvas.transform);
            UiFactory.Fill(root);
            dialog = UiDialog.Create(theme, root, texts);
        }

        private readonly struct EscapeHandler
        {
            public EscapeHandler(UiEscapeLevel level, Func<bool> handler)
            {
                Level = level;
                Handler = handler;
            }

            public UiEscapeLevel Level { get; }

            public Func<bool> Handler { get; }
        }
    }

    // One window, menu, or panel on the manager's stack, with the canvas the manager sorts. Its
    // owner says when it shows or hides it, and Escape asks the owner to close it.
    public sealed class UiStackEntry
    {
        private readonly UiWindowManager manager;
        private readonly Action close;

        internal UiStackEntry(UiWindowManager manager, string name, UiLayer layer, Canvas canvas, Action close)
        {
            this.manager = manager;
            this.close = close;
            Name = name ?? string.Empty;
            Layer = layer;
            Canvas = canvas;
        }

        public string Name { get; }

        public UiLayer Layer { get; }

        public Canvas Canvas { get; }

        public bool IsOpen { get; internal set; }

        // Called by the owner after it showed or hid the window, menu, or panel; one that opens
        // goes on top.
        public void SetOpen(bool value)
        {
            if (manager != null)
            {
                manager.SetOpen(this, value);
            }
        }

        // Brings the open entry to the top, such as when its owner shows something new in it.
        public void Focus()
        {
            if (manager != null)
            {
                manager.Focus(this);
            }
        }

        // Takes the entry off the stack for good, when its owner is destroyed.
        public void Remove()
        {
            if (manager != null)
            {
                manager.Remove(this);
            }
        }

        internal void Close()
        {
            close?.Invoke();
        }
    }
}
