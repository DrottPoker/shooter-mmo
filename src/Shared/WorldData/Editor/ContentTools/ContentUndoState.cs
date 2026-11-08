using UnityEngine;

namespace Mmorpg.WorldData.Editor.ContentTools
{
    // Holds the latest snapshot of an edit session, so Unity's Undo records every change as
    // the snapshot before and after it, and the snapshot of the content as it was last loaded or
    // saved, which the edits build on. The owning window keeps a serialized reference, so unsaved
    // edits and their undo history survive script reloads.
    public sealed class ContentUndoState : ScriptableObject
    {
        [SerializeField]
        private string snapshot = string.Empty;

        [SerializeField]
        private string baseSnapshot = string.Empty;

        public string Snapshot
        {
            get => snapshot;
            set => snapshot = value ?? string.Empty;
        }

        public string BaseSnapshot
        {
            get => baseSnapshot;
            set => baseSnapshot = value ?? string.Empty;
        }

        // Edits that were not saved: the latest snapshot differs from the one it builds on. A
        // state from before the base was kept has no base, and any snapshot counts as edits.
        public bool HasPendingEdits =>
            !string.IsNullOrEmpty(snapshot) && !string.Equals(snapshot, baseSnapshot, System.StringComparison.Ordinal);

        public static ContentUndoState Create(string name)
        {
            var state = CreateInstance<ContentUndoState>();
            state.name = name;
            state.hideFlags = HideFlags.HideAndDontSave;
            return state;
        }
    }
}
