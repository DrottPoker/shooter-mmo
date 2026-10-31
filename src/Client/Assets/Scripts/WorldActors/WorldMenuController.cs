using System;
using System.Collections.Generic;
using Mmorpg.Client.Gameplay;
using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    // The world menu, as in RuneScape. Every frame it collects the options for what the
    // pointer points at from the registered providers. A left click runs the top option at
    // once; a right click opens a menu with every option, which stays open until the player
    // chooses one, clicks elsewhere, presses Escape, or moves the pointer away. Walk here and
    // Examine are its own options; combat and world interaction add theirs. A left click
    // that walked keeps steering the walk toward the ground under the pointer while the
    // button stays down. It runs after input and before the features, so they can tell that
    // an Escape only closed the menu.
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class WorldMenuController : MonoBehaviour, IWorldMenuProvider
    {
        private const int WalkAction = 1;
        private const int ExamineAction = 2;

        private readonly WorldPointerPicker picker = new WorldPointerPicker();
        private readonly List<WorldPointerCandidate> candidates = new List<WorldPointerCandidate>();
        private readonly WorldMenuBuilder hover = new WorldMenuBuilder();
        private readonly List<WorldMenuOption> menuOptions = new List<WorldMenuOption>();
        private readonly List<ProviderEntry> providers = new List<ProviderEntry>();
        private readonly WalkSteering walkSteering = new WalkSteering();
        private WorldActorClientController actorController;
        private GameMessageLog messages;
        private LocalPlayerController localPlayer;
        // The navigation intent and destination of the latest Walk here.
        private int walkIntentId;
        private Vector3 walkDestination;
        private int cancelConsumedFrame = -1;

        // Raised when the menu opens or closes.
        public event Action MenuChanged;

        // Raised after an option acted, so other features can end what they were doing. A
        // stale option that did nothing raises nothing.
        public event Action<WorldMenuOption> OptionExecuted;

        public bool IsMenuOpen { get; private set; }

        // True in the frame in which Escape closed the open menu. That Escape does nothing
        // else, so it neither ends an attack nor closes an interaction or panel.
        public bool CancelConsumedThisFrame
        {
            get { return cancelConsumedFrame == Time.frameCount; }
        }

        // The open menu's options, top first, without Cancel.
        public IReadOnlyList<WorldMenuOption> MenuOptions
        {
            get { return menuOptions; }
        }

        // Where the right click happened, in screen pixels with the origin bottom left.
        public Vector2 MenuScreenPosition { get; private set; }

        // The options for the pointer right now; empty while the menu is open or the pointer
        // is over interface.
        public IReadOnlyList<WorldMenuOption> HoverOptions
        {
            get { return hover.Options; }
        }

        // Called by the composition root before the component is activated.
        public void Initialize(WorldActorClientController worldActors, GameMessageLog gameMessages)
        {
            actorController = worldActors;
            messages = gameMessages;
            AddProvider(this, WorldMenuOrder.WalkAndExamine);
        }

        // Providers add options in WorldMenuOrder, whichever registered first. Adding a
        // provider again moves it to its new order.
        public void AddProvider(IWorldMenuProvider provider, int order)
        {
            if (provider == null)
            {
                return;
            }

            RemoveProvider(provider);
            var index = providers.Count;
            while (index > 0 && providers[index - 1].Order > order)
            {
                index--;
            }

            providers.Insert(index, new ProviderEntry(provider, order));
        }

        public void RemoveProvider(IWorldMenuProvider provider)
        {
            for (var index = 0; index < providers.Count; index++)
            {
                if (ReferenceEquals(providers[index].Provider, provider))
                {
                    providers.RemoveAt(index);
                    return;
                }
            }
        }

        // Builds the menu for a pointer ray. The world menu does this every frame; tests and
        // tools may call it directly.
        public void BuildMenu(Ray ray, WorldMenuBuilder menu)
        {
            picker.PickAll(ray, localPlayer != null ? localPlayer.transform : null, candidates);
            menu.Begin(ray, candidates);
            for (var index = 0; index < providers.Count; index++)
            {
                providers[index].Provider.AddWorldMenuOptions(menu);
            }

            menu.Finish();
        }

        // A left click: runs the top option for the ray, if there is one.
        public void RunDefault(Ray ray)
        {
            BuildMenu(ray, hover);
            RunDefaultOfHover();
        }

        // A right click: opens the menu for the ray at a screen position.
        public void OpenMenu(Ray ray, Vector2 screenPosition)
        {
            BuildMenu(ray, hover);
            OpenFromHover(screenPosition);
        }

        // Runs an option of the open menu.
        public void Choose(int index)
        {
            if (!IsMenuOpen || index < 0 || index >= menuOptions.Count)
            {
                return;
            }

            Execute(menuOptions[index]);
        }

        public void CloseMenu()
        {
            if (!IsMenuOpen)
            {
                return;
            }

            IsMenuOpen = false;
            menuOptions.Clear();
            MenuChanged?.Invoke();
        }

        public void AddWorldMenuOptions(WorldMenuBuilder menu)
        {
            menu.Add(WorldMenuOption.ForWalk(this, WalkAction, menu.Ray));
            var targets = menu.Candidates;
            for (var index = 0; index < targets.Count; index++)
            {
                AddExamine(menu, index, targets[index]);
            }
        }

        public bool ExecuteWorldMenuOption(WorldMenuOption option)
        {
            switch (option.Action)
            {
                case WalkAction:
                    if (localPlayer == null || !localPlayer.TryFindGroundPoint(option.Ray, out var point))
                    {
                        return false;
                    }

                    walkIntentId = localPlayer.MoveTo(point);
                    walkDestination = point;
                    return true;
                case ExamineAction:
                    // A target that left while the menu stayed open is not described.
                    if (messages == null || !option.Candidate.IsActive)
                    {
                        return false;
                    }

                    messages.Add(option.Argument, Time.unscaledTime);
                    return true;
                default:
                    return false;
            }
        }

        private void Update()
        {
            EnsureLocalPlayer();
            var input = localPlayer != null ? localPlayer.PlayerInput : null;
            if (input == null || !input.IsConfigured || !localPlayer.IsServerAuthoritative)
            {
                hover.Begin(default(Ray), null);
                CloseMenu();
                walkSteering.End();
                return;
            }

            if (!input.SelectHeld)
            {
                walkSteering.End();
            }

            var hasRay = localPlayer.TryGetPointerRay(out var ray);
            if (IsMenuOpen)
            {
                // A press over interface may choose a row of the menu itself.
                var pressedInWorld = hasRay
                    && (input.SelectPressedThisFrame || input.ContextMenuPressedThisFrame);
                if (input.CancelPressedThisFrame)
                {
                    cancelConsumedFrame = Time.frameCount;
                    CloseMenu();
                }
                else if (pressedInWorld)
                {
                    CloseMenu();
                }

                if (IsMenuOpen || !input.ContextMenuPressedThisFrame)
                {
                    hover.Begin(default(Ray), null);
                    return;
                }
            }

            if (!hasRay)
            {
                hover.Begin(default(Ray), null);
                return;
            }

            BuildMenu(ray, hover);
            if (input.SelectPressedThisFrame)
            {
                RunDefaultOfHover();
            }
            else if (input.ContextMenuPressedThisFrame)
            {
                OpenFromHover(input.PointerPosition);
            }
            else if (walkSteering.IsSteering)
            {
                SteerWalk();
            }
        }

        // A left click that walked keeps steering the walk while the button stays down.
        private void RunDefaultOfHover()
        {
            walkSteering.End();
            if (hover.Options.Count == 0)
            {
                return;
            }

            var option = hover.Default;
            if (Execute(option) && ReferenceEquals(option.Provider, this) && option.Action == WalkAction)
            {
                walkSteering.Follow(walkIntentId, walkDestination, Time.unscaledTime);
            }
        }

        // Plans a route to the ground under the pointer whenever the steering says so. A menu
        // that offers nothing, as while the character is dead, ends the steering, and a pointer
        // that meets no ground keeps the current route.
        private void SteerWalk()
        {
            if (hover.IsBlocked)
            {
                walkSteering.End();
                return;
            }

            var now = Time.unscaledTime;
            if (localPlayer.TryFindGroundPoint(hover.Ray, out var point)
                && walkSteering.ShouldSteerTo(
                    point,
                    localPlayer.NavigationIntentId,
                    localPlayer.AppliedNavigationIntentId,
                    now))
            {
                walkSteering.Follow(localPlayer.MoveTo(point), point, now);
            }
        }

        private void OpenFromHover(Vector2 screenPosition)
        {
            CloseMenu();
            walkSteering.End();
            if (hover.Options.Count == 0)
            {
                return;
            }

            hover.CopyOptionsTo(menuOptions);
            MenuScreenPosition = screenPosition;
            IsMenuOpen = true;
            MenuChanged?.Invoke();
        }

        private void OnDisable()
        {
            CloseMenu();
            walkSteering.End();
        }

        // True when the option acted.
        private bool Execute(WorldMenuOption option)
        {
            CloseMenu();
            if (!IsRegistered(option.Provider) || !option.Provider.ExecuteWorldMenuOption(option))
            {
                return false;
            }

            OptionExecuted?.Invoke(option);
            return true;
        }

        private void AddExamine(WorldMenuBuilder menu, int index, WorldPointerCandidate candidate)
        {
            var target = candidate.Target;
            if (target == null)
            {
                return;
            }

            if (target.TargetKind == RealtimeWorldInteractionTargetKind.Corpse)
            {
                menu.Add(WorldMenuOption.ForExamine(
                    this,
                    ExamineAction,
                    index,
                    candidate,
                    WorldMenuTargets.CorpseName(target.TargetDisplayName),
                    WorldMenuTargetStyle.Corpse,
                    WorldMenuTargets.CorpseExamineText(target.TargetDisplayName)));
                return;
            }

            if (actorController != null
                && actorController.State.TryGet(target.TargetEntityId, out var actor)
                && !string.IsNullOrWhiteSpace(actor.ExamineText))
            {
                menu.Add(WorldMenuOption.ForExamine(
                    this,
                    ExamineAction,
                    index,
                    candidate,
                    actor.DisplayName,
                    WorldMenuTargets.StyleOf(actor.Kind),
                    actor.ExamineText));
            }
        }

        private bool IsRegistered(IWorldMenuProvider provider)
        {
            for (var index = 0; index < providers.Count; index++)
            {
                if (ReferenceEquals(providers[index].Provider, provider))
                {
                    return true;
                }
            }

            return false;
        }

        private void EnsureLocalPlayer()
        {
            if (localPlayer == null)
            {
                localPlayer = FindAnyObjectByType<LocalPlayerController>();
            }
        }

        private readonly struct ProviderEntry
        {
            public ProviderEntry(IWorldMenuProvider provider, int order)
            {
                Provider = provider;
                Order = order;
            }

            public IWorldMenuProvider Provider { get; }

            public int Order { get; }
        }
    }
}
