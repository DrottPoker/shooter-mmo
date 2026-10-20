using System.Collections.Generic;
using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    // Where an option sits in the world menu: every target's actions, nearest target first,
    // then Walk here, then Examine for each target, as in RuneScape.
    public enum WorldMenuOptionGroup
    {
        Target = 0,
        Walk = 1,
        Examine = 2
    }

    // How the menu colors the target name.
    public enum WorldMenuTargetStyle
    {
        None,
        Npc,
        Mob,
        Object,
        Corpse,
        Player
    }

    // The order in which features add options; a lower value goes first, so its options come
    // first among the options of the same target.
    public static class WorldMenuOrder
    {
        public const int Combat = 100;
        public const int WorldInteraction = 200;
        public const int WalkAndExamine = 300;
    }

    // A feature that offers options for what the pointer points at, such as Attack or Trade.
    public interface IWorldMenuProvider
    {
        // Adds this feature's options. A provider reads the targets from the builder and may
        // block the whole menu, as combat does while the character is dead.
        void AddWorldMenuOptions(WorldMenuBuilder menu);

        // Runs an option this provider added. The option may be old: the menu stays open while
        // the world moves on, so the provider checks that its target is still valid.
        void ExecuteWorldMenuOption(WorldMenuOption option);
    }

    // One row of the world menu: a verb, an optional target name, and what its provider needs to
    // run it. Options are plain values, so the menu can rebuild every frame without allocating.
    public readonly struct WorldMenuOption
    {
        private WorldMenuOption(
            IWorldMenuProvider provider,
            int action,
            WorldMenuOptionGroup group,
            int targetIndex,
            string verb,
            string targetName,
            WorldMenuTargetStyle style,
            WorldPointerCandidate candidate,
            ulong entityId,
            string argument,
            Ray ray)
        {
            Provider = provider;
            Action = action;
            Group = group;
            TargetIndex = targetIndex;
            Verb = verb ?? string.Empty;
            TargetName = targetName ?? string.Empty;
            Style = style;
            Candidate = candidate;
            EntityId = entityId;
            Argument = argument ?? string.Empty;
            Ray = ray;
        }

        public IWorldMenuProvider Provider { get; }

        // What the option does, in the provider's own terms.
        public int Action { get; }

        public WorldMenuOptionGroup Group { get; }

        // The target's place in the nearest-first target list, or -1 for Walk here.
        public int TargetIndex { get; }

        public string Verb { get; }

        public string TargetName { get; }

        public WorldMenuTargetStyle Style { get; }

        public WorldPointerCandidate Candidate { get; }

        public ulong EntityId { get; }

        // Provider data, such as a capability id or an examine text.
        public string Argument { get; }

        // The pointer ray the menu was built for.
        public Ray Ray { get; }

        // Examine neither moves the character nor ends what it is doing.
        public bool IsPassive
        {
            get { return Group == WorldMenuOptionGroup.Examine; }
        }

        public bool IsEmpty
        {
            get { return Provider == null; }
        }

        public string Label
        {
            get { return TargetName.Length == 0 ? Verb : Verb + " " + TargetName; }
        }

        public static WorldMenuOption ForTarget(
            IWorldMenuProvider provider,
            int action,
            int targetIndex,
            WorldPointerCandidate candidate,
            string verb,
            string targetName,
            WorldMenuTargetStyle style,
            string argument = null,
            ulong entityId = 0)
        {
            return new WorldMenuOption(
                provider,
                action,
                WorldMenuOptionGroup.Target,
                targetIndex,
                verb,
                targetName,
                style,
                candidate,
                entityId,
                argument,
                default(Ray));
        }

        public static WorldMenuOption ForWalk(IWorldMenuProvider provider, int action, Ray ray)
        {
            return new WorldMenuOption(
                provider,
                action,
                WorldMenuOptionGroup.Walk,
                -1,
                "Walk here",
                string.Empty,
                WorldMenuTargetStyle.None,
                default(WorldPointerCandidate),
                0,
                null,
                ray);
        }

        public static WorldMenuOption ForExamine(
            IWorldMenuProvider provider,
            int action,
            int targetIndex,
            WorldPointerCandidate candidate,
            string targetName,
            WorldMenuTargetStyle style,
            string examineText)
        {
            return new WorldMenuOption(
                provider,
                action,
                WorldMenuOptionGroup.Examine,
                targetIndex,
                "Examine",
                targetName,
                style,
                candidate,
                0,
                examineText,
                default(Ray));
        }
    }

    // Collects the options for one pointer ray. Providers add options in any order; Finish
    // sorts them by group and target and keeps the order in which they were added otherwise,
    // so the provider order decides between options of the same target.
    public sealed class WorldMenuBuilder
    {
        private readonly List<WorldPointerCandidate> candidates = new List<WorldPointerCandidate>();
        private readonly List<WorldMenuOption> options = new List<WorldMenuOption>();

        public Ray Ray { get; private set; }

        // Everything under the pointer, nearest first.
        public IReadOnlyList<WorldPointerCandidate> Candidates
        {
            get { return candidates; }
        }

        public IReadOnlyList<WorldMenuOption> Options
        {
            get { return options; }
        }

        public bool IsBlocked { get; private set; }

        // The left-click option, or an empty option.
        public WorldMenuOption Default
        {
            get { return options.Count > 0 ? options[0] : default(WorldMenuOption); }
        }

        public void Begin(Ray ray, IReadOnlyList<WorldPointerCandidate> pointerCandidates)
        {
            Ray = ray;
            IsBlocked = false;
            options.Clear();
            candidates.Clear();
            if (pointerCandidates == null)
            {
                return;
            }

            for (var index = 0; index < pointerCandidates.Count; index++)
            {
                candidates.Add(pointerCandidates[index]);
            }
        }

        public void Add(WorldMenuOption option)
        {
            if (!IsBlocked && !option.IsEmpty)
            {
                options.Add(option);
            }
        }

        // No option at all, not even Walk here.
        public void Block()
        {
            IsBlocked = true;
            options.Clear();
        }

        // A stable insertion sort: the menu holds a few dozen options at most.
        public void Finish()
        {
            for (var index = 1; index < options.Count; index++)
            {
                var option = options[index];
                var position = index;
                while (position > 0 && Compare(options[position - 1], option) > 0)
                {
                    options[position] = options[position - 1];
                    position--;
                }

                options[position] = option;
            }
        }

        public void CopyOptionsTo(List<WorldMenuOption> destination)
        {
            destination.Clear();
            destination.AddRange(options);
        }

        private static int Compare(WorldMenuOption left, WorldMenuOption right)
        {
            var group = ((int)left.Group).CompareTo((int)right.Group);
            return group != 0 ? group : left.TargetIndex.CompareTo(right.TargetIndex);
        }
    }

    // Shared naming and coloring of world targets.
    public static class WorldMenuTargets
    {
        public static WorldMenuTargetStyle StyleOf(RealtimeWorldActorKind kind)
        {
            switch (kind)
            {
                case RealtimeWorldActorKind.Npc:
                    return WorldMenuTargetStyle.Npc;
                case RealtimeWorldActorKind.Mob:
                    return WorldMenuTargetStyle.Mob;
                default:
                    return WorldMenuTargetStyle.Object;
            }
        }

        public static string CorpseName(string sourceName)
        {
            return "Corpse of " + (string.IsNullOrWhiteSpace(sourceName) ? "someone" : sourceName);
        }

        public static string CorpseExamineText(string sourceName)
        {
            return "The remains of " + (string.IsNullOrWhiteSpace(sourceName) ? "someone" : sourceName) + ".";
        }
    }
}
