using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Text;

namespace Mmorpg.Client.WorldActors
{
    public enum WorldActorStateApplyResult
    {
        Applied,
        Unchanged,
        Stale,
        Conflict
    }

    public sealed class WorldActorClientEntry
    {
        private readonly TextCatalog texts;

        // The worker sends ids only. The actor's name, its examine text, and the texts of its
        // capabilities come from the text catalog, by the actor definition id.
        public WorldActorClientEntry(RealtimeWorldActorSpawn spawn, TextCatalog texts)
        {
            if (spawn == null)
            {
                throw new ArgumentNullException(nameof(spawn));
            }

            this.texts = texts ?? throw new ArgumentNullException(nameof(texts));

            EntityId = spawn.EntityId;
            RuntimeActorId = spawn.RuntimeActorId;
            ActorDefinitionId = spawn.ActorDefinitionId;
            SpawnDefinitionId = spawn.SpawnDefinitionId;
            DisplayName = TextKeys.IsValidId(spawn.ActorDefinitionId)
                ? texts.Get(TextKeys.ActorName(spawn.ActorDefinitionId))
                : spawn.ActorDefinitionId;
            Kind = spawn.Kind;
            FactionId = spawn.FactionId;
            Disposition = spawn.Disposition;
            Level = spawn.Level;
            Aggression = spawn.Aggression;
            PresentationArchetypeId = spawn.PresentationArchetypeId;
            BoundsCenterX = spawn.BoundsCenterX;
            BoundsCenterY = spawn.BoundsCenterY;
            BoundsCenterZ = spawn.BoundsCenterZ;
            BoundsSizeX = spawn.BoundsSizeX;
            BoundsSizeY = spawn.BoundsSizeY;
            BoundsSizeZ = spawn.BoundsSizeZ;
            ExamineText = TextKeys.IsValidId(spawn.ActorDefinitionId)
                ? texts.GetOrEmpty(TextKeys.ActorExamine(spawn.ActorDefinitionId))
                : string.Empty;
            MenuOptions = spawn.MenuOptions;
            ApplyValues(
                spawn.StateRevision,
                spawn.InteractionRevision,
                spawn.IsActive,
                spawn.PositionX,
                spawn.PositionY,
                spawn.PositionZ,
                spawn.YawDegrees,
                spawn.ActivityTier);
        }

        public ulong EntityId { get; }
        public Guid RuntimeActorId { get; }
        public string ActorDefinitionId { get; }
        public string SpawnDefinitionId { get; }
        public string DisplayName { get; }
        public RealtimeWorldActorKind Kind { get; }
        public string FactionId { get; }
        public RealtimeWorldActorDisposition Disposition { get; }

        // A Mob's level and whether it attacks on sight; 0 and None for other actors.
        public int Level { get; }
        public RealtimeWorldActorAggression Aggression { get; }
        public string PresentationArchetypeId { get; }
        public float BoundsCenterX { get; }
        public float BoundsCenterY { get; }
        public float BoundsCenterZ { get; }
        public float BoundsSizeX { get; }
        public float BoundsSizeY { get; }
        public float BoundsSizeZ { get; }

        // What Examine shows.
        public string ExamineText { get; }

        // The actor's capabilities in menu order; the first is the left-click action.
        // Availability is only known once the interaction opens.
        public IReadOnlyList<RealtimeWorldActorMenuOption> MenuOptions { get; }

        public long StateRevision { get; private set; }
        public long InteractionRevision { get; private set; }
        public bool IsActive { get; private set; }
        public float PositionX { get; private set; }
        public float PositionY { get; private set; }
        public float PositionZ { get; private set; }
        public float YawDegrees { get; private set; }
        public RealtimeWorldActorActivityTier ActivityTier { get; private set; }

        internal WorldActorStateApplyResult Apply(RealtimeWorldActorState state)
        {
            if (state == null || state.EntityId != EntityId)
            {
                return WorldActorStateApplyResult.Conflict;
            }

            if (state.StateRevision < StateRevision)
            {
                return WorldActorStateApplyResult.Stale;
            }

            if (state.StateRevision == StateRevision)
            {
                return HasSameValues(state)
                    ? WorldActorStateApplyResult.Unchanged
                    : WorldActorStateApplyResult.Conflict;
            }

            if (state.InteractionRevision < InteractionRevision)
            {
                return WorldActorStateApplyResult.Conflict;
            }

            ApplyValues(
                state.StateRevision,
                state.InteractionRevision,
                state.IsActive,
                state.PositionX,
                state.PositionY,
                state.PositionZ,
                state.YawDegrees,
                state.ActivityTier);
            return WorldActorStateApplyResult.Applied;
        }

        // The verb the world menu shows before the actor's name, such as "Trade".
        public string MenuOptionLabel(string capabilityId)
        {
            return CapabilityText(capabilityId, TextKeys.ActorCapabilityMenuOption);
        }

        // The name of a capability in the interaction panel, such as "Character Bank".
        public string CapabilityName(string capabilityId)
        {
            return CapabilityText(capabilityId, TextKeys.ActorCapabilityName);
        }

        private string CapabilityText(string capabilityId, Func<string, string, string> key)
        {
            return TextKeys.IsValidId(ActorDefinitionId) && TextKeys.IsValidId(capabilityId)
                ? texts.Get(key(ActorDefinitionId, capabilityId))
                : capabilityId ?? string.Empty;
        }

        internal bool HasMenuOptions(IReadOnlyList<RealtimeWorldActorMenuOption> options)
        {
            if (options == null || options.Count != MenuOptions.Count)
            {
                return false;
            }

            for (var index = 0; index < options.Count; index++)
            {
                var current = MenuOptions[index];
                var other = options[index];
                if (!string.Equals(current.CapabilityId, other.CapabilityId, StringComparison.Ordinal)
                    || current.Kind != other.Kind)
                {
                    return false;
                }
            }

            return true;
        }

