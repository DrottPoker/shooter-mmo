using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Editor.ContentTools;
using Mmorpg.WorldData.Quests;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Mmorpg.WorldData.Editor.Quests
{
    // The Quest Studio tool: a filterable list of quests beside an editor for the selected
    // ones, the chain and NPC views, live validation with the shared compiler, and the preview
    // and save actions. Several selected quests are edited together where their fields agree;
    // the id, title, texts, requirements, items, and steps of one quest are edited on their own.
    public sealed class QuestStudioView : VisualElement, IDisposable
    {
        public const long LiveValidationDelayMilliseconds = 300;
        public const string StartItems = "startItems";
        public const string RewardItems = "rewardItems";
        public const string RewardChoiceItems = "rewardChoiceItems";
        public const string ChainNodeClass = "content-chain-node";

        // How a kill objective names its Mobs.
        public const string ListedMobs = "mobs";
        public const string MobTag = "tag";

        private static readonly (string Value, string Label)[] ObjectiveKinds =
        {
            (QuestObjectiveKindIds.Kill, "Kill"),
            (QuestObjectiveKindIds.Collect, "Collect"),
            (QuestObjectiveKindIds.Deliver, "Deliver"),
            (QuestObjectiveKindIds.TalkTo, "Talk To"),
            (QuestObjectiveKindIds.Gather, "Gather")
        };

        private static readonly (string Value, string Label)[] KillTargets =
        {
            (ListedMobs, "Listed Mobs"),
            (MobTag, "Mobs With A Tag")
        };

        private static readonly Regex ControlCharacters = new Regex(@"\p{Cc}+", RegexOptions.CultureInvariant);

        private static readonly string MaximumActiveQuestsTooltip =
            "How many quests a character can have active at once, between "
            + QuestContentRules.MinimumActiveQuestLimit + " and " + QuestContentRules.MaximumActiveQuestLimit + ".";

        private readonly QuestEditorService service;
        private readonly ContentUndoState undoState;
        private readonly Func<QuestEditorWorkspace> loadWorkspace;
        private readonly ContentToolLayout toolLayout;
        private readonly ContentListPane<QuestEditorDefinition> list;
        private readonly Button duplicateButton;
        private readonly Button deleteButton;
        private readonly IVisualElementScheduledItem liveValidation;
        private ContentEditSession<QuestEditorCatalog> session;
        private ContentForm<QuestEditorDefinition> form;
        private QuestEditorWorkspace workspace;
        private IReadOnlyList<ContentIssue> issues = Array.Empty<ContentIssue>();
        private IReadOnlyList<string> nonCanonicalFiles = Array.Empty<string>();
        private IntegerField maximumActiveQuestsField;
        private string detailShape = string.Empty;
        private string npcViewActorId = string.Empty;

        public QuestStudioView(
            QuestEditorService service,
            ContentUndoState undoState,
            Func<QuestEditorWorkspace> loadWorkspace = null)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.undoState = undoState ? undoState : throw new ArgumentNullException(nameof(undoState));
            this.loadWorkspace = loadWorkspace ?? service.Load;
            style.flexGrow = 1f;

            toolLayout = new ContentToolLayout(300f);
            Add(toolLayout);
            toolLayout.Toolbar.Add(new ToolbarButton(() => Reload())
            {
                text = "Reload",
                tooltip = "Load the quests and the compiled items and actors from disk again; unsaved edits can stay on top of them."
            });
            toolLayout.Toolbar.Add(new ToolbarButton(RefreshReferences)
            {
                text = "Reload References",
                tooltip = "Read the compiled items and actors again, after a save in the Item Catalog or Actor Studio. Quest edits stay."
            });
            var create = new ToolbarMenu { text = "New Quest", tooltip = "Create a quest from a template." };
            foreach (var template in QuestEditorTemplates.All)
            {
                create.menu.AppendAction(
                    QuestEditorTemplates.Label(template),
                    _ => AddQuest(template),
                    _ => workspace == null ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);
            }

            toolLayout.Toolbar.Add(create);
            toolLayout.Toolbar.Add(new ToolbarSpacer { flex = true });

            list = new ContentListPane<QuestEditorDefinition>(
                "Quests",
                Key,
                quest => string.IsNullOrEmpty(quest.title) ? "(untitled)" : quest.title,
                quest => quest.id + " | " + NpcSummary(quest),
                SearchText);
            list.AddChoiceFilter("All givers", () => UsedNpcOptions(quest => quest.giverActorId), (quest, value) => quest.giverActorId == value);
            list.AddChoiceFilter("All turn-in NPCs", () => UsedNpcOptions(quest => quest.turnInActorId), (quest, value) => quest.turnInActorId == value);
            list.AddChoiceFilter("All objective kinds", () => ObjectiveKinds, (quest, value) => quest.Objectives().Any(objective => objective.kind == value));
            list.AddChoiceFilter("All tags", TagOptions, (quest, value) => (quest.tags ?? Array.Empty<string>()).Contains(value, StringComparer.Ordinal));
            list.AddToggleFilter("Problems", quest => IssueState(quest) != null);
            list.SelectionChanged += OnSelectionChanged;
            toolLayout.ListSlot.Add(list);
            var listActions = new VisualElement();
            listActions.AddToClassList(ContentToolStyles.Row);
            duplicateButton = new Button(DuplicateSelected) { text = "Duplicate", tooltip = "Copy every selected quest with a new id." };
            deleteButton = new Button(() => DeleteSelected())
            {
                text = "Delete",
                tooltip = "Delete the selected quests that no other quest requires. A saved quest's id is retired."
            };
            listActions.Add(duplicateButton);
            listActions.Add(deleteButton);
            toolLayout.ListSlot.Add(listActions);

            toolLayout.Actions.Add(new Button(PreviewChanges) { text = "Preview Changes", tooltip = "Compile with the quest compiler and show the canonical files." });
            toolLayout.Actions.Add(new Button(() => Save()) { text = "Save + Compile", tooltip = "Write the quest files and the compiled quest catalog." });
            toolLayout.Actions.Add(new Button(VerifyCanonical) { text = "Verify Canonical", tooltip = "Check that the committed quest catalog matches the quest files." });
            var info = new Label(
                "Quests name NPCs, Mobs, resource nodes, and items as the item and actor compilers last wrote them. "
                    + "Save the Item Catalog and Actor Studio first, then use Reload References.");
            info.AddToClassList("content-actions__info");
            toolLayout.Actions.Add(info);
            toolLayout.Issues.IssueActivated += ShowIssue;

            liveValidation = schedule.Execute(RunLiveValidation);
            liveValidation.Pause();
            ShowDetailHint("Loading quest content...");
        }

        public event Action UnsavedChangesChanged;

        // Also edits from before a script reload whose content could not be loaded again, which
        // the undo state still holds for a later load.
        public bool HasUnsavedChanges => session != null ? session.HasUnsavedChanges : undoState.HasPendingEdits;

        public QuestEditorWorkspace Workspace => workspace;

        public ContentListPane<QuestEditorDefinition> List => list;

        public ContentForm<QuestEditorDefinition> Form => form;

        public IReadOnlyList<ContentIssue> Issues => issues;

        public string StatusText => toolLayout.StatusText;

        public HelpBoxMessageType StatusType => toolLayout.StatusType;

        public VisualElement Detail => toolLayout.Detail;

        // The NPC whose quests the NPC view lists.
        public string NpcViewActorId => npcViewActorId;

        public static string Key(QuestEditorDefinition quest)
        {
            return quest?.id ?? string.Empty;
        }

        // Loads quest content from disk. With keepPendingEdits, edits that were not saved
        // before a script reload are applied again, on top of quest files that changed on disk
        // since, such as after a git pull, quest by quest.
        public void Load(bool keepPendingEdits)
        {
            try
            {
                session?.Dispose();
                session = null;
                workspace = loadWorkspace();
                session = new ContentEditSession<QuestEditorCatalog>(
                    undoState,
                    () => workspace.Catalog,
                    catalog => workspace.RestoreContent(catalog),
                    QuestEditorMerge.Merge);
                form = new ContentForm<QuestEditorDefinition>(session, Key);
                detailShape = string.Empty;
                session.Changed += OnContentChanged;
                session.Load(keepPendingEdits);
                nonCanonicalFiles = ReadNonCanonicalFiles();
                var quests = Quests();
                if (list.Selection.Count == 0 && quests.Length > 0)
                {
                    list.Select(new[] { Key(quests[0]) });
                }

                toolLayout.SetStatus(
                    "Loaded " + quests.Length + " quests." + session.PendingEditsMessage,
                    session.PendingEditsGaveWay ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info);
                RunLiveValidation();
            }
            catch (Exception exception)
            {
                workspace = null;
                session?.Dispose();
                session = null;
                nonCanonicalFiles = Array.Empty<string>();
                list.SetItems(Array.Empty<QuestEditorDefinition>());
                ShowDetailHint("Quest content could not be loaded.");
                toolLayout.SetStatus(
                    exception.Message
                        + (undoState.HasPendingEdits ? " Unsaved edits from before the script reload are kept for the next load." : string.Empty),
                    HelpBoxMessageType.Error);
            }

            UnsavedChangesChanged?.Invoke();
        }

        // Loads the files on disk again. Unsaved edits either apply on top of them, quest by
        // quest, or are discarded, as the dialog or chooseKeep decides.
        public void Reload(Func<bool?> chooseKeep = null)
        {
            if (!HasUnsavedChanges)
            {
                Load(false);
                return;
            }

            var keep = chooseKeep != null ? chooseKeep() : AskKeepEdits();
            if (keep.HasValue)
            {
                Load(keep.Value);
            }
        }

        private static bool? AskKeepEdits()
        {
            var choice = EditorUtility.DisplayDialogComplex(
                "Reload Quests",
                "Quest Studio has unsaved changes. Keep them on top of the quest files on disk, where the files did not change the "
                    + "same quests, or discard them?",
                "Keep my edits",
                "Cancel",
                "Discard my edits");
            return choice switch
            {
                0 => true,
                2 => false,
                _ => null
            };
        }

        // Reads the compiled items and actors again and keeps the quest edits.
        public void RefreshReferences()
        {
            if (workspace == null)
            {
                return;
            }

            try
            {
                workspace.RefreshReferences(QuestEditorReferences.Load(service.Paths.ContentRoot, service.Paths.GeneratedRoot));
                detailShape = string.Empty;
                OnSelectionChanged();
                RunLiveValidation();
                toolLayout.SetStatus("Read the compiled items and actors again.", HelpBoxMessageType.Info);
            }
            catch (Exception exception)
            {
                toolLayout.SetStatus("The compiled items and actors could not be read: " + exception.Message, HelpBoxMessageType.Error);
            }
        }

        // Compiles with the quest compiler process, then writes the quest files and the compiled
        // catalog. With confirmChanges, the caller approves the listed changes instead of a dialog.
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
                toolLayout.SetStatus("The write was cancelled. No quest files were changed.", HelpBoxMessageType.Warning);
                return false;
            }

            try
            {
                service.Save(workspace, validation);
                AssetDatabase.Refresh();

                // The save may retire more ids than the edits did, such as after undo across a
                // save.
                workspace.Catalog.retiredQuestIds = validation.RetiredQuestIds;
                session.MarkSaved();
                nonCanonicalFiles = Array.Empty<string>();
                RunLiveValidation();

                // A saved quest's id is permanent from now on.
                detailShape = string.Empty;
                OnSelectionChanged();
                toolLayout.SetStatus("The quest files and the compiled quest catalog were written. " + validation.Message, HelpBoxMessageType.Info);
                return true;
            }
            catch (Exception exception)
            {
                toolLayout.SetStatus("Quest content write failed: " + exception.Message, HelpBoxMessageType.Error);
                return false;
            }
        }

        public void AddQuest(string template)
        {
            if (workspace == null)
            {
                return;
            }

            QuestEditorDefinition quest = null;
            session.Change("New " + QuestEditorTemplates.Label(template), () =>
            {
                quest = QuestEditorTemplates.Create(
                    template,
                    QuestEditorTemplates.UniqueId("quest.new_" + template, workspace.Catalog, workspace.IsReserved),
                    workspace.References);
                workspace.Catalog.quests = ById(Quests().Append(quest));
            });
            list.Select(new[] { Key(quest) });
        }

        public void DuplicateSelected()
        {
            var selection = list.Selection;
            if (workspace == null || selection.Count == 0)
            {
                return;
            }

            var copies = new List<QuestEditorDefinition>();
            session.Change(selection.Count == 1 ? "Duplicate quest" : "Duplicate " + selection.Count + " quests", () =>
            {
                foreach (var source in selection)
                {
                    var copy = QuestEditorJson.Clone(source);
                    copy.id = QuestEditorTemplates.UniqueId(source.id + "_copy", workspace.Catalog, workspace.IsReserved);
                    copy.title = source.title + " Copy";
                    workspace.Catalog.quests = ById(Quests().Append(copy));
                    copies.Add(copy);
                }
            });
            list.Select(copies.Select(Key));
        }

        // Deletes the selected quests that no remaining quest requires. A saved quest leaves its
        // id among the retired ids, so it can never return. With confirm, the caller approves
        // the listed ids instead of a dialog.
        public void DeleteSelected(Func<string[], bool> confirm = null)
        {
            var selection = list.Selection;
            if (workspace == null || selection.Count == 0)
            {
                return;
            }

            var deletable = new HashSet<string>(selection.Select(Key), StringComparer.Ordinal);
            bool kept;
            do
            {
                kept = false;
                foreach (var quest in Quests().Where(quest => !deletable.Contains(Key(quest))))
                {
                    foreach (var required in quest.requiredQuestIds ?? Array.Empty<string>())
                    {
                        kept |= required != null && deletable.Remove(required);
                    }
                }
            }
            while (kept);

            var blocked = selection
                .Where(quest => !deletable.Contains(Key(quest)))
                .Select(quest => quest.id + " is required by " + string.Join(", ", QuestEditorRelations.FollowUps(workspace.Catalog, quest.id)
                    .Where(followUp => !deletable.Contains(Key(followUp)))
                    .Select(followUp => followUp.id)))
                .ToArray();
            if (deletable.Count == 0)
            {
                toolLayout.SetStatus(
                    "Nothing was deleted. Remove the requirement from the follow-up quests first: " + string.Join("; ", blocked) + ".",
                    HelpBoxMessageType.Warning);
                return;
            }

            var ids = selection.Select(Key).Where(deletable.Contains).ToArray();
            var retire = ids.Where(IsSaved).ToArray();
            var approve = confirm ?? (values => EditorUtility.DisplayDialog(
                "Delete Quests",
                "Delete these quests?\n\n" + string.Join("\n", values)
                    + (retire.Length == 0
                        ? string.Empty
                        : "\n\nSaved quests leave their ids as retired ids, which can never be used again. "
                            + "GameService refuses to start while characters have a retired quest active."),
                "Delete",
                "Cancel"));
            if (!approve(ids))
            {
                return;
            }

            session.Change(ids.Length == 1 ? "Delete quest" : "Delete " + ids.Length + " quests", () =>
            {
                workspace.Catalog.quests = Quests().Where(quest => !deletable.Contains(Key(quest))).ToArray();
                workspace.Catalog.retiredQuestIds = Retired(retire);
            });
            toolLayout.SetStatus(
                "Deleted " + ids.Length + (ids.Length == 1 ? " quest." : " quests.")
                    + (retire.Length > 0 ? " Retired ids: " + string.Join(", ", retire) + "." : string.Empty)
                    + (blocked.Length > 0 ? " Kept because other quests require them: " + string.Join("; ", blocked) + "." : string.Empty),
                blocked.Length > 0 ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info);
        }

        // Renames a quest and every requirement that names it, as one undo step. A saved quest's
        // old id becomes a retired id, after a confirmation, or with confirmRetire.
        public bool RenameQuest(string currentId, string newId, Func<string, bool> confirmRetire = null)
        {
            var quest = Quests().FirstOrDefault(entry => entry.id == currentId);
            newId = newId?.Trim() ?? string.Empty;
            if (quest == null || newId.Length == 0 || currentId == newId)
            {
                form?.Refresh();
                return false;
            }

            // A saved quest's id stays its own even while undo took the quest out of the catalog.
            if (workspace.IsReserved(newId))
            {
                toolLayout.SetStatus("The id '" + newId + "' is already used, saved, or retired.", HelpBoxMessageType.Error);
                form?.Refresh();
                return false;
            }

            var retire = IsSaved(currentId);
            var approve = confirmRetire ?? (id => EditorUtility.DisplayDialog(
                "Rename Saved Quest",
                "Quest '" + id + "' is saved, so its id becomes a retired id that can never be used again. "
                    + "GameService refuses to start while characters have a retired quest active.\n\nRename it?",
                "Rename",
                "Cancel"));
            if (retire && !approve(currentId))
            {
                form?.Refresh();
                return false;
            }

            var followUps = QuestEditorRelations.FollowUps(workspace.Catalog, currentId).Count;
            session.Change("Quest id changed", () =>
            {
                quest.id = newId;
                foreach (var entry in Quests())
                {
                    entry.requiredQuestIds = (entry.requiredQuestIds ?? Array.Empty<string>())
                        .Select(id => id == currentId ? newId : id)
                        .ToArray();
                }

                workspace.Catalog.quests = ById(Quests());

                if (retire)
                {
                    workspace.Catalog.retiredQuestIds = Retired(new[] { currentId });
                }
            });
            list.Select(new[] { Key(quest) });
            toolLayout.SetStatus(
                "Renamed the quest"
                    + (followUps == 0 ? string.Empty : " and updated " + followUps + (followUps == 1 ? " quest" : " quests") + " that require it")
                    + (retire ? "; '" + currentId + "' is retired." : "."),
                HelpBoxMessageType.Info);
            return true;
        }

        // Adds the tag to every selected quest.
        public void AddTag(string tag)
        {
            tag = tag?.Trim() ?? string.Empty;
            var targets = form?.Targets ?? Array.Empty<QuestEditorDefinition>();
            if (tag.Length == 0 || targets.Count == 0)
            {
                return;
            }

            session.Change(targets.Count == 1 ? "Tag " + tag + " added" : "Tag " + tag + " added for " + targets.Count + " entries", () =>
            {
                foreach (var quest in targets)
                {
                    quest.tags = (quest.tags ?? Array.Empty<string>())
                        .Append(tag)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();
                }
            });
        }

        public void SetMaximumActiveQuests(int value)
        {
            if (workspace != null)
            {
                session.Change("Maximum active quests changed", () => workspace.Catalog.maximumActiveQuests = value);
            }
        }

        // Shows the quests of the NPC in the NPC view.
        public void ShowNpc(string actorId)
        {
            npcViewActorId = actorId ?? string.Empty;
            form?.Refresh();
        }

        public void AddRequirement()
        {
            var quest = SingleTarget();
            var required = quest?.requiredQuestIds ?? Array.Empty<string>();
            var next = RequirementOptions(quest).Select(option => option.Value).FirstOrDefault(id => !required.Contains(id));
            if (quest == null || next == null || required.Length >= QuestContentRules.MaximumRequiredQuests)
            {
                return;
            }

            session.Change("Requirement added", () => quest.requiredQuestIds = required.Append(next).ToArray());
        }

        public void RemoveRequirement(int position)
        {
            var quest = SingleTarget();
            if (quest?.requiredQuestIds == null || position < 0 || position >= quest.requiredQuestIds.Length)
            {
                return;
            }

            session.Change("Requirement removed", () => quest.requiredQuestIds = Without(quest.requiredQuestIds, position));
        }

        public void AddItem(string field)
        {
            var quest = SingleTarget();
            var items = ItemList(quest, field);
            if (items == null || items.Length >= ItemLimit(field))
            {
                return;
            }

            var choices = ItemOptions(field).Select(option => option.Value).ToArray();
            var definitionId = choices.FirstOrDefault(id => items.All(item => item?.definitionId != id))
                ?? choices.FirstOrDefault()
                ?? string.Empty;
            session.Change(ItemListLabel(field) + " added", () => SetItemList(
                quest,
                field,
                items.Append(new QuestEditorItemQuantity { definitionId = definitionId, quantity = 1 }).ToArray()));
        }

        public void RemoveItem(string field, int position)
        {
            var quest = SingleTarget();
            var items = ItemList(quest, field);
            if (items == null || position < 0 || position >= items.Length)
            {
                return;
            }

            session.Change(ItemListLabel(field) + " removed", () => SetItemList(quest, field, Without(items, position)));
        }

        public void AddStep()
        {
            var quest = SingleTarget();
            var steps = quest?.steps ?? Array.Empty<QuestEditorStep>();
            if (quest == null || steps.Length >= QuestContentRules.MaximumSteps)
            {
                return;
            }

            session.Change("Step added", () => quest.steps = steps
                .Append(QuestEditorTemplates.Step(UniqueStepId(quest), NewObjective(quest)))
                .ToArray());
        }

        public void RemoveStep(int position)
        {
            var quest = SingleTarget();
            if (quest?.steps == null || position < 0 || position >= quest.steps.Length || quest.steps.Length == 1)
            {
                return;
            }

            session.Change("Step removed", () => quest.steps = Without(quest.steps, position));
        }

        public void MoveStep(int position, int offset)
        {
            var quest = SingleTarget();
            if (quest?.steps != null && Swap(quest.steps, position, offset, out var steps))
            {
                session.Change(offset < 0 ? "Step moved up" : "Step moved down", () => quest.steps = steps);
            }
        }

        public void AddObjective(int step)
        {
            var quest = SingleTarget();
            var entry = StepAt(quest, step);
            var objectives = entry?.objectives ?? Array.Empty<QuestEditorObjective>();
            if (entry == null || objectives.Length >= QuestContentRules.MaximumObjectivesPerStep)
            {
                return;
            }

            session.Change("Objective added", () => entry.objectives = objectives.Append(NewObjective(quest)).ToArray());
        }

        public void RemoveObjective(int step, int position)
        {
            var entry = StepAt(SingleTarget(), step);
            if (entry?.objectives == null || position < 0 || position >= entry.objectives.Length || entry.objectives.Length == 1)
            {
                return;
            }

            session.Change("Objective removed", () => entry.objectives = Without(entry.objectives, position));
        }

        public void MoveObjective(int step, int position, int offset)
        {
            var entry = StepAt(SingleTarget(), step);
            if (entry?.objectives != null && Swap(entry.objectives, position, offset, out var objectives))
            {
                session.Change(offset < 0 ? "Objective moved up" : "Objective moved down", () => entry.objectives = objectives);
            }
        }

        // Adds a Mob to a kill objective or a resource node to a gathering objective.
        public void AddTarget(int step, int position)
        {
            var objective = ObjectiveAt(SingleTarget(), step, position);
            var targets = objective?.actorIds ?? Array.Empty<string>();
            if (objective == null || targets.Length >= QuestContentRules.MaximumObjectiveTargets)
            {
                return;
            }

            var choices = TargetOptions(objective.kind).Select(option => option.Value).ToArray();
            var actorId = choices.FirstOrDefault(id => !targets.Contains(id)) ?? choices.FirstOrDefault() ?? string.Empty;
            session.Change("Target added", () => objective.actorIds = targets.Append(actorId).ToArray());
        }

        public void RemoveTarget(int step, int position, int target)
        {
            var objective = ObjectiveAt(SingleTarget(), step, position);
            if (objective?.actorIds == null || target < 0 || target >= objective.actorIds.Length)
            {
                return;
            }

            session.Change("Target removed", () => objective.actorIds = Without(objective.actorIds, target));
        }

        // A Mob that drops a collect objective's quest item for the quest.
        public void AddDropSource(int step, int position)
        {
            var objective = ObjectiveAt(SingleTarget(), step, position);
            var sources = objective?.dropSources ?? Array.Empty<QuestEditorDropSource>();
            if (objective == null || sources.Length >= QuestContentRules.MaximumDropSources)
            {
                return;
            }

            var mobs = MobOptions().Select(option => option.Value).ToArray();
            var actorId = mobs.FirstOrDefault(id => sources.All(source => source?.actorId != id)) ?? mobs.FirstOrDefault() ?? string.Empty;
            session.Change("Quest drop added", () => objective.dropSources = sources
                .Append(new QuestEditorDropSource { actorId = actorId, chanceBasisPoints = 5000, quantity = 1 })
                .ToArray());
        }

        public void RemoveDropSource(int step, int position, int source)
        {
            var objective = ObjectiveAt(SingleTarget(), step, position);
            if (objective?.dropSources == null || source < 0 || source >= objective.dropSources.Length)
            {
                return;
            }

            session.Change("Quest drop removed", () => objective.dropSources = Without(objective.dropSources, source));
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
                ContentChangePreviewWindow.ShowPreview(
                    "Quest Canonical Change Preview",
                    validation.Changes,
                    validation.RemovedFiles,
                    validation.Files.Select(file => new ContentChangePreviewFile(file.DisplayPath, file.Text, file.Changed)));
            }
        }

        private void VerifyCanonical()
        {
            var result = service.VerifyCanonical();
            toolLayout.SetStatus(result.Output, result.Success ? HelpBoxMessageType.Info : HelpBoxMessageType.Error);
        }

        private void OnContentChanged()
        {
            list.SetItems(Quests());
            UnsavedChangesChanged?.Invoke();
            liveValidation.ExecuteLater(LiveValidationDelayMilliseconds);
        }

        // Validates the catalog as a save would write it, so a retired id that undo brought back
        // shows as a problem at once, together with the quest files that are not in the form a
        // save writes.
        private void RunLiveValidation()
        {
            issues = QuestEditorIssues.Collect(SavedForm(), workspace?.References)
                .Concat(nonCanonicalFiles.Select(NonCanonicalIssue))
                .ToArray();
            list.SetIssueState(IssueState);
            toolLayout.Issues.SetIssues(issues, issue => QuestEditorIssues.Describe(issue, workspace?.Catalog));
            form?.ApplyIssues(issues);
            MarkCatalogFields();
        }

        // The catalog settings belong to no quest, so the form leaves their findings to this.
        private void MarkCatalogFields()
        {
            if (maximumActiveQuestsField == null)
            {
                return;
            }

            var related = issues
                .Where(issue => issue.EntityKey.Length == 0 && issue.Concerns(QuestEditorIssues.MaximumActiveQuests))
                .ToArray();
            maximumActiveQuestsField.EnableInClassList(ContentToolStyles.FieldError, related.Length > 0);
            maximumActiveQuestsField.tooltip = related.Length == 0
                ? MaximumActiveQuestsTooltip
                : string.Join("\n", related.Select(issue => issue.Message));
        }

        // A definition file names its quest; the catalog file belongs to the whole catalog.
        private static ContentIssue NonCanonicalIssue(string file)
        {
            var prefix = ContentLayout.DefinitionsDirectoryName + "/";
            var questId = file.StartsWith(prefix, StringComparison.Ordinal) && file.EndsWith(".json", StringComparison.Ordinal)
                ? file.Substring(prefix.Length, file.Length - prefix.Length - ".json".Length)
                : string.Empty;
            return new ContentIssue(
                questId,
                string.Empty,
                file + " is not in the form Quest Studio writes, for example because of a field it does not know, which the quest "
                    + "compiler rejects. Saving writes it in that form.",
                ContentIssueSeverity.Warning);
        }

        private IReadOnlyList<string> ReadNonCanonicalFiles()
        {
            try
            {
                return service.NonCanonicalFiles();
            }
            catch (Exception exception) when (exception is IOException
                || exception is InvalidDataException
                || exception is UnauthorizedAccessException)
            {
                // The load itself reports unreadable files.
                return Array.Empty<string>();
            }
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
            MarkCatalogFields();
            duplicateButton.SetEnabled(selection.Count > 0);
            deleteButton.SetEnabled(selection.Count > 0);
        }

        // The detail panel is rebuilt only when its layout changes, so typing keeps focus.
        private string DetailShape(IReadOnlyList<QuestEditorDefinition> selection)
        {
            if (selection.Count != 1)
            {
                return selection.Count == 0 ? "none" : "many";
            }

            var quest = selection[0];
            var steps = (quest.steps ?? Array.Empty<QuestEditorStep>())
                .Select(step => string.Join(",", (step?.objectives ?? Array.Empty<QuestEditorObjective>()).Select(ObjectiveShape)));
            return "one:" + quest.id
                + ":" + IsSaved(quest.id)
                + ":" + Count(quest.requiredQuestIds)
                + ":" + Count(quest.startItems)
                + ":" + Count(quest.rewardItems)
                + ":" + Count(quest.rewardChoiceItems)
                + ":" + string.Join("|", steps);
        }

        private string ObjectiveShape(QuestEditorObjective objective)
        {
            return objective == null
                ? "none"
                : objective.kind + "/" + KillMode(objective) + "/" + Count(objective.actorIds) + "/" + Count(objective.dropSources) + "/" + ShowsDrops(objective);
        }

        private void BuildDetail(IReadOnlyList<QuestEditorDefinition> selection)
        {
            form.Clear();
            toolLayout.Detail.Clear();
            if (selection.Count == 0)
            {
                toolLayout.Detail.Add(ContentToolStyles.CreateHint(
                    "Select one or more quests, or create one with New Quest. Hold Ctrl or Shift to select several."));
                toolLayout.Detail.Add(BuildCatalogSection());
                toolLayout.Detail.Add(BuildNpcSection());
                form.SetTargets(selection);
                return;
            }

            var single = selection.Count == 1;
            var section = ContentToolStyles.CreateSection("Quest");
            if (single)
            {
                AddIdentityFields(section);
            }
            else
            {
                section.Add(ContentToolStyles.CreateHint(
                    selection.Count + " quests selected. Changes apply to every selected quest. "
                        + "Id, title, texts, requirements, items, and steps are edited one quest at a time."));
            }

            section.Add(form.Choice("Giver", "giverActorId", QuestNpcOptions, quest => quest.giverActorId, (quest, value) => quest.giverActorId = value));
            section.Add(form.Choice("Turn-In NPC", "turnInActorId", QuestNpcOptions, quest => quest.turnInActorId, (quest, value) => quest.turnInActorId = value));
            var level = form.Integer("Minimum Level", "minimumLevel", quest => quest.minimumLevel, (quest, value) => quest.minimumLevel = value);
            level.tooltip = "Between 1 and the character level cap of the item catalog.";
            section.Add(level);
            var repeatable = form.Toggle("Repeatable", "repeatable", quest => quest.repeatable, (quest, value) => quest.repeatable = value);
            repeatable.tooltip = "A repeatable quest can be accepted again after its turn-in.";
            section.Add(repeatable);
            toolLayout.Detail.Add(section);
            if (single)
            {
                toolLayout.Detail.Add(BuildDialogSection());
                toolLayout.Detail.Add(BuildRequirementsSection(selection[0]));
            }

            toolLayout.Detail.Add(BuildTagSection());
            if (single)
            {
                toolLayout.Detail.Add(BuildItemList(
                    "Start Items",
                    StartItems,
                    selection[0],
                    "Protected quest items granted at acceptance and removed again when the quest is abandoned."));
                toolLayout.Detail.Add(BuildStepsSection(selection[0]));
            }

            toolLayout.Detail.Add(BuildRewardsSection(single ? selection[0] : null));
            if (single)
            {
                toolLayout.Detail.Add(BuildChainSection());
                toolLayout.Detail.Add(BuildNpcSection());
            }

            form.SetTargets(selection);
        }

        private void AddIdentityFields(VisualElement section)
        {
            var idField = new TextField("Stable Quest Id")
            {
                isDelayed = true,
                tooltip = "Renaming also updates the quests that require this one. A saved quest's old id is retired."
            };
            idField.RegisterValueChangedCallback(change => RenameQuest(change.previousValue, change.newValue));
            form.Watch(() =>
            {
                if (form.Targets.Count == 1)
                {
                    idField.SetValueWithoutNotify(form.Targets[0].id ?? string.Empty);
                }
            });
            form.Register("id", idField);
            section.Add(idField);
            var title = form.Text("Title", "title", quest => quest.title, (quest, value) => quest.title = OneLine(value));
            title.tooltip = "One line of up to " + QuestContentRules.MaximumTitleLength + " characters.";
            section.Add(title);
        }

        private VisualElement BuildDialogSection()
        {
            var section = ContentToolStyles.CreateSection("Dialog");
            section.Add(TextArea(
                form.Text("Offer Text", "offerText", quest => quest.offerText, (quest, value) => quest.offerText = OneLine(value)),
                "What the giver says to offer the quest, up to " + QuestContentRules.MaximumOfferTextLength + " characters."));
            section.Add(TextArea(
                form.Text("Progress Text", "progressText", quest => quest.progressText, (quest, value) => quest.progressText = OneLine(value)),
                "What the turn-in NPC says while the quest is not ready, up to " + QuestContentRules.MaximumProgressTextLength + " characters."));
            section.Add(TextArea(
                form.Text("Completion Text", "completionText", quest => quest.completionText, (quest, value) => quest.completionText = OneLine(value)),
                "What the turn-in NPC says at the turn-in, up to " + QuestContentRules.MaximumCompletionTextLength + " characters."));
            return section;
        }

        private VisualElement BuildRequirementsSection(QuestEditorDefinition quest)
        {
            var section = ContentToolStyles.CreateSection("Required Quests");
            form.Register("requiredQuestIds", section);
            var required = quest.requiredQuestIds ?? Array.Empty<string>();
            if (required.Length == 0)
            {
                section.Add(ContentToolStyles.CreateHint("Available from the start. Add a requirement to make this quest a follow-up."));
            }

            for (var index = 0; index < required.Length; index++)
            {
                var position = index;
                var row = Row();
                var choice = form.Choice(
                    "Requires",
                    "requiredQuestIds[" + position + "]",
                    () => RequirementOptions(SingleTarget()),
                    entry => At(entry.requiredQuestIds, position),
                    (entry, value) => SetAt(entry.requiredQuestIds, position, value));
                choice.style.flexGrow = 1f;
                row.Add(choice);
                row.Add(FixedButton("Remove", () => RemoveRequirement(position)));
                section.Add(row);
            }

            var add = new Button(AddRequirement) { text = "Add Requirement" };
            add.SetEnabled(required.Length < QuestContentRules.MaximumRequiredQuests
                && RequirementOptions(quest).Any(option => !required.Contains(option.Value)));
            section.Add(add);
            return section;
        }

        private VisualElement BuildTagSection()
        {
            var section = ContentToolStyles.CreateSection("Content Tags");
            section.Add(form.Flags(
                "Tags",
                "tags",
                TagOptions,
                quest => quest.tags,
                (quest, value) => quest.tags = value,
                "No quest uses a tag yet."));
            var add = new TextField("Add Tag") { isDelayed = true, tooltip = "Adds a tag to every selected quest, such as a zone or a story arc." };
            add.RegisterValueChangedCallback(change =>
            {
                add.SetValueWithoutNotify(string.Empty);
                AddTag(change.newValue);
            });
            section.Add(add);
            return section;
        }

        private VisualElement BuildItemList(string title, string field, QuestEditorDefinition quest, string hint)
        {
            var section = ContentToolStyles.CreateSection(title);
            form.Register(field, section);
            section.Add(ContentToolStyles.CreateHint(hint));
            var items = ItemList(quest, field) ?? Array.Empty<QuestEditorItemQuantity>();
            for (var index = 0; index < items.Length; index++)
            {
                var position = index;
                var path = field + "[" + position + "]";
                var box = Box();
                form.Register(path, box);
                box.Add(form.Choice(
                    "Item",
                    path + ".definitionId",
                    () => ItemOptions(field),
                    entry => At(ItemList(entry, field), position)?.definitionId,
                    (entry, value) => EditItem(entry, field, position, item => item.definitionId = value)));
                var quantity = form.Integer(
                    "Quantity",
                    path + ".quantity",
                    entry => At(ItemList(entry, field), position)?.quantity ?? 0,
                    (entry, value) => EditItem(entry, field, position, item => item.quantity = value));
                quantity.tooltip = "Between 1 and the item's stack size.";
                box.Add(quantity);
                box.Add(new Button(() => RemoveItem(field, position)) { text = "Remove" });
                section.Add(box);
            }

            var add = new Button(() => AddItem(field)) { text = "Add " + ItemListLabel(field) };
            add.SetEnabled(items.Length < ItemLimit(field));
            section.Add(add);
            return section;
        }

        private VisualElement BuildStepsSection(QuestEditorDefinition quest)
        {
            var section = ContentToolStyles.CreateSection("Steps");
            form.Register("steps", section);
            section.Add(ContentToolStyles.CreateHint(
                "Steps run in order, and a step is done when all of its objectives are. Collect objectives belong to the last step, "
                    + "whose turn-in takes the items. Changing the steps, or an objective's id, kind, or targets, of a quest that "
                    + "characters have active needs a data migration, or GameService refuses to start."));
            var steps = quest.steps ?? Array.Empty<QuestEditorStep>();
            for (var index = 0; index < steps.Length; index++)
            {
                section.Add(BuildStep(quest, index, steps.Length));
            }

            var add = new Button(AddStep) { text = "Add Step" };
            add.SetEnabled(steps.Length < QuestContentRules.MaximumSteps);
            section.Add(add);
            return section;
        }

        private VisualElement BuildStep(QuestEditorDefinition quest, int position, int count)
        {
            var path = "steps[" + position + "]";
            var box = Box("Step " + (position + 1));
            form.Register(path, box);
            box.Add(form.Text("Step Id", path + ".id", entry => StepAt(entry, position)?.id, (entry, value) => EditStep(entry, position, step => step.id = value)));
            var objectives = StepAt(quest, position)?.objectives ?? Array.Empty<QuestEditorObjective>();
            for (var index = 0; index < objectives.Length; index++)
            {
                box.Add(BuildObjective(quest, position, index, objectives.Length));
            }

            var buttons = Row();
            var add = new Button(() => AddObjective(position)) { text = "Add Objective" };
            add.SetEnabled(objectives.Length < QuestContentRules.MaximumObjectivesPerStep);
            buttons.Add(add);
            buttons.Add(EnabledButton("Move Step Up", position > 0, () => MoveStep(position, -1)));
            buttons.Add(EnabledButton("Move Step Down", position < count - 1, () => MoveStep(position, 1)));
            buttons.Add(EnabledButton("Remove Step", count > 1, () => RemoveStep(position)));
            box.Add(buttons);
            return box;
        }

        private VisualElement BuildObjective(QuestEditorDefinition quest, int step, int position, int count)
        {
            var objective = ObjectiveAt(quest, step, position);
            var path = ObjectivePath(step, position);
            var box = Box("Objective " + (position + 1));
            form.Register(path, box);
            box.Add(form.Text("Objective Id", path + ".id", entry => ObjectiveAt(entry, step, position)?.id, (entry, value) => EditObjective(entry, step, position, target => target.id = value)));
            box.Add(form.Choice(
                "Kind",
                path + ".kind",
                () => ObjectiveKinds,
                entry => ObjectiveAt(entry, step, position)?.kind,
                (entry, value) => EditObjective(entry, step, position, target => SetObjectiveKind(entry, target, value))));
            var text = form.Text("Text", path + ".text", entry => ObjectiveAt(entry, step, position)?.text, (entry, value) => EditObjective(entry, step, position, target => target.text = OneLine(value)));
            text.tooltip = "The objective's line in the tracker and the quest log, shown with its count, such as Wolf Pelts. Up to "
                + QuestContentRules.MaximumObjectiveTextLength + " characters.";
            box.Add(text);
            var amount = form.Integer(
                "Count",
                path + ".count",
                entry => ObjectiveAt(entry, step, position)?.count ?? 0,
                (entry, value) => EditObjective(entry, step, position, target => target.count = value),
                entry => ObjectiveAt(entry, step, position)?.kind != QuestObjectiveKindIds.TalkTo);
            amount.tooltip = "Between 1 and " + QuestContentRules.MaximumObjectiveCount + "; a conversation counts once.";
            box.Add(amount);
            switch (objective?.kind)
            {
                case QuestObjectiveKindIds.Kill:
                    box.Add(form.Choice(
                        "Targets",
                        path + ".targets",
                        () => KillTargets,
                        entry => KillMode(ObjectiveAt(entry, step, position)),
                        (entry, value) => EditObjective(entry, step, position, target => SetKillTargets(target, value))));
                    if (KillMode(objective) == MobTag)
                    {
                        var tag = form.Choice("Mob Tag", path + ".actorTag", MobTagOptions, entry => ObjectiveAt(entry, step, position)?.actorTag, (entry, value) => EditObjective(entry, step, position, target => target.actorTag = value));
                        tag.tooltip = "Every placed Mob with this tag counts.";
                        box.Add(tag);
                    }
                    else
                    {
                        box.Add(BuildTargets(objective, step, position, "Mob", "Add Mob"));
                    }

                    break;
                case QuestObjectiveKindIds.Collect:
                    box.Add(form.Choice("Item", path + ".itemId", () => ObjectiveItemOptions(SingleTarget()), entry => ObjectiveAt(entry, step, position)?.itemId, (entry, value) => EditObjective(entry, step, position, target => target.itemId = value)));
                    if (ShowsDrops(objective))
                    {
                        box.Add(BuildDropSources(objective, step, position));
                    }

                    break;
                case QuestObjectiveKindIds.Deliver:
                    box.Add(form.Choice("Item", path + ".itemId", () => ObjectiveItemOptions(SingleTarget()), entry => ObjectiveAt(entry, step, position)?.itemId, (entry, value) => EditObjective(entry, step, position, target => target.itemId = value)));
                    box.Add(form.Choice("Deliver To", path + ".npcActorId", QuestNpcOptions, entry => ObjectiveAt(entry, step, position)?.npcActorId, (entry, value) => EditObjective(entry, step, position, target => target.npcActorId = value)));
                    break;
                case QuestObjectiveKindIds.TalkTo:
                    box.Add(form.Choice("Talk To", path + ".npcActorId", QuestNpcOptions, entry => ObjectiveAt(entry, step, position)?.npcActorId, (entry, value) => EditObjective(entry, step, position, target => target.npcActorId = value)));
                    box.Add(TextArea(
                        form.Text("Line", path + ".line", entry => ObjectiveAt(entry, step, position)?.line, (entry, value) => EditObjective(entry, step, position, target => target.line = OneLine(value))),
                        "What the NPC says, up to " + QuestContentRules.MaximumLineLength + " characters."));
                    break;
                case QuestObjectiveKindIds.Gather:
                    box.Add(BuildTargets(objective, step, position, "Resource Node", "Add Resource Node"));
                    break;
            }

            var buttons = Row();
            buttons.Add(EnabledButton("Move Up", position > 0, () => MoveObjective(step, position, -1)));
            buttons.Add(EnabledButton("Move Down", position < count - 1, () => MoveObjective(step, position, 1)));
            buttons.Add(EnabledButton("Remove", count > 1, () => RemoveObjective(step, position)));
            box.Add(buttons);
            return box;
        }

        // The Mobs or resource nodes an objective counts, one dropdown each.
        private VisualElement BuildTargets(QuestEditorObjective objective, int step, int position, string label, string addText)
        {
            var path = ObjectivePath(step, position) + ".actorIds";
            var container = new VisualElement();
            form.Register(path, container);
            var targets = objective.actorIds ?? Array.Empty<string>();
            for (var index = 0; index < targets.Length; index++)
            {
                var target = index;
                var row = Row();
                var choice = form.Choice(
                    label,
                    path + "[" + target + "]",
                    () => TargetOptions(ObjectiveAt(SingleTarget(), step, position)?.kind),
                    entry => At(ObjectiveAt(entry, step, position)?.actorIds, target),
                    (entry, value) => SetAt(ObjectiveAt(entry, step, position)?.actorIds, target, value));
                choice.style.flexGrow = 1f;
                row.Add(choice);
                row.Add(FixedButton("Remove", () => RemoveTarget(step, position, target)));
                container.Add(row);
            }

            var add = new Button(() => AddTarget(step, position)) { text = addText };
            add.SetEnabled(targets.Length < QuestContentRules.MaximumObjectiveTargets);
            container.Add(add);
            return container;
        }

        private VisualElement BuildDropSources(QuestEditorObjective objective, int step, int position)
        {
            var path = ObjectivePath(step, position) + ".dropSources";
            var container = Box("Quest Drops");
            form.Register(path, container);
            container.Add(ContentToolStyles.CreateHint(
                "Mobs that drop the quest item into their corpse for every player who has the quest, up to what it still needs."));
            var sources = objective.dropSources ?? Array.Empty<QuestEditorDropSource>();
            for (var index = 0; index < sources.Length; index++)
            {
                var source = index;
                var sourcePath = path + "[" + source + "]";
                var box = Box();
                form.Register(sourcePath, box);
                box.Add(form.Choice("Mob", sourcePath + ".actorId", MobOptions, entry => At(ObjectiveAt(entry, step, position)?.dropSources, source)?.actorId, (entry, value) => EditDropSource(entry, step, position, source, drop => drop.actorId = value)));
                var chance = form.Integer("Chance (basis points)", sourcePath + ".chanceBasisPoints", entry => At(ObjectiveAt(entry, step, position)?.dropSources, source)?.chanceBasisPoints ?? 0, (entry, value) => EditDropSource(entry, step, position, source, drop => drop.chanceBasisPoints = value));
                chance.tooltip = "Between 1 and " + QuestContentRules.MaximumDropChanceBasisPoints + ": 10000 always drops, 5000 drops half of the time.";
                box.Add(chance);
                var quantity = form.Integer("Quantity", sourcePath + ".quantity", entry => At(ObjectiveAt(entry, step, position)?.dropSources, source)?.quantity ?? 0, (entry, value) => EditDropSource(entry, step, position, source, drop => drop.quantity = value));
                quantity.tooltip = "Between 1 and the item's stack size.";
                box.Add(quantity);
                box.Add(new Button(() => RemoveDropSource(step, position, source)) { text = "Remove" });
                container.Add(box);
            }

            var add = new Button(() => AddDropSource(step, position)) { text = "Add Quest Drop" };
            add.SetEnabled(sources.Length < QuestContentRules.MaximumDropSources);
            container.Add(add);
            return container;
        }

        private VisualElement BuildRewardsSection(QuestEditorDefinition quest)
        {
            var section = ContentToolStyles.CreateSection("Rewards");
            var gold = form.Long("Reward Gold", "rewardGold", entry => entry.rewardGold, (entry, value) => entry.rewardGold = value);
            gold.tooltip = "Between 0 and " + QuestContentRules.MaximumRewardGold + ".";
            section.Add(gold);
            if (quest != null)
            {
                section.Add(BuildItemList("Reward Items", RewardItems, quest, "Ordinary items that the player gets at the turn-in."));
                section.Add(BuildItemList(
                    "Reward Choice",
                    RewardChoiceItems,
                    quest,
                    "None, or " + QuestContentRules.MinimumRewardChoices + " to " + QuestContentRules.MaximumRewardChoices
                        + " ordinary items, of which the player picks one at the turn-in."));
            }

            return section;
        }

        // The selected quest's chain in layers; each layer needs quests from the layers above.
        private VisualElement BuildChainSection()
        {
            var section = ContentToolStyles.CreateSection("Quest Chain");
            section.Add(ContentToolStyles.CreateHint("Each row needs quests from the rows above it. Click a quest to select it."));
            var graph = new VisualElement();
            section.Add(graph);
            form.Watch(() =>
            {
                graph.Clear();
                var quest = SingleTarget();
                var layers = quest == null
                    ? Array.Empty<IReadOnlyList<QuestEditorDefinition>>()
                    : QuestEditorRelations.Chain(workspace.Catalog, quest.id);
                if (layers.Sum(layer => layer.Count) <= 1)
                {
                    graph.Add(ContentToolStyles.CreateHint("This quest neither requires nor unlocks another quest."));
                    return;
                }

                for (var index = 0; index < layers.Count; index++)
                {
                    if (index > 0)
                    {
                        var arrow = new Label("↓");
                        arrow.AddToClassList("content-chain-arrow");
                        graph.Add(arrow);
                    }

                    var row = new VisualElement();
                    row.AddToClassList("content-chain-layer");
                    foreach (var member in layers[index])
                    {
                        row.Add(QuestNode(member, ReferenceEquals(member, quest)));
                    }

                    graph.Add(row);
                }
            });
            return section;
        }

        // Every quest that an NPC gives, receives, or appears in.
        private VisualElement BuildNpcSection()
        {
            var section = ContentToolStyles.CreateSection("NPC View");
            var values = new List<string>();
            var npc = new DropdownField("NPC") { tooltip = "Every quest this NPC gives, receives, or appears in. Click a quest to select it." };
            var groups = new VisualElement();
            section.Add(npc);
            section.Add(groups);
            Action refresh = () =>
            {
                var options = NpcViewOptions().ToArray();
                values.Clear();
                values.AddRange(options.Select(option => option.Value));
                if (!values.Contains(npcViewActorId))
                {
                    var giver = SingleTarget()?.giverActorId;
                    npcViewActorId = values.Contains(giver) ? giver : values.FirstOrDefault() ?? string.Empty;
                }

                npc.choices = options.Select(option => option.Label).ToList();
                var selected = values.IndexOf(npcViewActorId);
                npc.SetValueWithoutNotify(selected >= 0 ? npc.choices[selected] : string.Empty);
                groups.Clear();
                var quests = QuestEditorRelations.ForNpc(workspace?.Catalog, npcViewActorId);
                groups.Add(QuestGroup("Gives", quests.Gives));
                groups.Add(QuestGroup("Receives", quests.Receives));
                groups.Add(QuestGroup("Appears In", quests.AppearsIn));
            };
            npc.RegisterValueChangedCallback(_ =>
            {
                if (npc.index >= 0 && npc.index < values.Count)
                {
                    npcViewActorId = values[npc.index];
                    refresh();
                }
            });
            form.Watch(refresh);
            return section;
        }

        private VisualElement BuildCatalogSection()
        {
            var section = ContentToolStyles.CreateSection("Quest Catalog");
            var limit = new IntegerField("Maximum Active Quests")
            {
                isDelayed = true,
                tooltip = MaximumActiveQuestsTooltip
            };
            maximumActiveQuestsField = limit;
            limit.RegisterValueChangedCallback(change => SetMaximumActiveQuests(change.newValue));
            var retired = ContentToolStyles.CreateHint(string.Empty);
            form.Watch(() =>
            {
                limit.SetValueWithoutNotify(workspace?.Catalog.maximumActiveQuests ?? 0);
                var ids = workspace?.Catalog.retiredQuestIds ?? Array.Empty<string>();
                retired.text = ids.Length == 0
                    ? "No quest id is retired."
                    : "Retired quest ids, which can never be used again: " + string.Join(", ", ids) + ".";
            });
            section.Add(limit);
            section.Add(retired);
            return section;
        }

        private VisualElement QuestGroup(string title, IReadOnlyList<QuestEditorDefinition> quests)
        {
            var group = new VisualElement();
            var label = new Label(title + " (" + quests.Count + ")");
            label.AddToClassList(ContentToolStyles.SectionTitle);
            group.Add(label);
            if (quests.Count == 0)
            {
                group.Add(ContentToolStyles.CreateHint("None."));
                return group;
            }

            var row = new VisualElement();
            row.AddToClassList("content-chain-layer");
            var selected = SingleTarget();
            foreach (var quest in quests)
            {
                row.Add(QuestNode(quest, ReferenceEquals(quest, selected)));
            }

            group.Add(row);
            return group;
        }

        // A quest in the chain or NPC view, which selects the quest when clicked.
        private Button QuestNode(QuestEditorDefinition quest, bool selected)
        {
            var id = Key(quest);
            var required = (quest.requiredQuestIds ?? Array.Empty<string>()).Where(value => !string.IsNullOrEmpty(value)).ToArray();
            var node = new Button(() => list.Select(new[] { id }))
            {
                text = (string.IsNullOrEmpty(quest.title) ? id : quest.title) + "\n" + NpcSummary(quest),
                tooltip = id + (required.Length == 0 ? string.Empty : "\nRequires " + string.Join(", ", required)),
                userData = id
            };
            node.AddToClassList(ChainNodeClass);
            node.EnableInClassList(ChainNodeClass + "--selected", selected);
            node.EnableInClassList(ChainNodeClass + "--problem", IssueState(quest) == ContentIssueSeverity.Error);
            return node;
        }

        // Switching kind clears the fields the new kind must leave empty and fills the ones it
        // needs, so the objective stays valid where it can.
        private void SetObjectiveKind(QuestEditorDefinition quest, QuestEditorObjective objective, string kind)
        {
            if (objective.kind == kind)
            {
                return;
            }

            var item = objective.itemId ?? string.Empty;
            var npc = objective.npcActorId ?? string.Empty;
            objective.kind = kind;
            objective.actorIds = Array.Empty<string>();
            objective.actorTag = string.Empty;
            objective.itemId = string.Empty;
            objective.npcActorId = string.Empty;
            objective.line = string.Empty;
            objective.dropSources = Array.Empty<QuestEditorDropSource>();
            objective.count = Math.Max(1, objective.count);
            switch (kind)
            {
                case QuestObjectiveKindIds.Kill:
                    objective.actorIds = FirstOf(MobOptions());
                    break;
                case QuestObjectiveKindIds.Collect:
                    objective.itemId = item.Length > 0 ? item : FirstValue(ObjectiveItemOptions(quest));
                    break;
                case QuestObjectiveKindIds.Deliver:
                    objective.itemId = item.Length > 0 ? item : FirstValue(ItemOptions(StartItems, quest));
                    objective.npcActorId = npc.Length > 0 ? npc : OtherNpc(quest);
                    break;
                case QuestObjectiveKindIds.TalkTo:
                    objective.npcActorId = npc.Length > 0 ? npc : OtherNpc(quest);
                    objective.line = "Well met.";
                    objective.count = 1;
                    break;
                case QuestObjectiveKindIds.Gather:
                    objective.actorIds = FirstOf(ResourceNodeOptions());
                    break;
            }
        }

        private void SetKillTargets(QuestEditorObjective objective, string mode)
        {
            if (KillMode(objective) == mode)
            {
                return;
            }

            if (mode == MobTag)
            {
                objective.actorIds = Array.Empty<string>();
                objective.actorTag = FirstValue(MobTagOptions());
            }
            else
            {
                objective.actorTag = string.Empty;
                objective.actorIds = FirstOf(MobOptions());
            }
        }

        // A kill objective, which fits any step. Objective ids are unique in the whole quest.
        private QuestEditorObjective NewObjective(QuestEditorDefinition quest)
        {
            var id = Numbered("objective_", quest.Objectives().Select(objective => objective.id));
            return QuestEditorTemplates.Objective(id, QuestObjectiveKindIds.Kill, "Slain", 1, actorIds: FirstOf(MobOptions()));
        }

        private static string UniqueStepId(QuestEditorDefinition quest)
        {
            return Numbered("step_", (quest.steps ?? Array.Empty<QuestEditorStep>()).Select(step => step?.id));
        }

        // The prefix with the first number from the count of used ids up that no id uses.
        private static string Numbered(string prefix, IEnumerable<string> ids)
        {
            var used = new HashSet<string>(ids.Where(id => id != null), StringComparer.Ordinal);
            for (var number = used.Count + 1; ; number++)
            {
                if (!used.Contains(prefix + number))
                {
                    return prefix + number;
                }
            }
        }

        // An NPC for an objective: one other than the turn-in NPC, whom the last step may not name.
        private string OtherNpc(QuestEditorDefinition quest)
        {
            var npcs = QuestNpcOptions().Select(option => option.Value).ToArray();
            return npcs.FirstOrDefault(id => id != quest.turnInActorId) ?? npcs.FirstOrDefault() ?? string.Empty;
        }

        // The retired ids with the new ones appended in order, as a save appends them.
        private string[] Retired(IEnumerable<string> ids)
        {
            var retired = workspace.Catalog.retiredQuestIds ?? Array.Empty<string>();
            return retired
                .Concat(ids
                    .Where(id => !retired.Contains(id, StringComparer.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal))
                .ToArray();
        }

        // A quest that had a definition file when the workspace last read or wrote the files,
        // whose id characters and the services may already know.
        private bool IsSaved(string questId)
        {
            return !string.IsNullOrEmpty(questId)
                && workspace != null
                && workspace.SavedQuestIds.Contains(questId, StringComparer.Ordinal);
        }

        private bool ShowsDrops(QuestEditorObjective objective)
        {
            return objective?.kind == QuestObjectiveKindIds.Collect
                && (Count(objective.dropSources) > 0
                    || workspace?.References.ItemChoices.Any(item => item.Id == objective.itemId && item.ProtectedQuestItem) == true);
        }

        private void ShowIssue(ContentIssue issue)
        {
            list.Select(string.IsNullOrEmpty(issue.EntityKey) ? Array.Empty<string>() : new[] { issue.EntityKey });
            var field = form?.FindField(issue.Field);
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

        private ContentIssueSeverity? IssueState(QuestEditorDefinition quest)
        {
            var key = Key(quest);
            var related = issues.Where(issue => string.Equals(issue.EntityKey, key, StringComparison.Ordinal)).ToArray();
            if (related.Length == 0)
            {
                return null;
            }

            return related.Any(issue => issue.Severity == ContentIssueSeverity.Error)
                ? ContentIssueSeverity.Error
                : ContentIssueSeverity.Warning;
        }

        private QuestEditorDefinition[] Quests()
        {
            return workspace?.Catalog.quests ?? Array.Empty<QuestEditorDefinition>();
        }

        // The quests in the order a load lists their files, so the edited content and a fresh
        // load of the saved files take the same form.
        private static QuestEditorDefinition[] ById(IEnumerable<QuestEditorDefinition> quests)
        {
            return quests.OrderBy(quest => quest?.id ?? string.Empty, StringComparer.Ordinal).ToArray();
        }

        private QuestEditorCatalog SavedForm()
        {
            if (workspace == null)
            {
                return null;
            }

            try
            {
                return service.Reconcile(workspace);
            }
            catch (Exception exception) when (exception is IOException
                || exception is InvalidDataException
                || exception is UnauthorizedAccessException)
            {
                // Unreadable files on disk fail the save itself; live validation checks the edits.
                return workspace.Catalog;
            }
        }

        private QuestEditorDefinition SingleTarget()
        {
            return form != null && form.Targets.Count == 1 ? form.Targets[0] : null;
        }

        private string NpcSummary(QuestEditorDefinition quest)
        {
            var giver = NpcName(quest.giverActorId);
            return quest.giverActorId == quest.turnInActorId ? giver : giver + " to " + NpcName(quest.turnInActorId);
        }

        private string NpcName(string actorId)
        {
            var actor = workspace?.References.Actors.FirstOrDefault(candidate => candidate.Id == actorId);
            return actor == null ? (string.IsNullOrEmpty(actorId) ? "(no NPC)" : actorId) : actor.DisplayName;
        }

        private string SearchText(QuestEditorDefinition quest)
        {
            return string.Join(" ", (quest.tags ?? Array.Empty<string>())
                .Concat(quest.NpcActorIds().Select(NpcName))
                .Concat(quest.Objectives().SelectMany(objective => new[] { objective.kind, objective.text, objective.itemId, objective.actorTag }
                    .Concat(objective.actorIds ?? Array.Empty<string>()))));
        }

        private IEnumerable<(string Value, string Label)> UsedNpcOptions(Func<QuestEditorDefinition, string> npc)
        {
            return Quests()
                .Select(npc)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => (id, workspace.References.ActorLabel(id)));
        }

        // The quest NPCs, and every other NPC a quest names, such as a removed one.
        private IEnumerable<(string Value, string Label)> NpcViewOptions()
        {
            var options = QuestNpcOptions().ToList();
            options.AddRange(Quests()
                .SelectMany(quest => quest.NpcActorIds())
                .Where(id => !string.IsNullOrEmpty(id) && options.All(option => option.Value != id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => (id, workspace.References.ActorLabel(id))));
            return options;
        }

        private IEnumerable<(string Value, string Label)> TagOptions()
        {
            return Quests()
                .SelectMany(quest => quest.tags ?? Array.Empty<string>())
                .Where(tag => !string.IsNullOrEmpty(tag))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(tag => tag, StringComparer.Ordinal)
                .Select(tag => (tag, tag));
        }

        private IEnumerable<(string Value, string Label)> QuestNpcOptions()
        {
            return Actors(workspace?.References.QuestNpcs);
        }

        private IEnumerable<(string Value, string Label)> MobOptions()
        {
            return Actors(workspace?.References.Mobs);
        }

        private IEnumerable<(string Value, string Label)> ResourceNodeOptions()
        {
            return Actors(workspace?.References.ResourceNodes);
        }

        private IEnumerable<(string Value, string Label)> MobTagOptions()
        {
            return (workspace?.References.ActorTags ?? Array.Empty<string>()).Select(tag => (tag, tag));
        }

        private IEnumerable<(string Value, string Label)> TargetOptions(string kind)
        {
            return kind == QuestObjectiveKindIds.Gather ? ResourceNodeOptions() : MobOptions();
        }

        // Every item an objective may collect or deliver: a quest item that another quest already
        // gives, collects, delivers, or drops is left out, because a quest item belongs to one
        // quest.
        private IEnumerable<(string Value, string Label)> ObjectiveItemOptions(QuestEditorDefinition quest)
        {
            var taken = QuestItemsOfOtherQuests(quest);
            return Items(item => !taken.Contains(item.Id));
        }

        // Start items are protected quest items that no other quest uses; rewards are ordinary
        // items without policies.
        private IEnumerable<(string Value, string Label)> ItemOptions(string field, QuestEditorDefinition quest = null)
        {
            if (field != StartItems)
            {
                return Items(item => item.Rewardable);
            }

            var taken = QuestItemsOfOtherQuests(quest ?? SingleTarget());
            return Items(item => item.ProtectedQuestItem && !taken.Contains(item.Id));
        }

        private HashSet<string> QuestItemsOfOtherQuests(QuestEditorDefinition quest)
        {
            var questItems = new HashSet<string>(
                (workspace?.References.ItemChoices ?? Array.Empty<QuestEditorItemChoice>())
                    .Where(item => item.ProtectedQuestItem)
                    .Select(item => item.Id),
                StringComparer.Ordinal);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var other in Quests().Where(entry => entry != null && !ReferenceEquals(entry, quest)))
            {
                foreach (var id in (other.startItems ?? Array.Empty<QuestEditorItemQuantity>())
                             .Select(item => item?.definitionId)
                             .Concat(other.Objectives().Select(objective => objective.itemId)))
                {
                    if (id != null && questItems.Contains(id))
                    {
                        taken.Add(id);
                    }
                }
            }

            return taken;
        }

        private IEnumerable<(string Value, string Label)> Items(Func<QuestEditorItemChoice, bool> include)
        {
            return (workspace?.References.ItemChoices ?? Array.Empty<QuestEditorItemChoice>())
                .Where(include)
                .Select(item => (item.Id, item.DisplayName + " (" + item.Id + ")"));
        }

        // Every other quest, except those that already require this one, directly or through
        // others, which would form a cycle.
        private IEnumerable<(string Value, string Label)> RequirementOptions(QuestEditorDefinition quest)
        {
            var followUps = QuestEditorRelations.AllFollowUpIds(workspace?.Catalog, quest?.id);
            return Quests()
                .Where(entry => !ReferenceEquals(entry, quest) && !string.IsNullOrEmpty(entry.id) && !followUps.Contains(entry.id))
                .Select(entry => (entry.id, (string.IsNullOrEmpty(entry.title) ? entry.id : entry.title) + " (" + entry.id + ")"));
        }

        private static IEnumerable<(string Value, string Label)> Actors(IEnumerable<QuestEditorActorChoice> actors)
        {
            return (actors ?? Array.Empty<QuestEditorActorChoice>()).Select(actor => (actor.Id, actor.DisplayName + " (" + actor.Id + ")"));
        }

        private static string FirstValue(IEnumerable<(string Value, string Label)> options)
        {
            return options.Select(option => option.Value).FirstOrDefault() ?? string.Empty;
        }

        private static string[] FirstOf(IEnumerable<(string Value, string Label)> options)
        {
            return options.Select(option => option.Value).Take(1).ToArray();
        }

        private static string KillMode(QuestEditorObjective objective)
        {
            return string.IsNullOrEmpty(objective?.actorTag) ? ListedMobs : MobTag;
        }

        private static string ObjectivePath(int step, int position)
        {
            return "steps[" + step + "].objectives[" + position + "]";
        }

        private static QuestEditorItemQuantity[] ItemList(QuestEditorDefinition quest, string field)
        {
            if (quest == null)
            {
                return null;
            }

            switch (field)
            {
                case StartItems:
                    return quest.startItems ?? Array.Empty<QuestEditorItemQuantity>();
                case RewardItems:
                    return quest.rewardItems ?? Array.Empty<QuestEditorItemQuantity>();
                case RewardChoiceItems:
                    return quest.rewardChoiceItems ?? Array.Empty<QuestEditorItemQuantity>();
                default:
                    throw new ArgumentOutOfRangeException(nameof(field), field, null);
            }
        }

        private static void SetItemList(QuestEditorDefinition quest, string field, QuestEditorItemQuantity[] items)
        {
            switch (field)
            {
                case StartItems:
                    quest.startItems = items;
                    break;
                case RewardItems:
                    quest.rewardItems = items;
                    break;
                case RewardChoiceItems:
                    quest.rewardChoiceItems = items;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(field), field, null);
            }
        }

        private static int ItemLimit(string field)
        {
            switch (field)
            {
                case StartItems:
                    return QuestContentRules.MaximumStartItems;
                case RewardItems:
                    return QuestContentRules.MaximumRewardItems;
                default:
                    return QuestContentRules.MaximumRewardChoices;
            }
        }

        private static string ItemListLabel(string field)
        {
            switch (field)
            {
                case StartItems:
                    return "Start Item";
                case RewardItems:
                    return "Reward Item";
                default:
                    return "Reward Choice";
            }
        }

        private static QuestEditorStep StepAt(QuestEditorDefinition quest, int position)
        {
            return At(quest?.steps, position);
        }

        private static QuestEditorObjective ObjectiveAt(QuestEditorDefinition quest, int step, int position)
        {
            return At(StepAt(quest, step)?.objectives, position);
        }

        private static void EditStep(QuestEditorDefinition quest, int position, Action<QuestEditorStep> edit)
        {
            var step = StepAt(quest, position);
            if (step != null)
            {
                edit(step);
            }
        }

        private static void EditObjective(QuestEditorDefinition quest, int step, int position, Action<QuestEditorObjective> edit)
        {
            var objective = ObjectiveAt(quest, step, position);
            if (objective != null)
            {
                edit(objective);
            }
        }

        private static void EditItem(QuestEditorDefinition quest, string field, int position, Action<QuestEditorItemQuantity> edit)
        {
            var item = At(ItemList(quest, field), position);
            if (item != null)
            {
                edit(item);
            }
        }

        private static void EditDropSource(QuestEditorDefinition quest, int step, int position, int source, Action<QuestEditorDropSource> edit)
        {
            var drop = At(ObjectiveAt(quest, step, position)?.dropSources, source);
            if (drop != null)
            {
                edit(drop);
            }
        }

        private static T At<T>(T[] values, int position)
            where T : class
        {
            return values != null && position >= 0 && position < values.Length ? values[position] : null;
        }

        private static void SetAt(string[] values, int position, string value)
        {
            if (values != null && position >= 0 && position < values.Length)
            {
                values[position] = value;
            }
        }

        private static T[] Without<T>(T[] values, int position)
        {
            return values.Where((_, index) => index != position).ToArray();
        }

        private static bool Swap<T>(T[] values, int position, int offset, out T[] swapped)
        {
            var target = position + offset;
            swapped = values;
            if (position < 0 || position >= values.Length || target < 0 || target >= values.Length)
            {
                return false;
            }

            swapped = values.ToArray();
            (swapped[position], swapped[target]) = (swapped[target], swapped[position]);
            return true;
        }

        private static int Count<T>(T[] values)
        {
            return values?.Length ?? 0;
        }

        // Quest texts are one trimmed line, so a line break typed or pasted into a text becomes
        // a space.
        private static string OneLine(string value)
        {
            return ControlCharacters.Replace(value ?? string.Empty, " ").Trim();
        }

        private static TextField TextArea(TextField field, string tooltip)
        {
            field.multiline = true;
            field.tooltip = tooltip;
            field.AddToClassList("content-text-area");
            return field;
        }

        private static VisualElement Box(string title = null)
        {
            var box = new VisualElement();
            box.AddToClassList(ContentToolStyles.Section);
            if (!string.IsNullOrEmpty(title))
            {
                var label = new Label(title);
                label.AddToClassList(ContentToolStyles.SectionTitle);
                box.Add(label);
            }

            return box;
        }

        private static VisualElement Row()
        {
            var row = new VisualElement();
            row.AddToClassList(ContentToolStyles.Row);
            return row;
        }

        private static Button EnabledButton(string text, bool enabled, Action click)
        {
            var button = new Button(click) { text = text };
            button.SetEnabled(enabled);
            return button;
        }

        // A button beside a field in a row, which keeps its own width.
        private static Button FixedButton(string text, Action click)
        {
            var button = new Button(click) { text = text };
            button.style.flexGrow = 0f;
            return button;
        }

        private static bool ConfirmChanges(string[] changes)
        {
            return EditorUtility.DisplayDialog(
                "Compile Quests",
                string.Join("\n", changes) + "\n\nWrite the quest files and the compiled quest catalog?",
                "Write files",
                "Cancel");
        }
    }
}
