using System;
using UnityEditor;

namespace Mmorpg.WorldData.Editor.ContentTools
{
    // Applies an edit to the content of a tool as one undoable step.
    public interface IContentEditor
    {
        void Change(string label, Action edit);
    }

    // Tracks edits to the content of a tool. Every change becomes one step in Unity's Undo,
    // recorded as an exact snapshot, and undo or redo restores the content from that snapshot.
    // The session also knows whether the content differs from what was loaded or saved.
    public sealed class ContentEditSession<T> : IContentEditor, IDisposable
        where T : class
    {
        private readonly ContentUndoState state;
        private readonly Func<T> capture;
        private readonly Action<T> restore;
        private string appliedSnapshot = string.Empty;
        private string savedSnapshot = string.Empty;
        private bool disposed;

        public ContentEditSession(ContentUndoState state, Func<T> capture, Action<T> restore)
        {
            this.state = state ? state : throw new ArgumentNullException(nameof(state));
            this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
            this.restore = restore ?? throw new ArgumentNullException(nameof(restore));
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
        }

        // Raised after an edit, an undo or redo, or a load changed the content.
        public event Action Changed;

        public bool HasUnsavedChanges => !string.Equals(appliedSnapshot, savedSnapshot, StringComparison.Ordinal);

        // Starts from content that was just loaded from disk. With keepPendingEdits, edits that
        // were not saved before a script reload are applied again on top of the loaded content.
        public void Load(bool keepPendingEdits)
        {
            savedSnapshot = ContentSnapshot.Capture(capture());
            if (keepPendingEdits
                && !string.IsNullOrEmpty(state.Snapshot)
                && !string.Equals(state.Snapshot, savedSnapshot, StringComparison.Ordinal))
            {
                appliedSnapshot = state.Snapshot;
                restore(ContentSnapshot.Restore<T>(appliedSnapshot));
            }
            else
            {
                Undo.ClearUndo(state);
                appliedSnapshot = savedSnapshot;
                state.Snapshot = savedSnapshot;
            }

            Changed?.Invoke();
        }

        public void Change(string label, Action edit)
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
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(state, label);
            state.Snapshot = next;
            appliedSnapshot = next;
            Changed?.Invoke();
        }

        // The content as it is now was written to disk. A save may also normalize the content,
        // so the snapshot is taken again without an undo step.
        public void MarkSaved()
        {
            appliedSnapshot = ContentSnapshot.Capture(capture());
            state.Snapshot = appliedSnapshot;
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

        private void OnUndoRedoPerformed()
        {
            if (!state
                || string.IsNullOrEmpty(state.Snapshot)
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