        private bool HasSameValues(RealtimeWorldActorState state)
        {
            return state.InteractionRevision == InteractionRevision
                && state.IsActive == IsActive
                && state.PositionX.Equals(PositionX)
                && state.PositionY.Equals(PositionY)
                && state.PositionZ.Equals(PositionZ)
                && state.YawDegrees.Equals(YawDegrees)
                && state.ActivityTier == ActivityTier;
        }

        private void ApplyValues(
            long stateRevision,
            long interactionRevision,
            bool isActive,
            float positionX,
            float positionY,
            float positionZ,
            float yawDegrees,
            RealtimeWorldActorActivityTier activityTier)
        {
            StateRevision = stateRevision;
            InteractionRevision = interactionRevision;
            IsActive = isActive;
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            YawDegrees = yawDegrees;
            ActivityTier = activityTier;
        }
    }

    public sealed class WorldActorClientState
    {
        private readonly Dictionary<ulong, WorldActorClientEntry> actors =
            new Dictionary<ulong, WorldActorClientEntry>();
        private TextCatalog texts = TextCatalog.Empty(TextTableFormat.SourceLocale);

        // The texts name every actor that spawns from now on. Until they are set, an actor
        // shows the keys of its texts.
        public void UseTexts(TextCatalog catalog)
        {
            texts = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public event Action Changed;

        public IReadOnlyList<WorldActorClientEntry> Actors
        {
            get { return actors.Values.OrderBy(actor => actor.EntityId).ToArray(); }
        }

        public bool TryGet(ulong entityId, out WorldActorClientEntry actor)
        {
            return actors.TryGetValue(entityId, out actor);
        }

        public bool TryApplySpawn(
            RealtimeWorldActorSpawn spawn,
            out string error)
        {
            error = string.Empty;
            if (spawn == null)
            {
                error = "World actor spawn is required.";
                return false;
            }

            if (actors.TryGetValue(spawn.EntityId, out var existing))
            {
                if (existing.RuntimeActorId != spawn.RuntimeActorId
                    || !string.Equals(
                        existing.ActorDefinitionId,
                        spawn.ActorDefinitionId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        existing.SpawnDefinitionId,
                        spawn.SpawnDefinitionId,
                        StringComparison.Ordinal)
                    || existing.Kind != spawn.Kind
                    || !string.Equals(
                        existing.FactionId,
                        spawn.FactionId,
                        StringComparison.Ordinal)
                    || existing.Disposition != spawn.Disposition
                    || existing.Level != spawn.Level
                    || existing.Aggression != spawn.Aggression
                    || !string.Equals(
                        existing.PresentationArchetypeId,
                        spawn.PresentationArchetypeId,
                        StringComparison.Ordinal)
                    || !existing.BoundsCenterX.Equals(spawn.BoundsCenterX)
                    || !existing.BoundsCenterY.Equals(spawn.BoundsCenterY)
                    || !existing.BoundsCenterZ.Equals(spawn.BoundsCenterZ)
                    || !existing.BoundsSizeX.Equals(spawn.BoundsSizeX)
                    || !existing.BoundsSizeY.Equals(spawn.BoundsSizeY)
                    || !existing.BoundsSizeZ.Equals(spawn.BoundsSizeZ)
                    || !existing.HasMenuOptions(spawn.MenuOptions))
                {
                    error = "World actor entity identity changed without a despawn.";
                    return false;
                }

                var state = new RealtimeWorldActorState(
                    spawn.EntityId,
                    spawn.StateRevision,
                    spawn.InteractionRevision,
                    spawn.IsActive,
                    spawn.PositionX,
                    spawn.PositionY,
                    spawn.PositionZ,
                    spawn.YawDegrees,
                    spawn.ActivityTier);
                var result = existing.Apply(state);
                if (result == WorldActorStateApplyResult.Conflict)
                {
                    error = "World actor spawn conflicts with the current revision.";
                    return false;
                }

                if (result == WorldActorStateApplyResult.Applied)
                {
                    Changed?.Invoke();
                }

                return true;
            }

            actors.Add(spawn.EntityId, new WorldActorClientEntry(spawn, texts));
            Changed?.Invoke();
            return true;
        }

        public bool TryApplyState(
            RealtimeWorldActorState state,
            out string error)
        {
            error = string.Empty;
            if (state == null || !actors.TryGetValue(state.EntityId, out var actor))
            {
                error = "World actor state arrived without current presence.";
                return false;
            }

            var result = actor.Apply(state);
            if (result == WorldActorStateApplyResult.Conflict)
            {
                error = "World actor state conflicts with the current authoritative revision.";
                return false;
            }

            if (result == WorldActorStateApplyResult.Applied)
            {
                Changed?.Invoke();
            }

            return true;
        }

        public bool TryRemove(RealtimeWorldActorDespawn despawn, out string error)
        {
            error = string.Empty;
            if (despawn == null)
            {
                error = "World actor despawn is required.";
                return false;
            }

            if (!actors.TryGetValue(despawn.EntityId, out var actor))
            {
                return true;
            }

            if (actor.RuntimeActorId != despawn.RuntimeActorId)
            {
                error = "World actor despawn runtime identity conflicts with presence.";
                return false;
            }

            actors.Remove(despawn.EntityId);
            Changed?.Invoke();
            return true;
        }

        public void Clear()
        {
            if (actors.Count == 0)
            {
                return;
            }

            actors.Clear();
            Changed?.Invoke();
        }
    }
}
