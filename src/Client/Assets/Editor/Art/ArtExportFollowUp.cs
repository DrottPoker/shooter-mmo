using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // After an export reaches the project, creates the project materials it lacks and updates the
    // appearance catalogs, so no menu command is needed between Export-ArtAsset.ps1 and the game.
    // It runs once after the import that brought the models, their data, or textures, so a garment
    // whose masks change without its model changes its catalogs too. In batch mode, such as the CI
    // test run, it does nothing, so the tests check the committed materials and catalogs. See
    // docs/ART_PIPELINE.md.
    public sealed class ArtExportFollowUp : AssetPostprocessor
    {
        private static bool scheduled;

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (scheduled || Application.isBatchMode
                || !imported.Any(path => ArtModelImportRules.AppliesTo(path) || ArtModelImportRules.AppliesToData(path)
                    || ArtModelImportRules.AppliesToTexture(path)))
            {
                return;
            }

            // Assets are written after the import, not inside it.
            scheduled = true;
            EditorApplication.delayCall += Run;
        }

        private static void Run()
        {
            scheduled = false;
            var created = ArtMaterialCreator.CreateMissingMaterials();
            if (created.Count > 0)
            {
                Debug.Log("Created the project materials " + string.Join(", ", created) + " for the exported art.");
            }

            var dyed = ArtMaterialCreator.GiveDyesToGarmentMaterials();
            if (dyed.Count > 0)
            {
                Debug.Log("Gave the garment materials " + string.Join(", ", dyed) + " the dyes of their textures.");
            }

            ArtAppearanceCatalogs.UpdateAll();
        }
    }
}
