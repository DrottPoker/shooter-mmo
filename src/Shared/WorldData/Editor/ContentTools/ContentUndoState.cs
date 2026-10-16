using UnityEngine;

namespace Mmorpg.WorldData.Editor.ContentTools
{
    // Holds the latest snapshot of an edit session, so Unity's Undo records every change as
    // the snapshot before and after it. The owning window keeps a serialized reference, so
    // unsaved edits and their undo history survive script reloads.
    public sealed class ContentUndoState : ScriptableObject
    {
        [SerializeField]
        private string snapshot = string.Empty;

        public string Snapshot
        {
            get => snapshot;
            set => snapshot = value ?? string.Empty;
        }

        public static ContentUndoState Create(string name)
        {
            var state = CreateInstance<ContentUndoState>();
            state.name = name;
            state.hideFlags = HideFlags.HideAndDontSave;
            return state;
        }
    }
}
