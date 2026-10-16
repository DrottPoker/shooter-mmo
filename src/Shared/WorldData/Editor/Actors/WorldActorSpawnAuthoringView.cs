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
    // The Spawn Authoring tool: every spawn point, group, area, and patrol path of the edited
    // World in one filterable list beside an editor for the selected entries, scene handles,
    // ground snapping, prefab previews, live validation with the shared compiler, and the
    // preview and save actions. Several selected entries are edited together; each field
    // applies to the selected entries that have it.
    public sealed class WorldActorSpawnAuthoringView : VisualElement, IDisposable
    {
        public const long LiveValidationDelayMilliseconds = 300;
        public const float GroundProbeHeight = 1000f;

        private static readonly WorldActorSpawnEntryKind[] Kinds =
        {
            WorldActorSpawnEntryKind.Point,
            WorldActorSpawnEntryKind.Group,
            WorldActorSpawnEntryKind.Area,
            WorldActorSpawnEntryKind.PatrolPath
        };

        private readonly WorldActorEditorService service;
        private readonly ContentUndoState undoState;
        private readonly Func<WorldActorEditorWorkspace> loadWorkspace;
        private readonly ContentToolLayout toolLayout;
        private readonly ContentListPane<WorldActorSpawnEntry> list;
        private readonly Button duplicateButton;
        private readonly Button snapButton;
        private readonly Button frameButton;
        private readonly Button deleteButton;
        private readonly IVisualElementScheduledItem liveValidation;
        private ContentEditSession<WorldActorEditorSnapshot> session;
        private ContentForm<WorldActorSpawnEntry> form;
        private WorldActorEditorWorkspace workspace;
        private IReadOnlyList<ContentIssue> issues = Array.Empty<ContentIssue>();
        private string detailShape = string.Empty;

        public WorldActorSpawnAuthoringView(
            WorldActorEditorService service,
            ContentUndoState undoState,
            Func<WorldActorEditorWorkspace> loadWorkspace = null)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.undoState = undoState ? undoState : throw new ArgumentNullException(nameof(undoState));
            this.loadWorkspace = loadWorkspace ?? service.Load;
            style.flexGrow = 1f;

            toolLayout = new ContentToolLayout(300f);
            Add(toolLayout);
            toolLayout.Toolbar.Add(new ToolbarButton(Reload) { text = "Reload", tooltip = "Load spawn and actor content from disk again." });
            var scenePreview = new ToolbarToggle { text = "Scene Preview", value = true, tooltip = "Draw every entry and the handles of the selected ones in the Scene view." };
            scenePreview.RegisterValueChangedCallback(change =>
            {
                ScenePreviewEnabled = change.newValue;
                SceneView.RepaintAll();
            });
            toolLayout.Toolbar.Add(scenePreview);
            toolLayout.Toolbar.Add(new ToolbarSpacer { flex = true });

            list = new ContentListPane<WorldActorSpawnEntry>(
                "Spawn Entries",
                entry => entry.Key,
                entry => entry.Id,
                Subtitle,
                entry => entry.ActorId + " " + entry.PatrolPathId);
            list.AddChoiceFilter("All types", () => Kinds.Select(kind => (kind.ToString(), WorldActorSpawnEntry.Label(kind))), (entry, value) =>
                string.Equals(entry.Kind.ToString(), value, StringComparison.Ordinal));
            list.AddChoiceFilter("All actors", ActorOptions, (entry, value) => string.Equals(entry.ActorId, value, StringComparison.Ordinal));
            list.AddToggleFilter("Problems", entry => IssueState(entry) != null);
            list.SelectionChanged += OnSelectionChanged;
            toolLayout.ListSlot.Add(list);
            var addActions = new VisualElement();
            addActions.AddToClassList(ContentToolStyles.Row);
            foreach (var kind in Kinds)
            {
                addActions.Add(new Button(() => AddEntry(kind))
                {
                    text = "New " + WorldActorSpawnEntry.Label(kind),
                    tooltip = "Add a " + WorldActorSpawnEntry.Label(kind).ToLowerInvariant() + " at the Scene view pivot."
                });
            }

            toolLayout.ListSlot.Add(addActions);
            var selectionActions = new VisualElement();
            selectionActions.AddToClassList(ContentToolStyles.Row);
            duplicateButton = new Button(DuplicateSelected) { text = "Duplicate", tooltip = "Copy every selected entry, one unit along X." };
            snapButton = new Button(() => GroundSnapSelected()) { text = "Ground Snap", tooltip = "Move the selected entries onto the scene collision below them." };
            frameButton = new Button(FrameSelected) { text = "Frame", tooltip = "Frame the selected entries in the Scene view." };
            deleteButton = new Button(() => DeleteSelected()) { text = "Delete", tooltip = "Delete the selected entries." };
            selectionActions.Add(duplicateButton);
            selectionActions.Add(snapButton);
            selectionActions.Add(frameButton);
            selectionActions.Add(deleteButton);
            toolLayout.ListSlot.Add(selectionActions);

            toolLayout.Actions.Add(new Button(PreviewChanges) { text = "Preview Changes", tooltip = "Compile with the content compiler and show the canonical files." });
            toolLayout.Actions.Add(new Button(() => Save()) { text = "Save + Compile", tooltip = "Write the spawn and actor files and the canonical runtime of every World." });
            toolLayout.Actions.Add(new Button(VerifyCanonical) { text = "Verify Canonical", tooltip = "Check that the committed runtime matches the authoring files." });
            var info = new Label(
                "Spawn Authoring and Actor Studio each edit their own copy of the content. "
                    + "Save one before editing in the other.");
            info.AddToClassList("content-actions__info");
            toolLayout.Actions.Add(info);
            toolLayout.Issues.IssueActivated += ShowIssue;

            liveValidation = schedule.Execute(RunLiveValidation);
            liveValidation.Pause();
            ShowDetailHint("Loading spawn content...");
        }

        public event Action UnsavedChangesChanged;

        public bool HasUnsavedChanges => session != null && session.HasUnsavedChanges;

        public bool ScenePreviewEnabled { get; set; } = true;

        public WorldActorEditorWorkspace Workspace => workspace;

        public ContentListPane<WorldActorSpawnEntry> List => list;

        public ContentForm<WorldActorSpawnEntry> Form => form;

        public IReadOnlyList<ContentIssue> Issues => issues;

        public string StatusText => toolLayout.StatusText;

        public HelpBoxMessageType StatusType => toolLayout.StatusType;

        // Loads spawn and actor content from disk. With keepPendingEdits, edits that were not
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
                form = new ContentForm<WorldActorSpawnEntry>(session, entry => entry.Key);
                detailShape = string.Empty;
                session.Changed += OnContentChanged;
                session.Load(keepPendingEdits);
                var entries = WorldActorSpawnEntry.List(workspace.Spawns);
                if (list.Selection.Count == 0 && entries.Count > 0)
                {
                    list.Select(new[] { entries[0].Key });
                }

                toolLayout.SetStatus(
                    "Loaded " + entries.Count + " spawn entries of World '" + service.Paths.WorldId + "'."
                        + (HasUnsavedChanges ? " Unsaved edits from before the script reload were restored." : string.Empty),
                    HelpBoxMessageType.Info);
                RunLiveValidation();
            }
            catch (Exception exception)
            {
                workspace = null;
                session?.Dispose();
                session = null;
                list.SetItems(Array.Empty<WorldActorSpawnEntry>());
                ShowDetailHint("Spawn content could not be loaded.");
                toolLayout.SetStatus(exception.Message, HelpBoxMessageType.Error);
            }

            SceneView.RepaintAll();
            UnsavedChangesChanged?.Invoke();
        }

        public void Reload()
        {
            if (HasUnsavedChanges
                && !EditorUtility.DisplayDialog(
                    "Discard Unsaved Spawn Changes",
                    "Spawn Authoring has unsaved changes. Reloading discards them.",
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
                toolLayout.SetStatus("The write was cancelled. No spawn files were changed.", HelpBoxMessageType.Warning);
                return false;
            }

            try
            {
                service.SaveAndCompile(workspace, validation);
                AssetDatabase.Refresh();
                session.MarkSaved();
                toolLayout.SetStatus(
                    "World actor spawn authoring and canonical runtime files were written. " + validation.Message,
                    HelpBoxMessageType.Info);
                return true;
            }
            catch (Exception exception)
            {
                toolLayout.SetStatus("Spawn content write failed: " + exception.Message, HelpBoxMessageType.Error);
                return false;
            }
        }

        // Adds an entry at the Scene view pivot and selects it.
        public void AddEntry(WorldActorSpawnEntryKind kind)
        {
            if (workspace == null)
            {
                return;
            }

            var pivot = ScenePivot();
            var spawns = workspace.Spawns;
            string key = null;
            session.Change("New " + WorldActorSpawnEntry.Label(kind).ToLowerInvariant(), () =>
            {
                switch (kind)
                {
                    case WorldActorSpawnEntryKind.Point:
                        var point = new WorldActorEditorSpawnPoint
                        {
                            id = CreateUniqueSpawnId(IdPrefix() + ".new_point"),
                            actorId = FirstActorId(),
                            x = pivot.x,
                            y = pivot.y,
                            z = pivot.z,
                            patrolPathId = string.Empty
                        };
                        spawns.spawnPoints = Append(spawns.spawnPoints, point);
                        key = WorldActorEditorIssues.Key(WorldActorEditorIssues.SpawnPoints, point.id);
                        break;
                    case WorldActorSpawnEntryKind.Group:
                        var group = new WorldActorEditorSpawnGroup
                        {
                            id = CreateUniqueSpawnId(IdPrefix() + ".new_group"),
                            actorId = FirstActorId(),
                            centerX = pivot.x,
                            centerY = pivot.y,
                            centerZ = pivot.z,
                            patrolPathId = string.Empty
                        };
                        spawns.spawnGroups = Append(spawns.spawnGroups, group);
                        key = WorldActorEditorIssues.Key(WorldActorEditorIssues.SpawnGroups, group.id);
                        break;
                    case WorldActorSpawnEntryKind.Area:
                        var area = new WorldActorEditorSpawnArea
                        {
                            id = CreateUniqueSpawnId(IdPrefix() + ".new_area"),
                            actorId = FirstActorId(),
                            minimumX = pivot.x - 2f,
                            minimumZ = pivot.z - 2f,
                            maximumX = pivot.x + 2f,
                            maximumZ = pivot.z + 2f,
                            y = pivot.y,
                            patrolPathId = string.Empty
                        };
                        spawns.spawnAreas = Append(spawns.spawnAreas, area);
                        key = WorldActorEditorIssues.Key(WorldActorEditorIssues.SpawnAreas, area.id);
                        break;
                    default:
                        var patrol = new WorldActorEditorPatrolPath
                        {
                            id = CreateUniquePatrolPathId(IdPrefix() + ".new_patrol"),
                            points = new[]
                            {
                                new WorldActorEditorPatrolPoint { x = pivot.x, y = pivot.y, z = pivot.z, waitSeconds = 1f },
                                new WorldActorEditorPatrolPoint { x = pivot.x + 2f, y = pivot.y, z = pivot.z, waitSeconds = 1f }
                            }
                        };
                        spawns.patrolPaths = Append(spawns.patrolPaths, patrol);
                        key = WorldActorEditorIssues.Key(WorldActorEditorIssues.PatrolPaths, patrol.id);
                        break;
                }
            });
            list.Select(new[] { key });
        }

        public void DuplicateSelected()
        {
            var selection = list.Selection;
            if (workspace == null || selection.Count == 0)
            {
                return;
            }

            var spawns = workspace.Spawns;
            var keys = new List<string>();
            session.Change(selection.Count == 1 ? "Duplicate spawn entry" : "Duplicate " + selection.Count + " spawn entries", () =>
            {
                foreach (var entry in selection)
                {
                    WorldActorSpawnEntry copy;
                    switch (entry.Kind)
                    {
                        case WorldActorSpawnEntryKind.Point:
                            var point = WorldActorEditorJson.Clone(entry.Point);
                            point.id = CreateUniqueSpawnId(entry.Id + ".copy");
                            spawns.spawnPoints = Append(spawns.spawnPoints, point);
                            copy = WorldActorSpawnEntry.List(spawns).Last(value => value.Point == point);
                            break;
                        case WorldActorSpawnEntryKind.Group:
                            var group = WorldActorEditorJson.Clone(entry.Group);
                            group.id = CreateUniqueSpawnId(entry.Id + ".copy");
                            spawns.spawnGroups = Append(spawns.spawnGroups, group);
                            copy = WorldActorSpawnEntry.List(spawns).Last(value => value.Group == group);
                            break;
                        case WorldActorSpawnEntryKind.Area:
                            var area = WorldActorEditorJson.Clone(entry.Area);
                            area.id = CreateUniqueSpawnId(entry.Id + ".copy");
                            spawns.spawnAreas = Append(spawns.spawnAreas, area);
                            copy = WorldActorSpawnEntry.List(spawns).Last(value => value.Area == area);
                            break;
                        default:
                            var patrol = WorldActorEditorJson.Clone(entry.PatrolPath);
                            patrol.id = CreateUniquePatrolPathId(entry.Id + ".copy");
                            spawns.patrolPaths = Append(spawns.patrolPaths, patrol);
                            copy = WorldActorSpawnEntry.List(spawns).Last(value => value.PatrolPath == patrol);
                            break;
                    }

                    copy.Position += Vector3.right;
                    keys.Add(copy.Key);
                }
            });
            list.Select(keys);
        }

        // Deletes the selected entries. A patrol path that a remaining spawn follows stays.
        // With confirm, the caller approves the listed ids instead of a dialog.
        public void DeleteSelected(Func<string[], bool> confirm = null)
        {
            var selection = list.Selection;
            if (workspace == null || selection.Count == 0)
            {
                return;
            }

            var remaining = WorldActorSpawnEntry.List(workspace.Spawns)
                .Where(entry => entry.PlacesActors && !selection.Any(selected => selected.Key == entry.Key))
                .ToArray();
            var kept = selection
                .Where(entry => entry.Kind == WorldActorSpawnEntryKind.PatrolPath
                    && remaining.Any(spawn => string.Equals(spawn.PatrolPathId, entry.Id, StringComparison.Ordinal)))
                .ToArray();
            var deleted = selection.Except(kept).ToArray();
            if (deleted.Length == 0)
            {
                toolLayout.SetStatus(
                    "Nothing was deleted. Spawns still follow " + string.Join(", ", kept.Select(entry => entry.Id)) + ".",
                    HelpBoxMessageType.Warning);
                return;
            }

            var approve = confirm ?? (values => EditorUtility.DisplayDialog(
                "Delete Spawn Entries",
                "Delete these entries from the authoring workspace?\n\n" + string.Join("\n", values),
                "Delete",
                "Cancel"));
            if (!approve(deleted.Select(entry => entry.Id).ToArray()))
            {
                return;
            }

            var spawns = workspace.Spawns;
            session.Change(deleted.Length == 1 ? "Delete spawn entry" : "Delete " + deleted.Length + " spawn entries", () =>
            {
                spawns.spawnPoints = spawns.spawnPoints?.Where(value => !deleted.Any(entry => entry.Point == value)).ToArray();
                spawns.spawnGroups = spawns.spawnGroups?.Where(value => !deleted.Any(entry => entry.Group == value)).ToArray();
                spawns.spawnAreas = spawns.spawnAreas?.Where(value => !deleted.Any(entry => entry.Area == value)).ToArray();
                spawns.patrolPaths = spawns.patrolPaths?.Where(value => !deleted.Any(entry => entry.PatrolPath == value)).ToArray();
            });
            toolLayout.SetStatus(
                "Deleted " + deleted.Length + (deleted.Length == 1 ? " entry." : " entries.")
                    + (kept.Length > 0 ? " Kept patrol paths that spawns still follow: " + string.Join(", ", kept.Select(entry => entry.Id)) + "." : string.Empty),
                kept.Length > 0 ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info);
        }

        // Renames an entry. Renaming a patrol path also updates the spawns that follow it, as
        // one undo step.
        public bool RenameEntry(WorldActorSpawnEntry entry, string newId)
        {
            newId = newId?.Trim() ?? string.Empty;
            if (workspace == null || entry == null || newId.Length == 0 || string.Equals(entry.Id, newId, StringComparison.Ordinal))
            {
                form?.Refresh();
                return false;
            }

            var patrol = entry.Kind == WorldActorSpawnEntryKind.PatrolPath;
            var taken = patrol ? PatrolPathIds() : SpawnIds();
            if (taken.Contains(newId))
            {
                toolLayout.SetStatus("Another " + (patrol ? "patrol path" : "spawn entry") + " already uses the id '" + newId + "'.", HelpBoxMessageType.Error);
                form?.Refresh();
                return false;
            }

            var currentId = entry.Id;
            var followers = patrol
                ? WorldActorSpawnEntry.List(workspace.Spawns).Where(value => value.PlacesActors && value.PatrolPathId == currentId).ToArray()
                : Array.Empty<WorldActorSpawnEntry>();
            session.Change(patrol ? "Patrol path id changed" : "Spawn id changed", () =>
            {
                entry.Id = newId;
                foreach (var follower in followers)
                {
                    follower.PatrolPathId = newId;
                }
            });
            list.Select(new[] { WorldActorEditorIssues.Key(entry.Collection, newId) });
            toolLayout.SetStatus(
                followers.Length == 0
                    ? "Renamed the entry."
                    : "Renamed the patrol path and updated " + followers.Length + (followers.Length == 1 ? " spawn" : " spawns") + " that follow it.",
                HelpBoxMessageType.Info);
            return true;
        }

        // Moves every selected entry, and every point of a selected patrol path, onto the
        // collision below it as one undo step. GroundHeight finds the surface below a position;
        // by default it raycasts against the open scene's colliders.
        public int GroundSnapSelected(Func<Vector3, float?> groundHeight = null)
        {
            var selection = list.Selection;
            if (workspace == null || selection.Count == 0)
            {
                toolLayout.SetStatus("Select entries before ground snapping.", HelpBoxMessageType.Warning);
                return 0;
            }

            var probe = groundHeight ?? RaycastGround;
            var snapped = 0;
            var missed = 0;
            session.Change(selection.Count == 1 ? "Ground snap" : "Ground snap " + selection.Count + " entries", () =>
            {
                foreach (var entry in selection)
                {
                    if (entry.Kind == WorldActorSpawnEntryKind.PatrolPath)
                    {
                        foreach (var point in entry.PatrolPath.points ?? Array.Empty<WorldActorEditorPatrolPoint>())
                        {
                            var height = probe(new Vector3(point.x, point.y, point.z));
                            if (height.HasValue)
                            {
                                point.y = height.Value;
                                snapped++;
                            }
                            else
                            {
                                missed++;
                            }
                        }

                        continue;
                    }

                    var position = entry.Position;
                    var ground = probe(position);
                    if (ground.HasValue)
                    {
                        entry.Position = new Vector3(position.x, ground.Value, position.z);
                        snapped++;
                    }
                    else
                    {
                        missed++;
                    }
                }
            });
            toolLayout.SetStatus(
                "Snapped " + snapped + (snapped == 1 ? " position" : " positions") + " to the ground."
                    + (missed > 0 ? " " + missed + " found no collider below." : string.Empty),
                missed > 0 ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info);
            return snapped;
        }

        public void AddPatrolPoint()
        {
            var patrol = SinglePatrolPath();
            if (patrol == null)
            {
                return;
            }

            session.Change("Patrol point added", () =>
            {
                var last = patrol.points?.LastOrDefault();
                patrol.points = Append(patrol.points, new WorldActorEditorPatrolPoint
                {
                    x = (last?.x ?? 0f) + 2f,
                    y = last?.y ?? 0f,
                    z = last?.z ?? 0f,
                    waitSeconds = 1f
                });
            });
        }

        // Draws every entry in the Scene view, handles for the selected ones, and selects an
        // entry when its marker is clicked. Ctrl or Shift adds it to the selection.
        public void DrawSceneGui()
        {
            if (!ScenePreviewEnabled || workspace == null || session == null)
            {
                return;
            }

            var entries = WorldActorSpawnEntry.List(workspace.Spawns);
            var selection = list.Selection;
            var selectedKeys = new HashSet<string>(selection.Select(entry => entry.Key), StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                DrawEntry(entry, selectedKeys.Contains(entry.Key));
            }

            // Selected entries have move handles instead, which would compete for the click.
            foreach (var entry in entries.Where(value => !selectedKeys.Contains(value.Key)))
            {
                var position = entry.Position;
                var size = HandleUtility.GetHandleSize(position) * 0.12f;
                Handles.color = new Color(1f, 1f, 1f, 0.6f);
                if (Handles.Button(position, Quaternion.identity, size, size * 1.2f, Handles.SphereHandleCap))
                {
                    var additive = Event.current.control || Event.current.shift;
                    list.Select(additive ? selectedKeys.Concat(new[] { entry.Key }) : new[] { entry.Key });
                    return;
                }
            }

            var placing = selection.Where(entry => entry.PlacesActors).ToArray();
            if (placing.Length > 0)
            {
                var center = placing.Aggregate(Vector3.zero, (sum, entry) => sum + entry.Position) / placing.Length;
                EditorGUI.BeginChangeCheck();
                var moved = Handles.PositionHandle(center, Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    var offset = moved - center;
                    session.ChangeContinuously(placing.Length == 1 ? "Move " + placing[0].Id : "Move " + placing.Length + " spawn entries", () =>
                    {
                        foreach (var entry in placing)
                        {
                            entry.Position += offset;
                        }
                    });
                }
            }

            foreach (var entry in selection.Where(value => value.Kind == WorldActorSpawnEntryKind.PatrolPath))
            {
                var points = entry.PatrolPath.points ?? Array.Empty<WorldActorEditorPatrolPoint>();
                for (var index = 0; index < points.Length; index++)
                {
                    var point = points[index];
                    EditorGUI.BeginChangeCheck();
                    var moved = Handles.PositionHandle(new Vector3(point.x, point.y, point.z), Quaternion.identity);
                    if (EditorGUI.EndChangeCheck())
                    {
                        var label = "Move " + entry.Id + " point " + (index + 1);
                        session.ChangeContinuously(label, () =>
                        {
                            point.x = moved.x;
                            point.y = moved.y;
                            point.z = moved.z;
                        });
                    }
                }
            }
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

        private void FrameSelected()
        {
            var selection = list.Selection;
            var sceneView = SceneView.lastActiveSceneView;
            if (selection.Count == 0 || sceneView == null)
            {
                return;
            }

            var positions = selection.SelectMany(Positions).ToArray();
            var bounds = new Bounds(positions[0], Vector3.one * 2f);
            foreach (var position in positions)
            {
                bounds.Encapsulate(new Bounds(position, Vector3.one * 2f));
            }

            sceneView.Frame(bounds, false);
        }

        private void OnContentChanged()
        {
            list.SetItems(WorldActorSpawnEntry.List(workspace.Spawns));
            UnsavedChangesChanged?.Invoke();
            liveValidation.ExecuteLater(LiveValidationDelayMilliseconds);
            SceneView.RepaintAll();
        }

        private void RunLiveValidation()
        {
            issues = WorldActorEditorIssues.Collect(workspace);
            list.SetIssueState(IssueState);
            toolLayout.Issues.SetIssues(issues, issue => WorldActorEditorIssues.Describe(issue, workspace));
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
            snapButton.SetEnabled(selection.Count > 0);
            frameButton.SetEnabled(selection.Count > 0);
            deleteButton.SetEnabled(selection.Count > 0);
            SceneView.RepaintAll();
        }

        // The detail panel is rebuilt only when its layout changes, so typing keeps focus.
        private string DetailShape(IReadOnlyList<WorldActorSpawnEntry> selection)
        {
            if (selection.Count == 0)
            {
                return "none";
            }

            var kinds = string.Join(",", selection.Select(entry => entry.Kind).Distinct().OrderBy(kind => kind));
            var mobs = selection.Any(entry => IsMob(ActorOf(entry)));
            if (selection.Count > 1)
            {
                return "many:" + kinds + ":" + mobs;
            }

            var single = selection[0];
            return "one:" + single.Key + ":" + mobs + ":" + (single.PatrolPath?.points?.Length ?? 0);
        }

        private void BuildDetail(IReadOnlyList<WorldActorSpawnEntry> selection)
        {
            form.Clear();
            toolLayout.Detail.Clear();
            if (selection.Count == 0)
            {
                ShowDetailHint("Select one or more spawn entries, or click a marker in the Scene view. Hold Ctrl or Shift to select several.");
                form.SetTargets(selection);
                return;
            }

            var single = selection.Count == 1 ? selection[0] : null;
            var placing = selection.Any(entry => entry.PlacesActors);
            Func<WorldActorSpawnEntry, bool> placesActors = entry => entry.PlacesActors;

            var identity = ContentToolStyles.CreateSection(single == null ? "Spawn Entries" : single.KindLabel);
            if (single != null)
            {
                var idField = new TextField("Stable Id") { isDelayed = true };
                idField.tooltip = single.Kind == WorldActorSpawnEntryKind.PatrolPath
                    ? "Renaming also updates the spawns that follow this patrol path."
                    : "Spawn ids are unique across points, groups, and areas.";
                idField.RegisterValueChangedCallback(change => RenameEntry(form.Targets.FirstOrDefault(), change.newValue));
                form.Watch(() => idField.SetValueWithoutNotify(form.Targets.Count == 1 ? form.Targets[0].Id ?? string.Empty : string.Empty));
                form.Register("id", idField);
                identity.Add(idField);
            }
            else
            {
                var counts = selection.GroupBy(entry => entry.Kind)
                    .OrderBy(group => group.Key)
                    .Select(group => group.Count() + " " + WorldActorSpawnEntry.Label(group.Key).ToLowerInvariant() + (group.Count() == 1 ? string.Empty : "s"));
                identity.Add(ContentToolStyles.CreateHint(
                    selection.Count + " entries selected: " + string.Join(", ", counts) + ". Each change applies to the selected "
                        + "entries that have the setting. Ids and patrol points are edited one entry at a time."));
            }

            if (placing)
            {
                identity.Add(form.Choice("Actor", "actorId", ActorOptions, entry => entry.ActorId, (entry, value) => entry.ActorId = value, placesActors));
                identity.Add(form.Choice("Patrol Path", "patrolPathId", PatrolPathOptions, entry => entry.PatrolPathId, (entry, value) => entry.PatrolPathId = value, placesActors));
            }

            toolLayout.Detail.Add(identity);
            if (placing)
            {
                toolLayout.Detail.Add(BuildPlacementSection(placesActors));
            }

            if (selection.Any(entry => entry.Kind == WorldActorSpawnEntryKind.Group))
            {
                toolLayout.Detail.Add(BuildGroupSection());
            }

            if (selection.Any(entry => entry.Kind == WorldActorSpawnEntryKind.Area))
            {
                toolLayout.Detail.Add(BuildAreaSection());
            }

            if (selection.Any(entry => IsMob(ActorOf(entry))))
            {
                toolLayout.Detail.Add(BuildMobLifecycleSection());
            }

            if (single?.Kind == WorldActorSpawnEntryKind.PatrolPath)
            {
                toolLayout.Detail.Add(BuildPatrolPointsSection(single.PatrolPath));
            }

            if (single != null && single.PlacesActors)
            {
                toolLayout.Detail.Add(BuildPresentationPreview());
            }

            form.SetTargets(selection);
        }

        private VisualElement BuildPlacementSection(Func<WorldActorSpawnEntry, bool> placesActors)
        {
            var section = ContentToolStyles.CreateSection("Placement");
            var position = form.Vector3("Position", "position", entry => entry.Position, (entry, value) => entry.Position = value, placesActors);
            position.tooltip = "A point's position, a group's center, or an area's center and height.";
            form.Covers("position", "x", "y", "z", "minimum.y", "maximum.y");
            section.Add(position);
            section.Add(form.Float("Yaw Degrees", "yawDegrees", entry => entry.YawDegrees, (entry, value) => entry.YawDegrees = value, placesActors));
            var instances = ContentToolStyles.CreateHint(string.Empty);
            form.Watch(() =>
            {
                var count = form.Targets.Sum(entry => entry.InstanceCount);
                instances.text = "Places " + count + (count == 1 ? " actor" : " actors") + " at startup. Drag the handle in the Scene view to move the selection.";
            });
            section.Add(instances);
            return section;
        }

        private VisualElement BuildGroupSection()
        {
            Func<WorldActorSpawnEntry, bool> isGroup = entry => entry.Kind == WorldActorSpawnEntryKind.Group;
            var section = ContentToolStyles.CreateSection("Group Layout");
            section.Add(form.Integer("Rows", "rows", entry => entry.Group?.rows ?? 0, (entry, value) => entry.Group.rows = value, isGroup));
            section.Add(form.Integer("Columns", "columns", entry => entry.Group?.columns ?? 0, (entry, value) => entry.Group.columns = value, isGroup));
            section.Add(form.Float("Spacing", "spacing", entry => entry.Group?.spacing ?? 0f, (entry, value) => entry.Group.spacing = value, isGroup));
            section.Add(ContentToolStyles.CreateHint(
                "A group places rows times columns actors on a grid around its center, along X and Z."));
            return section;
        }

        private VisualElement BuildAreaSection()
        {
            Func<WorldActorSpawnEntry, bool> isArea = entry => entry.Kind == WorldActorSpawnEntryKind.Area;
            var section = ContentToolStyles.CreateSection("Area");
            section.Add(GroundPlane(form.Vector2(
                "Minimum XZ",
                "area.minimum",
                entry => entry.Area == null ? Vector2.zero : new Vector2(entry.Area.minimumX, entry.Area.minimumZ),
                (entry, value) =>
                {
                    entry.Area.minimumX = value.x;
                    entry.Area.minimumZ = value.y;
                },
                isArea)));
            form.Covers("area.minimum", "minimum.x", "minimum.z");
            section.Add(GroundPlane(form.Vector2(
                "Maximum XZ",
                "area.maximum",
                entry => entry.Area == null ? Vector2.zero : new Vector2(entry.Area.maximumX, entry.Area.maximumZ),
                (entry, value) =>
                {
                    entry.Area.maximumX = value.x;
                    entry.Area.maximumZ = value.y;
                },
                isArea)));
            form.Covers("area.maximum", "maximum.x", "maximum.z");
            section.Add(form.Integer("Count", "count", entry => entry.Area?.count ?? 0, (entry, value) => entry.Area.count = value, isArea));
            section.Add(form.Integer("Seed", "seed", entry => entry.Area?.seed ?? 0, (entry, value) => entry.Area.seed = value, isArea));
            section.Add(ContentToolStyles.CreateHint(
                "An area places its count of actors at positions that its seed chooses deterministically."));
            return section;
        }

        // Settings of the placed Mob's definition and its respawn profile. They apply wherever
        // that Mob spawns.
        private VisualElement BuildMobLifecycleSection()
        {
            Func<WorldActorSpawnEntry, bool> placesMob = entry => IsMob(ActorOf(entry));
            Func<WorldActorSpawnEntry, bool> hasRespawn = entry => RespawnProfileOf(entry) != null;
            var section = ContentToolStyles.CreateSection("Mob Lifecycle");
            section.Add(ContentToolStyles.CreateHint(
                "These settings belong to the Mob definition and its respawn profile, so they apply wherever the Mob spawns."));
            section.Add(form.Choice(
                "Activity Profile",
                "actor.activityProfileId",
                () => (workspace?.Catalog.activityProfiles ?? Array.Empty<WorldActorEditorActivityProfile>()).Select(profile => (profile.id, profile.id)),
                entry => ActorOf(entry)?.activityProfileId,
                (entry, value) => ActorOf(entry).activityProfileId = value,
                placesMob));
            section.Add(form.Choice(
                "Respawn Profile",
                "actor.respawnProfileId",
                () => (workspace?.Catalog.respawnProfiles ?? Array.Empty<WorldActorEditorRespawnProfile>()).Select(profile => (profile.id, profile.id)),
                entry => ActorOf(entry)?.respawnProfileId,
                (entry, value) => ActorOf(entry).respawnProfileId = value,
                placesMob));
            section.Add(form.Integer(
                "Population Limit",
                "respawn.populationLimit",
                entry => RespawnProfileOf(entry)?.populationLimit ?? 0,
                (entry, value) => RespawnProfileOf(entry).populationLimit = value,
                hasRespawn));
            section.Add(form.Float(
                "Respawn Delay Seconds",
                "respawn.delaySeconds",
                entry => RespawnProfileOf(entry)?.delaySeconds ?? 0f,
                (entry, value) => RespawnProfileOf(entry).delaySeconds = value,
                hasRespawn));
            return section;
        }

        private VisualElement BuildPatrolPointsSection(WorldActorEditorPatrolPath patrol)
        {
            var section = ContentToolStyles.CreateSection("Patrol Points");
            var points = patrol.points ?? Array.Empty<WorldActorEditorPatrolPoint>();
            for (var position = 0; position < points.Length; position++)
            {
                section.Add(BuildPatrolPoint(position, points.Length));
            }

            section.Add(new Button(AddPatrolPoint) { text = "Add Patrol Point" });
            section.Add(ContentToolStyles.CreateHint(
                "Actors walk the points in order and wait at each. A path needs at least two points. Drag the point handles in the Scene view."));
            return section;
        }

        private VisualElement BuildPatrolPoint(int position, int count)
        {
            var box = new VisualElement();
            box.AddToClassList(ContentToolStyles.Section);
            var path = "points[" + position + "]";
            box.Add(form.Vector3(
                "Point " + (position + 1),
                path + ".position",
                entry => PatrolPointAt(entry, position) is WorldActorEditorPatrolPoint point ? new Vector3(point.x, point.y, point.z) : Vector3.zero,
                (entry, value) =>
                {
                    var point = PatrolPointAt(entry, position);
                    if (point != null)
                    {
                        point.x = value.x;
                        point.y = value.y;
                        point.z = value.z;
                    }
                }));
            form.Covers(path + ".position", path + ".x", path + ".y", path + ".z");
            box.Add(form.Float(
                "Wait Seconds",
                path + ".waitSeconds",
                entry => PatrolPointAt(entry, position)?.waitSeconds ?? 0f,
                (entry, value) =>
                {
                    var point = PatrolPointAt(entry, position);
                    if (point != null)
                    {
                        point.waitSeconds = value;
                    }
                }));
            var buttons = new VisualElement();
            buttons.AddToClassList(ContentToolStyles.Row);
            var up = new Button(() => MovePatrolPoint(position, -1)) { text = "Move Up" };
            up.SetEnabled(position > 0);
            var down = new Button(() => MovePatrolPoint(position, 1)) { text = "Move Down" };
            down.SetEnabled(position < count - 1);
            buttons.Add(up);
            buttons.Add(down);
            buttons.Add(new Button(() => RemovePatrolPoint(position)) { text = "Remove" });
            box.Add(buttons);
            return box;
        }

        // The prefab mapped to the placed actor's presentation archetype, as runtime shows it.
        private VisualElement BuildPresentationPreview()
        {
            var section = ContentToolStyles.CreateSection("Presentation Preview");
            var missing = new HelpBox(string.Empty, HelpBoxMessageType.Warning);
            var prefabField = new ObjectField("Mapped Prefab") { objectType = typeof(GameObject), allowSceneObjects = false };
            prefabField.SetEnabled(false);
            var image = new Image { scaleMode = ScaleMode.ScaleToFit };
            image.AddToClassList("content-prefab-preview");
            IVisualElementScheduledItem pending = null;
            form.Watch(() =>
            {
                pending?.Pause();
                var actor = form.Targets.Count == 1 ? ActorOf(form.Targets[0]) : null;
                var prefab = actor == null ? null : ResolvePresentationPrefab(actor.presentationArchetypeId);
                missing.style.display = actor != null && prefab == null ? DisplayStyle.Flex : DisplayStyle.None;
                missing.text = actor == null
                    ? string.Empty
                    : "No prefab mapping was found for presentation archetype '" + actor.presentationArchetypeId
                        + "'. Runtime will use the explicit development fallback.";
                prefabField.SetValueWithoutNotify(prefab);
                prefabField.style.display = prefab == null ? DisplayStyle.None : DisplayStyle.Flex;
                image.image = null;
                image.style.display = prefab == null ? DisplayStyle.None : DisplayStyle.Flex;
                if (prefab != null)
                {
                    // Asset previews render asynchronously, so the image waits for its texture.
                    pending = image.schedule.Execute(() => image.image = AssetPreview.GetAssetPreview(prefab) ?? AssetPreview.GetMiniThumbnail(prefab))
                        .Every(200)
                        .Until(() => !AssetPreview.IsLoadingAssetPreview(prefab.GetEntityId()));
                }
            });
            section.Add(missing);
            section.Add(prefabField);
            section.Add(image);
            return section;
        }

        private void MovePatrolPoint(int position, int offset)
        {
            var patrol = SinglePatrolPath();
            var target = position + offset;
            if (patrol?.points == null || target < 0 || target >= patrol.points.Length)
            {
                return;
            }

            session.Change(offset < 0 ? "Patrol point moved up" : "Patrol point moved down", () =>
            {
                var points = patrol.points.ToArray();
                (points[position], points[target]) = (points[target], points[position]);
                patrol.points = points;
            });
        }

        private void RemovePatrolPoint(int position)
        {
            var patrol = SinglePatrolPath();
            if (patrol?.points == null || position >= patrol.points.Length)
            {
                return;
            }

            session.Change("Patrol point removed", () =>
                patrol.points = patrol.points.Where((_, index) => index != position).ToArray());
        }

        private WorldActorEditorPatrolPath SinglePatrolPath()
        {
            return form?.Targets.Count == 1 ? form.Targets[0].PatrolPath : null;
        }

        private static WorldActorEditorPatrolPoint PatrolPointAt(WorldActorSpawnEntry entry, int position)
        {
            var points = entry.PatrolPath?.points;
            return points != null && position < points.Length ? points[position] : null;
        }

        private static void DrawEntry(WorldActorSpawnEntry entry, bool selected)
        {
            var thickness = selected ? 3f : 1f;
            switch (entry.Kind)
            {
                case WorldActorSpawnEntryKind.Point:
                    Handles.color = new Color(0.2f, 0.8f, 1f, 1f);
                    Handles.DrawWireDisc(entry.Position, Vector3.up, 0.5f, thickness);
                    Handles.Label(entry.Position + Vector3.up, entry.Id + "\n" + entry.ActorId);
                    break;
                case WorldActorSpawnEntryKind.Group:
                    Handles.color = new Color(0.2f, 1f, 0.4f, 1f);
                    foreach (var position in Positions(entry))
                    {
                        Handles.DrawWireDisc(position, Vector3.up, 0.4f, thickness);
                    }

                    Handles.Label(entry.Position + Vector3.up, entry.Id + "\n" + entry.ActorId);
                    break;
                case WorldActorSpawnEntryKind.Area:
                    Handles.color = new Color(1f, 0.7f, 0.15f, 1f);
                    var area = entry.Area;
                    var corners = new[]
                    {
                        new Vector3(area.minimumX, area.y, area.minimumZ),
                        new Vector3(area.maximumX, area.y, area.minimumZ),
                        new Vector3(area.maximumX, area.y, area.maximumZ),
                        new Vector3(area.minimumX, area.y, area.maximumZ),
                        new Vector3(area.minimumX, area.y, area.minimumZ)
                    };
                    Handles.DrawAAPolyLine(thickness * 2f, corners);
                    Handles.Label(entry.Position + Vector3.up, entry.Id + " x" + area.count + "\n" + entry.ActorId);
                    break;
                default:
                    Handles.color = selected ? Color.magenta : new Color(0.8f, 0.3f, 0.8f, 1f);
                    var points = Positions(entry).ToArray();
                    for (var index = 0; index < points.Length; index++)
                    {
                        Handles.SphereHandleCap(0, points[index], Quaternion.identity, 0.25f, EventType.Repaint);
                    }

                    if (points.Length > 1)
                    {
                        Handles.DrawAAPolyLine(thickness * 2f, points);
                    }

                    if (points.Length > 0)
                    {
                        Handles.Label(points[0] + Vector3.up, entry.Id);
                    }

                    break;
            }
        }

        // The positions an entry occupies: a group's grid as the compiler lays it out, an
        // area's corners, or a patrol path's points.
        private static IEnumerable<Vector3> Positions(WorldActorSpawnEntry entry)
        {
            switch (entry.Kind)
            {
                case WorldActorSpawnEntryKind.Group:
                    var group = entry.Group;
                    var rows = Math.Max(1, group.rows);
                    var columns = Math.Max(1, group.columns);
                    var halfWidth = (columns - 1) * group.spacing * 0.5f;
                    var halfDepth = (rows - 1) * group.spacing * 0.5f;
                    for (var row = 0; row < rows; row++)
                    {
                        for (var column = 0; column < columns; column++)
                        {
                            yield return new Vector3(
                                group.centerX + (column * group.spacing) - halfWidth,
                                group.centerY,
                                group.centerZ + (row * group.spacing) - halfDepth);
                        }
                    }

                    break;
                case WorldActorSpawnEntryKind.Area:
                    yield return new Vector3(entry.Area.minimumX, entry.Area.y, entry.Area.minimumZ);
                    yield return new Vector3(entry.Area.maximumX, entry.Area.y, entry.Area.maximumZ);
                    break;
                case WorldActorSpawnEntryKind.PatrolPath:
                    foreach (var point in entry.PatrolPath.points ?? Array.Empty<WorldActorEditorPatrolPoint>())
                    {
                        yield return new Vector3(point.x, point.y, point.z);
                    }

                    break;
                default:
                    yield return entry.Position;
                    break;
            }
        }

        // A field of X and Z on the ground plane labels its second value Z.
        private static Vector2Field GroundPlane(Vector2Field field)
        {
            var parts = field.Query<FloatField>().ToList();
            if (parts.Count == 2)
            {
                parts[1].label = "Z";
            }

            return field;
        }

        private static float? RaycastGround(Vector3 position)
        {
            var origin = position + (Vector3.up * GroundProbeHeight);
            return Physics.Raycast(origin, Vector3.down, out var hit, GroundProbeHeight * 2f, Physics.AllLayers, QueryTriggerInteraction.Ignore)
                ? hit.point.y
                : (float?)null;
        }

        private static Vector3 ScenePivot()
        {
            var pivot = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : Vector3.zero;
            return new Vector3(Round(pivot.x), Round(pivot.y), Round(pivot.z));
        }

        private static float Round(float value)
        {
            return Mathf.Round(value * 100f) / 100f;
        }

        private static TValue[] Append<TValue>(TValue[] values, TValue value)
        {
            return (values ?? Array.Empty<TValue>()).Concat(new[] { value }).ToArray();
        }

        private WorldActorEditorDefinition ActorOf(WorldActorSpawnEntry entry)
        {
            return entry.PlacesActors
                ? workspace?.Catalog.actors?.FirstOrDefault(actor => string.Equals(actor?.id, entry.ActorId, StringComparison.Ordinal))
                : null;
        }

        private WorldActorEditorRespawnProfile RespawnProfileOf(WorldActorSpawnEntry entry)
        {
            var actor = ActorOf(entry);
            return IsMob(actor)
                ? workspace.Catalog.respawnProfiles?.FirstOrDefault(profile => string.Equals(profile?.id, actor.respawnProfileId, StringComparison.Ordinal))
                : null;
        }

        private static bool IsMob(WorldActorEditorDefinition actor)
        {
            return actor != null && actor.kind == WorldActorKindIds.Mob;
        }

        private string FirstActorId()
        {
            return workspace.Catalog.actors?.FirstOrDefault()?.id ?? string.Empty;
        }

        // The prefix most spawn ids of this World share, such as "local".
        private string IdPrefix()
        {
            return WorldActorSpawnEntry.List(workspace.Spawns)
                .Select(entry => entry.Id ?? string.Empty)
                .Where(id => id.IndexOf('.') > 0)
                .GroupBy(id => id.Substring(0, id.IndexOf('.')))
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key)
                .FirstOrDefault() ?? "local";
        }

        private HashSet<string> SpawnIds()
        {
            return new HashSet<string>(
                WorldActorSpawnEntry.List(workspace.Spawns).Where(entry => entry.PlacesActors).Select(entry => entry.Id).Where(id => id != null),
                StringComparer.Ordinal);
        }

        private HashSet<string> PatrolPathIds()
        {
            return new HashSet<string>(
                (workspace.Spawns.patrolPaths ?? Array.Empty<WorldActorEditorPatrolPath>()).Select(path => path?.id).Where(id => id != null),
                StringComparer.Ordinal);
        }

        private string CreateUniqueSpawnId(string candidate)
        {
            return CreateUnique(candidate, SpawnIds());
        }

        private string CreateUniquePatrolPathId(string candidate)
        {
            return CreateUnique(candidate, PatrolPathIds());
        }

        private static string CreateUnique(string candidate, ICollection<string> existing)
        {
            var value = candidate;
            var suffix = 2;
            while (existing.Contains(value))
            {
                value = candidate + "_" + suffix;
                suffix++;
            }

            return value;
        }

        private void ShowIssue(ContentIssue issue)
        {
            if (!WorldActorEditorIssues.TrySplitKey(issue.EntityKey, out var collection, out _))
            {
                return;
            }

            if (!WorldActorEditorIssues.IsSpawnCollection(collection))
            {
                if (collection == WorldActorEditorIssues.Actors)
                {
                    toolLayout.SetStatus("Actor definitions are edited in Actor Studio.", HelpBoxMessageType.Info);
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

        private ContentIssueSeverity? IssueState(WorldActorSpawnEntry entry)
        {
            var key = entry.Key;
            var related = issues.Where(issue => string.Equals(issue.EntityKey, key, StringComparison.Ordinal)).ToArray();
            if (related.Length == 0)
            {
                return null;
            }

            return related.Any(issue => issue.Severity == ContentIssueSeverity.Error)
                ? ContentIssueSeverity.Error
                : ContentIssueSeverity.Warning;
        }

        private string Subtitle(WorldActorSpawnEntry entry)
        {
            if (!entry.PlacesActors)
            {
                return entry.KindLabel + " | " + (entry.PatrolPath.points?.Length ?? 0) + " points";
            }

            var actor = ActorOf(entry);
            return entry.KindLabel + " | " + (actor?.displayName ?? entry.ActorId) + " | x" + entry.InstanceCount;
        }

        private IEnumerable<(string Value, string Label)> ActorOptions()
        {
            return (workspace?.Catalog.actors ?? Array.Empty<WorldActorEditorDefinition>())
                .Where(actor => actor != null)
                .Select(actor => (actor.id, actor.displayName + " (" + actor.id + ")"));
        }

        private IEnumerable<(string Value, string Label)> PatrolPathOptions()
        {
            return new[] { (string.Empty, "None") }
                .Concat((workspace?.Spawns.patrolPaths ?? Array.Empty<WorldActorEditorPatrolPath>())
                    .Where(path => path != null)
                    .Select(path => (path.id, path.id)));
        }

        private static bool ConfirmChanges(string[] changes)
        {
            return EditorUtility.DisplayDialog(
                "Compile Actor Spawns",
                string.Join("\n", changes) + "\n\nWrite deterministic authoring and runtime files?",
                "Write files",
                "Cancel");
        }

        private static GameObject ResolvePresentationPrefab(string presentationArchetypeId)
        {
            var registryPaths = new[] { WorldActorStudioView.PresentationRegistryPath }
                .Concat(AssetDatabase.FindAssets("t:WorldActorPresentationRegistry").Select(AssetDatabase.GUIDToAssetPath))
                .Distinct(StringComparer.Ordinal);
            foreach (var path in registryPaths)
            {
                var registry = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                var entries = registry == null ? null : new SerializedObject(registry).FindProperty("entries");
                if (entries == null || !entries.isArray)
                {
                    continue;
                }

                for (var index = 0; index < entries.arraySize; index++)
                {
                    var entry = entries.GetArrayElementAtIndex(index);
                    if (string.Equals(entry.FindPropertyRelative("presentationArchetypeId")?.stringValue, presentationArchetypeId, StringComparison.Ordinal))
                    {
                        return entry.FindPropertyRelative("prefab")?.objectReferenceValue as GameObject;
                    }
                }
            }

            return null;
        }
    }
}
