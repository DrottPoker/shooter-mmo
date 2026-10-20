using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Editor.ContentTools;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Mmorpg.WorldData.Editor.Actors
{
    // The Actor Studio tool: a filterable list of actor definitions beside an editor for the
    // selected ones, live validation with the shared compiler, and the preview and save
    // actions. Several selected actors are edited together; the id, display name, and
    // capabilities of one actor are edited on their own.
    public sealed class WorldActorStudioView : VisualElement, IDisposable
    {
        public const long LiveValidationDelayMilliseconds = 300;
        public const int MaximumCapabilities = 16;
        public const string PresentationRegistryPath = "Assets/Content/WorldActors/WorldActorPresentationRegistry.asset";

        // The registry stores each prefab as an Addressables reference. This folder is one
        // Addressables folder entry, so every prefab below it can be referenced.
        public const string PresentationPrefabFolder = "Assets/Prefabs/WorldActors";

        private static readonly (string Value, string Label)[] Kinds =
        {
            (WorldActorKindIds.Npc, "NPC"),
            (WorldActorKindIds.Mob, "Mob"),
            (WorldActorKindIds.ResourceNode, "Resource Node")
        };

        private static readonly (string Value, string Label)[] Dispositions =
        {
            (WorldActorDispositionIds.Friendly, "Friendly"),
            (WorldActorDispositionIds.Neutral, "Neutral"),
            (WorldActorDispositionIds.Hostile, "Hostile")
        };

        private static readonly (string Value, string Label)[] DamagePolicies =
        {
            (WorldActorDamagePolicyIds.Damageable, "Damageable"),
            (WorldActorDamagePolicyIds.Invulnerable, "Invulnerable")
        };

        private static readonly (string Value, string Label)[] CorpseModes =
        {
            (WorldActorCorpsePersistenceModeIds.Live, "Live"),
            (WorldActorCorpsePersistenceModeIds.Durable, "Durable")
        };

        private static readonly (string Value, string Label)[] CapabilityKinds =
        {
            (WorldActorCapabilityKindIds.Dialogue, "Dialogue"),
            (WorldActorCapabilityKindIds.Vendor, "Vendor"),
            (WorldActorCapabilityKindIds.QuestOffer, "Quest Offer"),
            (WorldActorCapabilityKindIds.QuestTurnIn, "Quest Turn In"),
            (WorldActorCapabilityKindIds.Crafting, "Crafting"),
            (WorldActorCapabilityKindIds.Insurance, "Insurance"),
            (WorldActorCapabilityKindIds.Trainer, "Trainer"),
            (WorldActorCapabilityKindIds.Bank, "Bank"),
            (WorldActorCapabilityKindIds.RecoveryStorage, "Recovery Storage"),
            (WorldActorCapabilityKindIds.Gathering, "Gathering")
        };

        private readonly WorldActorEditorService service;
        private readonly ContentUndoState undoState;
        private readonly Func<WorldActorEditorWorkspace> loadWorkspace;
        private readonly ContentToolLayout toolLayout;
        private readonly ContentListPane<WorldActorEditorDefinition> list;
        private readonly Button duplicateButton;
        private readonly Button deleteButton;
        private readonly IVisualElementScheduledItem liveValidation;
        private ContentEditSession<WorldActorEditorSnapshot> session;
        private ContentForm<WorldActorEditorDefinition> form;
        private WorldActorEditorWorkspace workspace;
        private IReadOnlyList<ContentIssue> issues = Array.Empty<ContentIssue>();
        private string detailShape = string.Empty;

        public WorldActorStudioView(
            WorldActorEditorService service,
            ContentUndoState undoState,
            Func<WorldActorEditorWorkspace> loadWorkspace = null)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.undoState = undoState ? undoState : throw new ArgumentNullException(nameof(undoState));
            this.loadWorkspace = loadWorkspace ?? service.Load;
            style.flexGrow = 1f;

            toolLayout = new ContentToolLayout(280f);
            Add(toolLayout);
            toolLayout.Toolbar.Add(new ToolbarButton(Reload) { text = "Reload", tooltip = "Load actor and spawn content from disk again." });
            toolLayout.Toolbar.Add(new ToolbarSpacer { flex = true });

            list = new ContentListPane<WorldActorEditorDefinition>(
                "Actor Definitions",
                Key,
                actor => string.IsNullOrEmpty(actor.displayName) ? "(unnamed)" : actor.displayName,
                actor => actor.id + " | " + Label(Kinds, actor.kind) + " | " + actor.factionId,
                SearchText);
            list.AddChoiceFilter("All kinds", () => Kinds, (actor, value) => string.Equals(actor.kind, value, StringComparison.Ordinal));
            list.AddChoiceFilter("All factions", FactionOptions, (actor, value) => string.Equals(actor.factionId, value, StringComparison.Ordinal));
            list.AddChoiceFilter("All capabilities", () => CapabilityKinds, (actor, value) =>
                (actor.capabilities ?? Array.Empty<WorldActorEditorCapability>()).Any(capability => string.Equals(capability?.kind, value, StringComparison.Ordinal)));
            list.AddChoiceFilter("All tags", TagOptions, (actor, value) => (actor.tags ?? Array.Empty<string>()).Contains(value, StringComparer.Ordinal));
            list.AddToggleFilter("Problems", actor => IssueState(actor) != null);
            list.SelectionChanged += OnSelectionChanged;
            toolLayout.ListSlot.Add(list);
            var listActions = new VisualElement();
            listActions.AddToClassList(ContentToolStyles.Row);
            listActions.Add(new Button(() => AddActor(WorldActorKindIds.Npc)) { text = "New NPC" });
            listActions.Add(new Button(() => AddActor(WorldActorKindIds.Mob)) { text = "New Mob" });
            listActions.Add(new Button(() => AddActor(WorldActorKindIds.ResourceNode)) { text = "New Resource Node", tooltip = "An ore vein or another node that players gather from." });
            duplicateButton = new Button(DuplicateSelected) { text = "Duplicate", tooltip = "Copy every selected actor." };
            deleteButton = new Button(() => DeleteSelected()) { text = "Delete", tooltip = "Delete the selected actors that no spawn places." };
            listActions.Add(duplicateButton);
            listActions.Add(deleteButton);
            toolLayout.ListSlot.Add(listActions);

            toolLayout.Actions.Add(new Button(PreviewChanges) { text = "Preview Changes", tooltip = "Compile with the content compiler and show the canonical files." });
            toolLayout.Actions.Add(new Button(() => Save()) { text = "Save + Compile", tooltip = "Write the authoring files and the canonical runtime of every World." });
            toolLayout.Actions.Add(new Button(VerifyCanonical) { text = "Verify Canonical", tooltip = "Check that the committed runtime matches the authoring files." });
            var info = new Label(
                "Actor Studio and Spawn Authoring each edit their own copy of the content. "
                    + "Save one before editing in the other.");
            info.AddToClassList("content-actions__info");
            toolLayout.Actions.Add(info);
            toolLayout.Issues.IssueActivated += ShowIssue;

            liveValidation = schedule.Execute(RunLiveValidation);
            liveValidation.Pause();
            ShowDetailHint("Loading actor content...");
        }

        public event Action UnsavedChangesChanged;

        public bool HasUnsavedChanges => session != null && session.HasUnsavedChanges;

        public WorldActorEditorWorkspace Workspace => workspace;

        public ContentListPane<WorldActorEditorDefinition> List => list;

        public ContentForm<WorldActorEditorDefinition> Form => form;

        public IReadOnlyList<ContentIssue> Issues => issues;

        public string StatusText => toolLayout.StatusText;

        public HelpBoxMessageType StatusType => toolLayout.StatusType;

        public static string Key(WorldActorEditorDefinition actor)
        {
            return WorldActorEditorIssues.Key(WorldActorEditorIssues.Actors, actor.id);
        }

        // Loads actor and spawn content from disk. With keepPendingEdits, edits that were not
        // saved before a script reload are applied again.
        public void Load(bool keepPendingEdits)
        {
            try
            {
                session?.Dispose();
                workspace = loadWorkspace();
                session = new ContentEditSession<WorldActorEditorSnapshot>(
                    undoState,
                    () => new WorldActorEditorSnapshot { catalog = workspace.Catalog, spawns = workspace.Spawns },
                    snapshot => workspace.RestoreContent(snapshot.catalog, snapshot.spawns));
                form = new ContentForm<WorldActorEditorDefinition>(session, Key);
                detailShape = string.Empty;
                session.Changed += OnContentChanged;
                session.Load(keepPendingEdits);
                var actors = workspace.Catalog.actors ?? Array.Empty<WorldActorEditorDefinition>();
                if (list.Selection.Count == 0 && actors.Length > 0)
                {
                    list.Select(new[] { Key(actors[0]) });
                }

                toolLayout.SetStatus(
                    "Loaded " + actors.Length + " actor definitions and the spawns of World '" + service.Paths.WorldId + "'."
                        + (HasUnsavedChanges ? " Unsaved edits from before the script reload were restored." : string.Empty),
                    HelpBoxMessageType.Info);
                RunLiveValidation();
            }
            catch (Exception exception)
            {
                workspace = null;
                session?.Dispose();
                session = null;
                list.SetItems(Array.Empty<WorldActorEditorDefinition>());
                ShowDetailHint("Actor content could not be loaded.");
                toolLayout.SetStatus(exception.Message, HelpBoxMessageType.Error);
            }

            UnsavedChangesChanged?.Invoke();
        }

        public void Reload()
        {
            if (HasUnsavedChanges
                && !EditorUtility.DisplayDialog(
                    "Discard Unsaved Actor Changes",
                    "Actor Studio has unsaved changes. Reloading discards them.",
                    "Discard and reload",
                    "Cancel"))
            {
                return;
            }

            Load(false);
        }

        // Compiles with the content compiler process, then writes the authoring files and the
        // canonical runtime. With confirmChanges, the caller approves the listed changes
        // instead of a dialog.
        public bool Save(Func<string[], bool> confirmChanges = null)
        {
            if (workspace == null)
            {
                return false;
            }

            var validation = service.Validate(workspace);
            toolLayout.SetStatus(validation.Message, validation.Success ? HelpBoxMessageType.Info : HelpBoxMessageType.Error);
            RunLiveValidation();
            if (!validation.Success)
            {
                return false;
            }

            var confirm = confirmChanges ?? ConfirmChanges;
            if (validation.Changes.Length > 0 && !confirm(validation.Changes))
            {
                toolLayout.SetStatus("The write was cancelled. No actor files were changed.", HelpBoxMessageType.Warning);
                return false;
            }

            try
            {
                service.SaveAndCompile(workspace, validation);
                AssetDatabase.Refresh();
                session.MarkSaved();
                toolLayout.SetStatus(
                    "World actor authoring and canonical runtime files were written. " + validation.Message,
                    HelpBoxMessageType.Info);
                return true;
            }
            catch (Exception exception)
            {
                toolLayout.SetStatus("Actor content write failed: " + exception.Message, HelpBoxMessageType.Error);
                return false;
            }
        }

        public void AddActor(string kind)
        {
            if (workspace == null)
            {
                return;
            }

            WorldActorEditorDefinition actor = null;
            session.Change("New " + Label(Kinds, kind), () =>
            {
                var actors = workspace.Catalog.actors ?? Array.Empty<WorldActorEditorDefinition>();
                actor = CreateTemplate(kind, actors.Length + 1);
                workspace.Catalog.actors = actors.Concat(new[] { actor }).ToArray();
            });
            list.Select(new[] { Key(actor) });
        }

        public void DuplicateSelected()
        {
            var selection = list.Selection;
            if (workspace == null || selection.Count == 0)
            {
                return;
            }

            var copies = new List<WorldActorEditorDefinition>();
            session.Change(selection.Count == 1 ? "Duplicate actor" : "Duplicate " + selection.Count + " actors", () =>
            {
                foreach (var source in selection)
                {
                    var copy = WorldActorEditorJson.Clone(source);
                    copy.id = CreateUniqueActorId(source.id + ".copy");
                    copy.displayName += " Copy";
                    workspace.Catalog.actors = workspace.Catalog.actors.Concat(new[] { copy }).ToArray();
                    copies.Add(copy);
                }
            });
            list.Select(copies.Select(Key));
        }

        // Deletes the selected actors that no spawn of any World places. With confirm, the
        // caller approves the listed ids instead of a dialog.
        public void DeleteSelected(Func<string[], bool> confirm = null)
        {
            var selection = list.Selection;
            if (workspace == null || selection.Count == 0)
            {
                return;
            }

            var blocked = selection
                .Select(actor => (Actor: actor, Reason: PlacementSummary(actor.id)))
                .Where(entry => entry.Reason.Length > 0)
                .ToArray();
            var deletable = selection.Except(blocked.Select(entry => entry.Actor)).ToArray();
            var blockedText = string.Join("; ", blocked.Select(entry => entry.Actor.id + " is placed by " + entry.Reason));
            if (deletable.Length == 0)
            {
                toolLayout.SetStatus(
                    "Nothing was deleted. Remove or reassign the spawns first: " + blockedText + ".",
                    HelpBoxMessageType.Warning);
                return;
            }

            var ids = deletable.Select(actor => actor.id).ToArray();
            var approve = confirm ?? (values => EditorUtility.DisplayDialog(
                "Delete Actor Definitions",
                "Delete these actors from the authoring workspace?\n\n" + string.Join("\n", values),
                "Delete",
                "Cancel"));
            if (!approve(ids))
            {
                return;
            }

            session.Change(ids.Length == 1 ? "Delete actor" : "Delete " + ids.Length + " actors", () =>
                workspace.Catalog.actors = workspace.Catalog.actors.Where(actor => !deletable.Contains(actor)).ToArray());
            toolLayout.SetStatus(
                "Deleted " + ids.Length + (ids.Length == 1 ? " actor." : " actors.")
                    + (blocked.Length > 0 ? " Kept because spawns place them: " + blockedText + "." : string.Empty),
                blocked.Length > 0 ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info);
        }

        // Renames an actor and every spawn of the edited World that places it, as one undo
        // step. An actor that other Worlds place keeps its id, because their spawns are not
        // part of this workspace.
        public bool RenameActor(string currentId, string newId)
        {
            var actor = workspace?.Catalog.actors?.FirstOrDefault(entry => string.Equals(entry.id, currentId, StringComparison.Ordinal));
            newId = newId?.Trim() ?? string.Empty;
            if (actor == null || newId.Length == 0 || string.Equals(currentId, newId, StringComparison.Ordinal))
            {
                form?.Refresh();
                return false;
            }

            if (workspace.Catalog.actors.Any(entry => !ReferenceEquals(entry, actor) && string.Equals(entry.id, newId, StringComparison.Ordinal)))
            {
                toolLayout.SetStatus("Another actor already uses the id '" + newId + "'.", HelpBoxMessageType.Error);
                form?.Refresh();
                return false;
            }

            var otherWorlds = OtherWorldReferences(currentId);
            if (otherWorlds.Length > 0)
            {
                toolLayout.SetStatus(
                    "The id stays '" + currentId + "' because spawns in other Worlds place this actor: "
                        + string.Join(", ", otherWorlds) + ".",
                    HelpBoxMessageType.Error);
                form?.Refresh();
                return false;
            }

            var references = workspace.Spawns.CountActorReferences(currentId);
            session.Change("Actor id changed", () =>
            {
                actor.id = newId;
                workspace.Spawns.ReplaceActorReferences(currentId, newId);
            });
            list.Select(new[] { Key(actor) });
            toolLayout.SetStatus(
                references == 0
                    ? "Renamed the actor."
                    : "Renamed the actor and updated " + references + (references == 1 ? " spawn" : " spawns") + " that place it.",
                HelpBoxMessageType.Info);
            return true;
        }

        // Adds the tag to every selected actor.
        public void AddTag(string tag)
        {
            tag = tag?.Trim() ?? string.Empty;
            var targets = form?.Targets ?? Array.Empty<WorldActorEditorDefinition>();
            if (tag.Length == 0 || targets.Count == 0)
            {
                return;
            }

            session.Change(targets.Count == 1 ? "Tag " + tag + " added" : "Tag " + tag + " added for " + targets.Count + " entries", () =>
            {
                foreach (var actor in targets)
                {
                    actor.tags = (actor.tags ?? Array.Empty<string>())
                        .Concat(new[] { tag })
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();
                }
            });
        }

        public void AddCapability()
        {
            var actor = form?.Targets.Count == 1 ? form.Targets[0] : null;
            var count = actor?.capabilities?.Length ?? 0;
            if (actor == null || count >= MaximumCapabilities)
            {
                return;
            }

            session.Change("Capability added", () =>
            {
                actor.capabilities = (actor.capabilities ?? Array.Empty<WorldActorEditorCapability>())
                    .Concat(new[]
                    {
                        new WorldActorEditorCapability
                        {
                            id = CreateUniqueCapabilityId(actor, actor.id + ".capability_" + (count + 1)),
                            kind = WorldActorCapabilityKindIds.Dialogue,
                            displayName = "Talk",
                            menuOption = "Talk-to"
                        }
                    })
                    .ToArray();
            });
        }

        public void Dispose()
        {
            liveValidation.Pause();
            session?.Dispose();
            session = null;
        }

        private void PreviewChanges()
        {
            if (workspace == null)
            {
                return;
            }

            var validation = service.Validate(workspace);
            toolLayout.SetStatus(validation.Message, validation.Success ? HelpBoxMessageType.Info : HelpBoxMessageType.Error);
            RunLiveValidation();
            if (validation.Success)
            {
                WorldActorChangePreviewWindow.ShowPreview(validation);
            }
        }

        private void VerifyCanonical()
        {
            var result = service.VerifyCanonical();
            toolLayout.SetStatus(result.Output, result.Success ? HelpBoxMessageType.Info : HelpBoxMessageType.Error);
        }

        private void OnContentChanged()
        {
            list.SetItems(workspace.Catalog.actors ?? Array.Empty<WorldActorEditorDefinition>());
            UnsavedChangesChanged?.Invoke();
            liveValidation.ExecuteLater(LiveValidationDelayMilliseconds);
        }

        private void RunLiveValidation()
        {
            issues = WorldActorEditorIssues.Collect(workspace);
            list.SetIssueState(IssueState);
            toolLayout.Issues.SetIssues(issues, DescribeIssue);
            form?.ApplyIssues(issues);
        }

        private void OnSelectionChanged()
        {
            if (workspace == null || form == null)
            {
                return;
            }

            var selection = list.Selection;
            var shape = DetailShape(selection);
            if (!string.Equals(shape, detailShape, StringComparison.Ordinal))
            {
                detailShape = shape;
                BuildDetail(selection);
            }
            else
            {
                form.SetTargets(selection);
            }

            form.ApplyIssues(issues);
            duplicateButton.SetEnabled(selection.Count > 0);
            deleteButton.SetEnabled(selection.Count > 0);
        }

        // The detail panel is rebuilt only when its layout changes, so typing keeps focus.
        private static string DetailShape(IReadOnlyList<WorldActorEditorDefinition> selection)
        {
            if (selection.Count == 0)
            {
                return "none";
            }

            var kinds = string.Join(",", selection.Select(actor => actor.kind).Distinct().OrderBy(kind => kind, StringComparer.Ordinal));
            if (selection.Count > 1)
            {
                return "many:" + kinds;
            }

            var actor = selection[0];
            return "one:" + actor.id + ":" + kinds + ":" + (actor.capabilities?.Length ?? 0);
        }

        private void BuildDetail(IReadOnlyList<WorldActorEditorDefinition> selection)
        {
            form.Clear();
            toolLayout.Detail.Clear();
            if (selection.Count == 0)
            {
                ShowDetailHint("Select one or more actors, or create one from a template. Hold Ctrl or Shift to select several.");
                form.SetTargets(selection);
                return;
            }

            var single = selection.Count == 1;
            var definition = ContentToolStyles.CreateSection("Actor Definition");
            if (single)
            {
                AddIdentityFields(definition);
            }
            else
            {
                definition.Add(ContentToolStyles.CreateHint(
                    selection.Count + " actors selected. Changes apply to every selected actor. "
                        + "Id, display name, examine text, and capabilities are edited one actor at a time."));
            }

            definition.Add(form.Choice("Kind", "kind", () => Kinds, actor => actor.kind, SetKind));
            definition.Add(form.Choice("Faction", "factionId", FactionOptions, actor => actor.factionId, (actor, value) => actor.factionId = value));
            definition.Add(form.Choice("Disposition", "disposition", () => Dispositions, actor => actor.disposition, (actor, value) => actor.disposition = value));
            toolLayout.Detail.Add(definition);
            toolLayout.Detail.Add(BuildPresentationSection(single));
            toolLayout.Detail.Add(BuildTagSection());
            toolLayout.Detail.Add(BuildLifecycleSection(selection));
            toolLayout.Detail.Add(BuildBoundsSection(single));
            if (single)
            {
                toolLayout.Detail.Add(BuildCapabilitiesSection(selection[0]));
            }

            form.SetTargets(selection);
        }

        private void AddIdentityFields(VisualElement section)
        {
            var idField = new TextField("Stable Actor Id")
            {
                isDelayed = true,
                tooltip = "Renaming also updates the spawns of this World that place the actor."
            };
            idField.RegisterValueChangedCallback(change => RenameActor(change.previousValue, change.newValue));
            form.Watch(() =>
            {
                if (form.Targets.Count == 1)
                {
                    idField.SetValueWithoutNotify(form.Targets[0].id ?? string.Empty);
                }
            });
            form.Register("id", idField);
            section.Add(idField);
            section.Add(form.Text("Display Name", "displayName", actor => actor.displayName, (actor, value) => actor.displayName = value));
            var examine = form.Text("Examine Text", "examineText", actor => actor.examineText, (actor, value) => actor.examineText = value);
            examine.tooltip = "What Examine in the right-click menu shows: one line of up to "
                + WorldActorTextRules.MaximumExamineTextLength + " characters.";
            section.Add(examine);
        }

        private VisualElement BuildPresentationSection(bool single)
        {
            var section = ContentToolStyles.CreateSection("Presentation");
            section.Add(form.Choice(
                "Presentation Archetype",
                "presentationArchetypeId",
                PresentationOptions,
                actor => actor.presentationArchetypeId,
                SetPresentation));
            if (single)
            {
                section.Add(BuildPrefabMapping());
            }

            section.Add(ContentToolStyles.CreateHint(
                "The presentation archetype is neutral WorldData. Its replaceable prefab mapping remains a Unity presentation asset."));
            return section;
        }

        private VisualElement BuildTagSection()
        {
            var section = ContentToolStyles.CreateSection("Content Tags");
            section.Add(form.Flags(
                "Tags",
                "tags",
                TagOptions,
                actor => actor.tags,
                (actor, value) => actor.tags = value,
                "No actor uses a tag yet."));
            var add = new TextField("Add Tag") { isDelayed = true, tooltip = "Adds a tag to every selected actor." };
            add.RegisterValueChangedCallback(change =>
            {
                add.SetValueWithoutNotify(string.Empty);
                AddTag(change.newValue);
            });
            section.Add(add);
            return section;
        }

        private VisualElement BuildLifecycleSection(IReadOnlyList<WorldActorEditorDefinition> selection)
        {
            var section = ContentToolStyles.CreateSection("Combat And Lifecycle");
            if (selection.All(actor => actor.kind != WorldActorKindIds.Mob))
            {
                section.Add(ContentToolStyles.CreateHint(
                    "NPCs and resource nodes are always invulnerable and have no activity, respawn, combat, brain, locomotion, loot, or corpse settings. "
                        + "A resource node's charges and respawn come from its gathering capability's resource profile."));
                return section;
            }

            if (selection.Any(actor => actor.kind != WorldActorKindIds.Mob))
            {
                section.Add(ContentToolStyles.CreateHint("These settings apply to the selected mobs only."));
            }

            Func<WorldActorEditorDefinition, bool> isMob = actor => actor.kind == WorldActorKindIds.Mob;
            section.Add(form.Choice("Damage Policy", "damagePolicy", () => DamagePolicies, actor => actor.damagePolicy, (actor, value) => actor.damagePolicy = value, isMob));
            section.Add(form.Choice("Activity Profile", "activityProfileId", ActivityProfileOptions, actor => actor.activityProfileId, (actor, value) => actor.activityProfileId = value, isMob));
            section.Add(form.Choice("Respawn Profile", "respawnProfileId", RespawnProfileOptions, actor => actor.respawnProfileId, (actor, value) => actor.respawnProfileId = value, isMob));
            section.Add(form.Choice("Combat Profile", "combatProfileId", CombatProfileOptions, actor => actor.combatProfileId, (actor, value) => actor.combatProfileId = value, isMob));
            section.Add(form.Choice("Brain Profile", "brainProfileId", BrainProfileOptions, actor => actor.brainProfileId, (actor, value) => actor.brainProfileId = value, isMob));
            section.Add(form.Choice("Locomotion Profile", "locomotionProfileId", LocomotionProfileOptions, actor => actor.locomotionProfileId, (actor, value) => actor.locomotionProfileId = value, isMob));
            section.Add(form.Choice("Loot Profile", "lootProfileId", LootProfileOptions, actor => actor.lootProfileId, (actor, value) => actor.lootProfileId = value, isMob));
            section.Add(form.Choice("Corpse Persistence", "corpsePersistenceMode", () => CorpseModes, actor => actor.corpsePersistenceMode, (actor, value) => actor.corpsePersistenceMode = value, isMob));
            var lifetime = form.Float("Corpse Lifetime Seconds", "corpseLifetimeSeconds", actor => actor.corpseLifetimeSeconds, (actor, value) => actor.corpseLifetimeSeconds = value, isMob);
            lifetime.tooltip = "Between " + WorldActorCorpseRules.MinimumLifetimeSeconds + " and " + WorldActorCorpseRules.MaximumLifetimeSeconds + " seconds.";
            section.Add(lifetime);
            section.Add(ContentToolStyles.CreateHint(
                "Live corpses remain worker-owned and may disappear on restart. Durable corpses use PostgreSQL custody and restart restoration."));
            return section;
        }

        private VisualElement BuildBoundsSection(bool single)
        {
            var section = ContentToolStyles.CreateSection("Authoritative Interaction Bounds");
            section.Add(form.Vector3(
                "Local Center",
                "interactionBounds.center",
                actor => actor.interactionBounds == null
                    ? Vector3.zero
                    : new Vector3(actor.interactionBounds.centerX, actor.interactionBounds.centerY, actor.interactionBounds.centerZ),
                (actor, value) =>
                {
                    actor.interactionBounds = actor.interactionBounds ?? new WorldActorEditorBounds();
                    actor.interactionBounds.centerX = value.x;
                    actor.interactionBounds.centerY = value.y;
                    actor.interactionBounds.centerZ = value.z;
                }));
            form.Covers("interactionBounds.center", "interactionBounds.centerX", "interactionBounds.centerY", "interactionBounds.centerZ");
            section.Add(form.Vector3(
                "Size",
                "interactionBounds.size",
                actor => actor.interactionBounds == null
                    ? Vector3.zero
                    : new Vector3(actor.interactionBounds.sizeX, actor.interactionBounds.sizeY, actor.interactionBounds.sizeZ),
                (actor, value) =>
                {
                    actor.interactionBounds = actor.interactionBounds ?? new WorldActorEditorBounds();
                    actor.interactionBounds.sizeX = value.x;
                    actor.interactionBounds.sizeY = value.y;
                    actor.interactionBounds.sizeZ = value.z;
                }));
            form.Covers("interactionBounds.size", "interactionBounds.sizeX", "interactionBounds.sizeY", "interactionBounds.sizeZ");
            if (single)
            {
                section.Add(BuildBoundsPreview());
            }

            return section;
        }

        // A front view of the bounds at 32 pixels per unit.
        private VisualElement BuildBoundsPreview()
        {
            const float width = 220f;
            const float height = 100f;
            var preview = new VisualElement();
            preview.AddToClassList("content-bounds-preview");
            preview.style.width = width;
            preview.style.height = height;
            var box = new VisualElement();
            box.AddToClassList("content-bounds-preview__box");
            preview.Add(box);
            form.Watch(() =>
            {
                var bounds = form.Targets.Count == 1 ? form.Targets[0].interactionBounds : null;
                var boxWidth = Mathf.Clamp((bounds?.sizeX ?? 0f) * 32f, 4f, width - 12f);
                var boxHeight = Mathf.Clamp((bounds?.sizeY ?? 0f) * 32f, 4f, height - 12f);
                box.style.left = (width - boxWidth) * 0.5f;
                box.style.top = (height - boxHeight) * 0.5f;
                box.style.width = boxWidth;
                box.style.height = boxHeight;
            });
            return preview;
        }

        private VisualElement BuildCapabilitiesSection(WorldActorEditorDefinition actor)
        {
            var section = ContentToolStyles.CreateSection("Composable Capabilities");
            var capabilities = actor.capabilities ?? Array.Empty<WorldActorEditorCapability>();
            var mob = actor.kind == WorldActorKindIds.Mob;
            if (mob)
            {
                section.Add(ContentToolStyles.CreateHint(capabilities.Length == 0
                    ? "Mobs have no capabilities."
                    : "Mobs have no capabilities. Remove these before saving."));
            }

            for (var position = 0; position < capabilities.Length; position++)
            {
                section.Add(BuildCapability(position, capabilities.Length));
            }

            if (!mob)
            {
                var add = new Button(AddCapability) { text = "Add Capability" };
                add.SetEnabled(capabilities.Length < MaximumCapabilities);
                section.Add(add);
            }

            return section;
        }

        private VisualElement BuildCapability(int position, int count)
        {
            var box = new VisualElement();
            box.AddToClassList(ContentToolStyles.Section);
            var path = "capabilities[" + position + "]";
            box.Add(form.Text("Stable Capability Id", path + ".id", actor => CapabilityAt(actor, position)?.id, (actor, value) => EditCapability(actor, position, capability => capability.id = value)));
            box.Add(form.Choice("Kind", path + ".kind", () => CapabilityKinds, actor => CapabilityAt(actor, position)?.kind, (actor, value) => EditCapability(actor, position, capability => SetCapabilityKind(capability, value))));
            box.Add(form.Text("Display Name", path + ".displayName", actor => CapabilityAt(actor, position)?.displayName, (actor, value) => EditCapability(actor, position, capability => capability.displayName = value)));
            var menuOption = form.Text("Menu Option", path + ".menuOption", actor => CapabilityAt(actor, position)?.menuOption, (actor, value) => EditCapability(actor, position, capability => capability.menuOption = value));
            menuOption.tooltip = "The verb the right-click menu shows before the actor's name, such as Trade or Mine. The first capability is the left-click action.";
            box.Add(menuOption);
            var profile = form.Choice(
                "Profile",
                path + ".profileId",
                () => CapabilityProfileOptions(form.Targets.Count == 1 ? CapabilityAt(form.Targets[0], position) : null),
                actor => CapabilityAt(actor, position)?.profileId,
                (actor, value) => EditCapability(actor, position, capability => capability.profileId = value),
                actor => UsesProfile(CapabilityAt(actor, position)?.kind));
            profile.tooltip = "A vendor names an item catalog vendor profile; a gathering capability names a resource profile from catalog.json.";
            box.Add(profile);
            var buttons = new VisualElement();
            buttons.AddToClassList(ContentToolStyles.Row);
            var up = new Button(() => MoveCapability(position, -1)) { text = "Move Up" };
            up.SetEnabled(position > 0);
            var down = new Button(() => MoveCapability(position, 1)) { text = "Move Down" };
            down.SetEnabled(position < count - 1);
            buttons.Add(up);
            buttons.Add(down);
            buttons.Add(new Button(() => RemoveCapability(position)) { text = "Remove" });
            box.Add(buttons);
            return box;
        }

        private VisualElement BuildPrefabMapping()
        {
            var container = new VisualElement();
            var registry = AssetDatabase.LoadAssetAtPath<ScriptableObject>(PresentationRegistryPath);
            if (registry == null)
            {
                container.Add(new HelpBox("No runtime presentation registry asset exists at " + PresentationRegistryPath + ".", HelpBoxMessageType.Warning));
                container.Add(new Button(() =>
                {
                    if (CreatePresentationRegistry() != null)
                    {
                        detailShape = string.Empty;
                        OnSelectionChanged();
                    }
                })
                { text = "Create Presentation Registry" });
                return container;
            }

            var prefab = new ObjectField("Replaceable Prefab")
            {
                objectType = typeof(GameObject),
                allowSceneObjects = false,
                tooltip = "The Unity prefab of this presentation archetype, stored as an Addressables reference in the presentation registry asset. It must be below " + PresentationPrefabFolder + "."
            };
            Action refresh = () => prefab.SetValueWithoutNotify(FindMappedPrefab(registry, SelectedPresentationId()));
            prefab.RegisterValueChangedCallback(change => SetMappedPrefab(registry, SelectedPresentationId(), change.newValue as GameObject));
            // The registry is a Unity asset with its own undo, so the field follows it directly.
            prefab.TrackSerializedObjectValue(new SerializedObject(registry), _ => refresh());
            form.Watch(refresh);
            container.Add(prefab);
            return container;
        }

        private string SelectedPresentationId()
        {
            return form.Targets.Count == 1 ? form.Targets[0].presentationArchetypeId : null;
        }

        // Switching kind clears the settings the new kind must not have and fills the ones it
        // needs, so the actor stays valid.
        private void SetKind(WorldActorEditorDefinition actor, string kind)
        {
            if (string.Equals(actor.kind, kind, StringComparison.Ordinal))
            {
                return;
            }

            actor.kind = kind;
            if (kind != WorldActorKindIds.Mob)
            {
                actor.damagePolicy = WorldActorDamagePolicyIds.Invulnerable;
                actor.activityProfileId = string.Empty;
                actor.respawnProfileId = string.Empty;
                actor.corpsePersistenceMode = string.Empty;
                actor.corpseLifetimeSeconds = 0f;
                actor.combatProfileId = string.Empty;
                actor.brainProfileId = string.Empty;
                actor.locomotionProfileId = string.Empty;
                actor.lootProfileId = string.Empty;
                actor.capabilities = kind == WorldActorKindIds.ResourceNode
                    ? new[] { CreateGatheringCapability(actor) }
                    : (actor.capabilities ?? Array.Empty<WorldActorEditorCapability>())
                        .Where(capability => capability?.kind != WorldActorCapabilityKindIds.Gathering)
                        .ToArray();
            }
            else
            {
                actor.damagePolicy = WorldActorDamagePolicyIds.Damageable;
                actor.capabilities = Array.Empty<WorldActorEditorCapability>();
                actor.activityProfileId = string.IsNullOrEmpty(actor.activityProfileId)
                    ? workspace.Catalog.activityProfiles?.FirstOrDefault()?.id ?? string.Empty
                    : actor.activityProfileId;
                actor.respawnProfileId = string.IsNullOrEmpty(actor.respawnProfileId)
                    ? workspace.Catalog.respawnProfiles?.FirstOrDefault()?.id ?? string.Empty
                    : actor.respawnProfileId;
                actor.corpsePersistenceMode = string.IsNullOrEmpty(actor.corpsePersistenceMode)
                    ? WorldActorCorpsePersistenceModeIds.Live
                    : actor.corpsePersistenceMode;
                actor.corpseLifetimeSeconds = actor.corpseLifetimeSeconds > 0f
                    ? actor.corpseLifetimeSeconds
                    : WorldActorCorpseRules.DefaultLiveLifetimeSeconds;
                actor.combatProfileId = string.IsNullOrEmpty(actor.combatProfileId)
                    ? workspace.Catalog.combatProfiles?.FirstOrDefault()?.id ?? string.Empty
                    : actor.combatProfileId;
                actor.brainProfileId = string.IsNullOrEmpty(actor.brainProfileId)
                    ? workspace.Catalog.brainProfiles?.FirstOrDefault()?.id ?? string.Empty
                    : actor.brainProfileId;
                actor.locomotionProfileId = string.IsNullOrEmpty(actor.locomotionProfileId)
                    ? workspace.Catalog.locomotionProfiles?.FirstOrDefault()?.id ?? string.Empty
                    : actor.locomotionProfileId;
                actor.lootProfileId = actor.lootProfileId ?? string.Empty;
            }

            var presentationKind = workspace.Catalog.presentationArchetypes?.FirstOrDefault(value =>
                string.Equals(value.id, actor.presentationArchetypeId, StringComparison.Ordinal))?.actorKind;
            if (!string.Equals(presentationKind, kind, StringComparison.Ordinal))
            {
                actor.presentationArchetypeId = workspace.Catalog.presentationArchetypes?
                    .FirstOrDefault(value => string.Equals(value.actorKind, kind, StringComparison.Ordinal))?.id ?? string.Empty;
            }
        }

        // A presentation archetype only fits actors of its own kind.
        private void SetPresentation(WorldActorEditorDefinition actor, string presentationId)
        {
            var presentation = workspace.Catalog.presentationArchetypes?.FirstOrDefault(value =>
                string.Equals(value.id, presentationId, StringComparison.Ordinal));
            if (presentation == null || string.Equals(presentation.actorKind, actor.kind, StringComparison.Ordinal))
            {
                actor.presentationArchetypeId = presentationId;
            }
        }

        private void MoveCapability(int position, int offset)
        {
            var actor = form.Targets.Count == 1 ? form.Targets[0] : null;
            var target = position + offset;
            if (actor?.capabilities == null || target < 0 || target >= actor.capabilities.Length)
            {
                return;
            }

            session.Change(offset < 0 ? "Capability moved up" : "Capability moved down", () =>
            {
                var capabilities = actor.capabilities.ToArray();
                (capabilities[position], capabilities[target]) = (capabilities[target], capabilities[position]);
                actor.capabilities = capabilities;
            });
        }

        private void RemoveCapability(int position)
        {
            var actor = form.Targets.Count == 1 ? form.Targets[0] : null;
            if (actor?.capabilities == null || position >= actor.capabilities.Length)
            {
                return;
            }

            session.Change("Capability removed", () =>
                actor.capabilities = actor.capabilities.Where((_, index) => index != position).ToArray());
        }

        private static WorldActorEditorCapability CapabilityAt(WorldActorEditorDefinition actor, int position)
        {
            var capabilities = actor.capabilities;
            return capabilities != null && position < capabilities.Length ? capabilities[position] : null;
        }

        private static void EditCapability(WorldActorEditorDefinition actor, int position, Action<WorldActorEditorCapability> edit)
        {
            var capability = CapabilityAt(actor, position);
            if (capability != null)
            {
                edit(capability);
            }
        }

        private WorldActorEditorDefinition CreateTemplate(string kind, int index)
        {
            if (kind == WorldActorKindIds.ResourceNode)
            {
                return CreateResourceNodeTemplate(index);
            }

            var npc = kind == WorldActorKindIds.Npc;
            return new WorldActorEditorDefinition
            {
                id = CreateUniqueActorId((npc ? "npc.new_actor_" : "mob.new_actor_") + index),
                displayName = npc ? "New NPC" : "New Mob",
                examineText = npc ? "A new NPC." : "A new Mob.",
                kind = kind,
                factionId = workspace.Catalog.factions?.FirstOrDefault()?.id ?? string.Empty,
                disposition = npc ? WorldActorDispositionIds.Friendly : WorldActorDispositionIds.Hostile,
                presentationArchetypeId = workspace.Catalog.presentationArchetypes?
                    .FirstOrDefault(value => value.actorKind == kind)?.id ?? string.Empty,
                damagePolicy = npc ? WorldActorDamagePolicyIds.Invulnerable : WorldActorDamagePolicyIds.Damageable,
                interactionBounds = new WorldActorEditorBounds(),
                capabilities = Array.Empty<WorldActorEditorCapability>(),
                tags = npc ? new[] { "city", "service" } : new[] { "wild" },
                activityProfileId = npc ? string.Empty : workspace.Catalog.activityProfiles?.FirstOrDefault()?.id ?? string.Empty,
                respawnProfileId = npc ? string.Empty : workspace.Catalog.respawnProfiles?.FirstOrDefault()?.id ?? string.Empty,
                corpsePersistenceMode = npc ? string.Empty : WorldActorCorpsePersistenceModeIds.Live,
                corpseLifetimeSeconds = npc ? 0f : WorldActorCorpseRules.DefaultLiveLifetimeSeconds,
                combatProfileId = npc ? string.Empty : workspace.Catalog.combatProfiles?.FirstOrDefault()?.id ?? string.Empty,
                brainProfileId = npc ? string.Empty : workspace.Catalog.brainProfiles?.FirstOrDefault()?.id ?? string.Empty,
                locomotionProfileId = npc ? string.Empty : workspace.Catalog.locomotionProfiles?.FirstOrDefault()?.id ?? string.Empty,
                lootProfileId = string.Empty
            };
        }

        // A resource node is a neutral, invulnerable world object with one gathering capability.
        private WorldActorEditorDefinition CreateResourceNodeTemplate(int index)
        {
            var node = new WorldActorEditorDefinition
            {
                id = CreateUniqueActorId("resource.new_node_" + index),
                displayName = "New Resource Node",
                examineText = "A new resource node.",
                kind = WorldActorKindIds.ResourceNode,
                factionId = (workspace.Catalog.factions?.FirstOrDefault(faction =>
                        faction?.defaultDisposition == WorldActorDispositionIds.Neutral)
                    ?? workspace.Catalog.factions?.FirstOrDefault())?.id ?? string.Empty,
                disposition = WorldActorDispositionIds.Neutral,
                presentationArchetypeId = workspace.Catalog.presentationArchetypes?
                    .FirstOrDefault(value => value.actorKind == WorldActorKindIds.ResourceNode)?.id ?? string.Empty,
                damagePolicy = WorldActorDamagePolicyIds.Invulnerable,
                interactionBounds = new WorldActorEditorBounds
                {
                    centerY = 0.6f,
                    sizeX = 1.6f,
                    sizeY = 1.2f,
                    sizeZ = 1.6f
                },
                tags = new[] { "resource" },
                activityProfileId = string.Empty,
                respawnProfileId = string.Empty,
                corpsePersistenceMode = string.Empty,
                corpseLifetimeSeconds = 0f,
                combatProfileId = string.Empty,
                brainProfileId = string.Empty,
                locomotionProfileId = string.Empty,
                lootProfileId = string.Empty
            };
            node.capabilities = new[] { CreateGatheringCapability(node) };
            return node;
        }

        private WorldActorEditorCapability CreateGatheringCapability(WorldActorEditorDefinition actor)
        {
            var existing = actor.capabilities?.FirstOrDefault(capability =>
                capability?.kind == WorldActorCapabilityKindIds.Gathering);
            return existing ?? new WorldActorEditorCapability
            {
                id = CreateUniqueCapabilityId(actor, actor.id + ".gather"),
                kind = WorldActorCapabilityKindIds.Gathering,
                displayName = "Gather",
                menuOption = "Gather",
                profileId = workspace.Catalog.resourceProfiles?.FirstOrDefault()?.id ?? string.Empty
            };
        }

        // A kind without a profile clears it, and a kind with one starts at the first choice.
        private void SetCapabilityKind(WorldActorEditorCapability capability, string kind)
        {
            capability.kind = kind;
            var options = CapabilityProfileOptions(capability).Select(option => option.Value).ToArray();
            capability.profileId = !UsesProfile(kind)
                ? string.Empty
                : options.Contains(capability.profileId, StringComparer.Ordinal)
                    ? capability.profileId
                    : options.FirstOrDefault() ?? string.Empty;
        }

        private static bool UsesProfile(string capabilityKind)
        {
            return capabilityKind == WorldActorCapabilityKindIds.Vendor
                || capabilityKind == WorldActorCapabilityKindIds.Gathering;
        }

        private IEnumerable<(string Value, string Label)> CapabilityProfileOptions(WorldActorEditorCapability capability)
        {
            if (capability?.kind == WorldActorCapabilityKindIds.Vendor)
            {
                return (workspace?.ItemReferences.vendors ?? Array.Empty<WorldActorEditorItemReference>())
                    .Select(vendor => (vendor.id, vendor.displayName + " (" + vendor.id + ")"));
            }

            if (capability?.kind == WorldActorCapabilityKindIds.Gathering)
            {
                return (workspace?.Catalog.resourceProfiles ?? Array.Empty<WorldActorEditorResourceProfile>())
                    .Select(profile => (profile.id, profile.id + " (" + profile.yieldDefinitionId + ", " + profile.requiredToolKind + ")"));
            }

            return new[] { (string.Empty, "(none)") };
        }

        private string CreateUniqueActorId(string candidate)
        {
            return CreateUnique(candidate, (workspace.Catalog.actors ?? Array.Empty<WorldActorEditorDefinition>()).Select(actor => actor.id));
        }

        private static string CreateUniqueCapabilityId(WorldActorEditorDefinition actor, string candidate)
        {
            return CreateUnique(candidate, (actor.capabilities ?? Array.Empty<WorldActorEditorCapability>()).Select(capability => capability?.id));
        }

        private static string CreateUnique(string candidate, IEnumerable<string> used)
        {
            var existing = new HashSet<string>(used.Where(value => value != null), StringComparer.Ordinal);
            var value = candidate;
            var suffix = 2;
            while (existing.Contains(value))
            {
                value = candidate + "_" + suffix;
                suffix++;
            }

            return value;
        }

        // Where spawns place the actor, or empty when nothing does.
        private string PlacementSummary(string actorId)
        {
            var parts = new List<string>();
            var local = workspace.Spawns.CountActorReferences(actorId);
            if (local > 0)
            {
                parts.Add(local + (local == 1 ? " spawn" : " spawns") + " in World '" + service.Paths.WorldId + "'");
            }

            var otherWorlds = OtherWorldReferences(actorId);
            if (otherWorlds.Length > 0)
            {
                parts.Add("spawns in World " + string.Join(", ", otherWorlds.Select(world => "'" + world + "'")));
            }

            return string.Join(" and ", parts);
        }

        private string[] OtherWorldReferences(string actorId)
        {
            try
            {
                return service.FindOtherWorldReferences(actorId);
            }
            catch (Exception exception)
            {
                // Unreadable World content counts as a reference, so nothing is lost silently.
                return new[] { "(unreadable: " + exception.Message + ")" };
            }
        }

        private void ShowIssue(ContentIssue issue)
        {
            if (!issue.EntityKey.StartsWith(WorldActorEditorIssues.Actors + "/", StringComparison.Ordinal))
            {
                if (WorldActorEditorIssues.TrySplitKey(issue.EntityKey, out var collection, out _)
                    && WorldActorEditorIssues.IsSpawnCollection(collection))
                {
                    toolLayout.SetStatus("Spawn entries and patrol paths are edited in Spawn Authoring.", HelpBoxMessageType.Info);
                }

                return;
            }

            list.Select(new[] { issue.EntityKey });
            var field = form.FindField(issue.Field);
            if (field != null)
            {
                toolLayout.Detail.ScrollTo(field);
                field.Focus();
            }
        }

        private void ShowDetailHint(string text)
        {
            toolLayout.Detail.Clear();
            toolLayout.Detail.Add(ContentToolStyles.CreateHint(text));
        }

        private ContentIssueSeverity? IssueState(WorldActorEditorDefinition actor)
        {
            var key = Key(actor);
            var related = issues.Where(issue => string.Equals(issue.EntityKey, key, StringComparison.Ordinal)).ToArray();
            if (related.Length == 0)
            {
                return null;
            }

            return related.Any(issue => issue.Severity == ContentIssueSeverity.Error)
                ? ContentIssueSeverity.Error
                : ContentIssueSeverity.Warning;
        }

        private string DescribeIssue(ContentIssue issue)
        {
            return WorldActorEditorIssues.Describe(issue, workspace);
        }

        private static string SearchText(WorldActorEditorDefinition actor)
        {
            return string.Join(" ", (actor.tags ?? Array.Empty<string>())
                .Concat((actor.capabilities ?? Array.Empty<WorldActorEditorCapability>())
                    .Where(capability => capability != null)
                    .SelectMany(capability => new[] { capability.kind, capability.displayName, capability.menuOption })));
        }

        private IEnumerable<(string Value, string Label)> FactionOptions()
        {
            return (workspace?.Catalog.factions ?? Array.Empty<WorldActorEditorFaction>())
                .Select(faction => (faction.id, faction.displayName + " (" + faction.id + ")"));
        }

        private IEnumerable<(string Value, string Label)> TagOptions()
        {
            return (workspace?.Catalog.actors ?? Array.Empty<WorldActorEditorDefinition>())
                .SelectMany(actor => actor.tags ?? Array.Empty<string>())
                .Where(tag => !string.IsNullOrEmpty(tag))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(tag => tag, StringComparer.Ordinal)
                .Select(tag => (tag, tag));
        }

        private IEnumerable<(string Value, string Label)> ActivityProfileOptions()
        {
            return (workspace?.Catalog.activityProfiles ?? Array.Empty<WorldActorEditorActivityProfile>())
                .Select(profile => (profile.id, profile.id));
        }

        private IEnumerable<(string Value, string Label)> RespawnProfileOptions()
        {
            return (workspace?.Catalog.respawnProfiles ?? Array.Empty<WorldActorEditorRespawnProfile>())
                .Select(profile => (profile.id, profile.id));
        }

        private IEnumerable<(string Value, string Label)> CombatProfileOptions()
        {
            return (workspace?.Catalog.combatProfiles ?? Array.Empty<WorldActorEditorCombatProfile>())
                .Select(profile => (profile.id, profile.id + " (" + profile.maximumHealth + " health)"));
        }

        private IEnumerable<(string Value, string Label)> BrainProfileOptions()
        {
            return (workspace?.Catalog.brainProfiles ?? Array.Empty<WorldActorEditorBrainProfile>())
                .Select(profile => (profile.id, profile.id + " (" + profile.aggression + ")"));
        }

        private IEnumerable<(string Value, string Label)> LocomotionProfileOptions()
        {
            return (workspace?.Catalog.locomotionProfiles ?? Array.Empty<WorldActorEditorLocomotionProfile>())
                .Select(profile => (profile.id, profile.id));
        }

        // A Mob without a loot profile drops nothing and leaves no corpse.
        private IEnumerable<(string Value, string Label)> LootProfileOptions()
        {
            return new[] { (string.Empty, "(no loot)") }.Concat(
                (workspace?.Catalog.lootProfiles ?? Array.Empty<WorldActorEditorLootProfile>())
                    .Select(profile => (profile.id, profile.id)));
        }

        // Archetypes of the selected kind, or of every kind when the selection mixes kinds.
        private IEnumerable<(string Value, string Label)> PresentationOptions()
        {
            var kinds = form.Targets.Select(actor => actor.kind).Distinct().ToArray();
            return (workspace?.Catalog.presentationArchetypes ?? Array.Empty<WorldActorEditorPresentation>())
                .Where(presentation => kinds.Length != 1 || string.Equals(presentation.actorKind, kinds[0], StringComparison.Ordinal))
                .Select(presentation => (presentation.id, presentation.displayName + " (" + presentation.id + ")"));
        }

        private static string Label((string Value, string Label)[] options, string value)
        {
            var match = options.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.Ordinal));
            return match.Label ?? value;
        }

        private static bool ConfirmChanges(string[] changes)
        {
            return EditorUtility.DisplayDialog(
                "Compile World Actors",
                string.Join("\n", changes) + "\n\nWrite authoring and canonical runtime files?",
                "Write files",
                "Cancel");
        }

        private static GameObject FindMappedPrefab(ScriptableObject registry, string presentationArchetypeId)
        {
            var entries = new SerializedObject(registry).FindProperty("entries");
            if (entries == null || !entries.isArray || string.IsNullOrEmpty(presentationArchetypeId))
            {
                return null;
            }

            for (var index = 0; index < entries.arraySize; index++)
            {
                var entry = entries.GetArrayElementAtIndex(index);
                if (string.Equals(entry.FindPropertyRelative("presentationArchetypeId")?.stringValue, presentationArchetypeId, StringComparison.Ordinal))
                {
                    return LoadReferencedPrefab(entry.FindPropertyRelative("prefab"));
                }
            }

            return null;
        }

        // Resolves the Addressables reference stored in a registry entry's prefab field.
        public static GameObject LoadReferencedPrefab(SerializedProperty prefabReference)
        {
            var guid = prefabReference?.FindPropertyRelative("m_AssetGUID")?.stringValue;
            return string.IsNullOrEmpty(guid)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
        }

        // Records the prefab in the registry asset through a SerializedObject, so the change
        // has its own step in Unity's Undo.
        private void SetMappedPrefab(ScriptableObject registry, string presentationArchetypeId, GameObject prefab)
        {
            if (string.IsNullOrEmpty(presentationArchetypeId))
            {
                return;
            }

            var prefabPath = prefab == null ? string.Empty : AssetDatabase.GetAssetPath(prefab);
            if (prefab != null && !prefabPath.StartsWith(PresentationPrefabFolder + "/", StringComparison.Ordinal))
            {
                toolLayout.SetStatus("Presentation prefabs must be stored below " + PresentationPrefabFolder + " so they are Addressable.", HelpBoxMessageType.Error);
                form.Refresh();
                return;
            }

            var serialized = new SerializedObject(registry);
            var entries = serialized.FindProperty("entries");
            if (entries == null || !entries.isArray)
            {
                toolLayout.SetStatus("The presentation registry asset has an incompatible schema.", HelpBoxMessageType.Error);
                return;
            }

            var entryIndex = -1;
            for (var index = 0; index < entries.arraySize; index++)
            {
                if (string.Equals(entries.GetArrayElementAtIndex(index).FindPropertyRelative("presentationArchetypeId")?.stringValue, presentationArchetypeId, StringComparison.Ordinal))
                {
                    entryIndex = index;
                    break;
                }
            }

            if (entryIndex < 0)
            {
                entryIndex = entries.arraySize;
                entries.InsertArrayElementAtIndex(entryIndex);
            }

            var selectedEntry = entries.GetArrayElementAtIndex(entryIndex);
            selectedEntry.FindPropertyRelative("presentationArchetypeId").stringValue = presentationArchetypeId;
            var reference = selectedEntry.FindPropertyRelative("prefab");
            reference.FindPropertyRelative("m_AssetGUID").stringValue = prefab == null
                ? string.Empty
                : AssetDatabase.AssetPathToGUID(prefabPath);
            foreach (var subObjectField in new[] { "m_SubObjectName", "m_SubObjectType", "m_SubObjectGUID" })
            {
                var subObject = reference.FindPropertyRelative(subObjectField);
                if (subObject != null)
                {
                    subObject.stringValue = string.Empty;
                }
            }
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(registry);
        }

        private static ScriptableObject CreatePresentationRegistry()
        {
            var scriptGuid = AssetDatabase.FindAssets("WorldActorPresentationRegistry t:MonoScript").FirstOrDefault();
            var script = string.IsNullOrEmpty(scriptGuid)
                ? null
                : AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(scriptGuid));
            var registryType = script?.GetClass();
            if (registryType == null || !typeof(ScriptableObject).IsAssignableFrom(registryType))
            {
                Debug.LogError("WorldActorPresentationRegistry runtime type could not be resolved.");
                return null;
            }

            if (!AssetDatabase.IsValidFolder("Assets/Content"))
            {
                AssetDatabase.CreateFolder("Assets", "Content");
            }

            if (!AssetDatabase.IsValidFolder("Assets/Content/WorldActors"))
            {
                AssetDatabase.CreateFolder("Assets/Content", "WorldActors");
            }

            var registry = ScriptableObject.CreateInstance(registryType);
            AssetDatabase.CreateAsset(registry, PresentationRegistryPath);
            AssetDatabase.SaveAssets();
            Selection.activeObject = registry;
            return registry;
        }
    }
}
