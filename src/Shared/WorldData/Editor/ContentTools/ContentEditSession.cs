using System;
using System.Collections.Generic;
using UnityEditor;

namespace Mmorpg.WorldData.Editor.ContentTools
{
    // Applies an edit to the content of a tool as one undoable step.
    public interface IContentEditor
    {
        void Change(string label, Action edit);
    }

    // Combines edits made on the base content with the content on disk now, entry by entry,
    // and names the entries whose edits gave way because the disk changed them too.
    public delegate T ContentMerge<T>(T baseContent, T edited, T disk, ICollection<string> conflicts);

    // What a load did with edits that were not saved before a script reload.
    public enum ContentPendingEdits
    {
        // There were none, or the load discarded them on purpose.
        None,

        // They were made on the content on disk and apply again.
        Restored,

        // The content on disk changed since they were made; they apply on top of it, except
        // for the entries that it changed too.
        Merged,

        // The content on disk changed since they were made, and they gave way to it.
        Dropped
    }

    // Tracks edits to the content of a tool. Every change becomes one step in Unity's Undo,
    // recorded as an exact snapshot, and undo or redo restores the content from that snapshot.
    // The session also knows whether the content differs from what was loaded or saved, and
    // keeps that base with the snapshot, so edits that survive a script reload never apply on
    // top of content that changed on disk since, such as after a git pull.
    public sealed class ContentEditSession<T> : IContentEditor, IDisposable
        where T : class
    {
        private readonly ContentUndoState state;
        private readonly Func<T> capture;
        private readonly Action<T> restore;
        private readonly ContentMerge<T> merge;
        private string appliedSnapshot = string.Empty;
        private string savedSnapshot = string.Empty;
        private bool disposed;

        // Without merge, edits on content that changed on disk give way to the disk.
        public ContentEditSession(ContentUndoState state, Func<T> capture, Action<T> restore, ContentMerge<T> merge = null)
        {
            this.state = state ? state : throw new ArgumentNullException(nameof(state));
            this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
            this.restore = restore ?? throw new ArgumentNullException(nameof(restore));
            this.merge = merge;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
        }

        // Raised after an edit, an undo or redo, or a load changed the content.
        public event Action Changed;

        public bool HasUnsavedChanges => !string.Equals(appliedSnapshot, savedSnapshot, StringComparison.Ordinal);

        // What the last load did with edits from before a script reload, and the entries whose
        // edits gave way to the disk.
        public ContentPendingEdits PendingEdits { get; private set; }

        public IReadOnlyList<string> Conflicts { get; private set; } = Array.Empty<string>();

        // The status line's words for what the last load did with earlier edits, with a
        // leading space, or empty.
        public string PendingEditsMessage
        {
            get
            {
                switch (PendingEdits)
                {
                    case ContentPendingEdits.Restored:
                        return " Unsaved edits from before the script reload were restored.";
                    case ContentPendingEdits.Merged:
                        return Conflicts.Count == 0
                            ? " Unsaved edits from before the script reload were applied on top of the files that changed on disk."
                            : " Unsaved edits from before the script reload were applied on top of the files that changed on disk, except for "
                                + string.Join(", ", Conflicts) + ", which the files changed too.";
                    case ContentPendingEdits.Dropped:
                        return " Unsaved edits from before the script reload were not restored, because the files changed on disk since.";
                    default:
                        return string.Empty;
                }
            }
        }

        // True when edits from before a script reload gave way, in whole or in part.
        public bool PendingEditsGaveWay =>
            PendingEdits == ContentPendingEdits.Dropped
            || (PendingEdits == ContentPendingEdits.Merged && Conflicts.Count > 0);

        // Starts from content that was just loaded from disk. With keepPendingEdits, edits that
        // were not saved before a script reload apply again: as they were when the disk still
        // holds the content they were made on, otherwise merged with the disk, or not at all
        // without a merge.
        public ContentPendingEdits Load(bool keepPendingEdits)
        {
            var loaded = capture();
            savedSnapshot = ContentSnapshot.Capture(loaded);
            Conflicts = Array.Empty<string>();
            PendingEdits = ContentPendingEdits.None;
            if (keepPendingEdits
                && state.HasPendingEdits
                && !string.Equals(state.Snapshot, savedSnapshot, StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(state.BaseSnapshot)
                    || string.Equals(state.BaseSnapshot, savedSnapshot, StringComparison.Ordinal))
                {
                    // The undo history still fits the content, so it stays.
                    PendingEdits = ContentPendingEdits.Restored;
                    appliedSnapshot = state.Snapshot;
                }
                else if (merge != null)
                {
                    var conflicts = new List<string>();
                    var merged = merge(
                        ContentSnapshot.Restore<T>(state.BaseSnapshot),
                        ContentSnapshot.Restore<T>(state.Snapshot),
                        ContentSnapshot.Restore<T>(savedSnapshot),
                        conflicts);
                    PendingEdits = ContentPendingEdits.Merged;
                    Conflicts = conflicts.ToArray();
                    appliedSnapshot = ContentSnapshot.Capture(merged);
                    Undo.ClearUndo(state);
                }
                else
                {
                    PendingEdits = ContentPendingEdits.Dropped;
                    appliedSnapshot = savedSnapshot;
                    Undo.ClearUndo(state);
                }
            }
            else
            {
                Undo.ClearUndo(state);
                appliedSnapshot = savedSnapshot;
            }

            if (!string.Equals(appliedSnapshot, savedSnapshot, StringComparison.Ordinal))
            {
                restore(ContentSnapshot.Restore<T>(appliedSnapshot));
            }

            state.Snapshot = appliedSnapshot;
            state.BaseSnapshot = savedSnapshot;
            Changed?.Invoke();
            return PendingEdits;
        }

        public void Change(string label, Action edit)
        {
            Record(label, edit, true);
        }

        // An edit that continues the current undo step, such as every frame of a handle drag.
        // Unity starts a new step on the next mouse or key press, so one drag undoes at once.
        public void ChangeContinuously(string label, Action edit)
        {
            Record(label, edit, false);
        }

        // The content as it is now was written to disk. A save may also normalize the content,
        // so the snapshot is taken again without an undo step.
        public void MarkSaved()
        {
            appliedSnapshot = ContentSnapshot.Capture(capture());
            state.Snapshot = appliedSnapshot;
            state.BaseSnapshot = appliedSnapshot;
            savedSnapshot = appliedSnapshot;
            Changed?.Invoke();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
        }

        private void Record(string label, Action edit, bool newStep)
        {
            if (edit == null)
            {
                throw new ArgumentNullException(nameof(edit));
            }

            edit();
            var next = ContentSnapshot.Capture(capture());
            if (string.Equals(next, appliedSnapshot, StringComparison.Ordinal))
            {
                return;
            }

            // The undo state still holds the previous snapshot, which Undo records as the step
            // to return to. A new group keeps two edits in one frame as two steps.
            if (newStep)
            {
                Undo.IncrementCurrentGroup();
            }

            Undo.RecordObject(state, label);
            state.Snapshot = next;
            appliedSnapshot = next;
            Changed?.Invoke();
        }

        private void OnUndoRedoPerformed()
        {
            if (!state)
            {
                return;
            }

            // Undo restores the whole state as recorded, also a base from before the last
            // save, so the base is the saved content again.
            state.BaseSnapshot = savedSnapshot;
            if (string.IsNullOrEmpty(state.Snapshot)
                || string.Equals(state.Snapshot, appliedSnapshot, StringComparison.Ordinal))
            {
                return;
            }

            appliedSnapshot = state.Snapshot;
            restore(ContentSnapshot.Restore<T>(appliedSnapshot));
            Changed?.Invoke();
        }
    }
}
