using System;
using System.Linq;
using Mmorpg.WorldData.Editor.ContentTools;

namespace Mmorpg.WorldData.Editor.Actors
{
    // Shows the canonical files of an actor or spawn validation in the shared change preview.
    public static class WorldActorChangePreview
    {
        public static void Show(WorldActorEditorValidationResult validation)
        {
            if (validation == null || !validation.Success)
            {
                throw new ArgumentException(
                    "A successful validation is required for change preview.",
                    nameof(validation));
            }

            ContentChangePreviewWindow.ShowPreview(
                "World Actor Canonical Change Preview",
                validation.Changes,
                validation.RemovedFiles,
                validation.Files.Select(file => new ContentChangePreviewFile(file.DisplayPath, file.Text, file.Changed)));
        }
    }
}
