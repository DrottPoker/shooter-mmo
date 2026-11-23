using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // What follows an export in the Unity project, so no menu command is needed between
    // Export-ArtAsset.ps1 and the game. Apply, the whole follow-up, imports the exported files, creates
    // the project materials they lack, gives the garment materials the dyes of their textures, updates
    // the appearance catalogs with their content and Addressables entries, writes the cloth fixtures
    // when the bodies or garments they capture changed, and saves the assets; tools/art/Update-UnityArt.ps1
    // runs it in the Editor that has the project open, through the Unity CLI, or else in batch mode
    // through ApplyFromCommandLine. An open Editor runs the light part by itself once after the import
    // that brought the models, their data, or textures, also in Play Mode: the materials, the dyes, and
    // the catalogs, without a forced refresh and without capturing the cloth, so a garment whose masks
    // change without its model changes its catalogs too. Both write only what differs, so a run with
    // nothing to do changes no file; any other batch run, such as the CI tests, leaves the committed
    // materials and catalogs as they are. See docs/ART_PIPELINE.md.
    public sealed class ArtExportFollowUp : AssetPostprocessor
    {
        // Every line ApplyFromCommandLine logs starts with it, so Update-UnityArt.ps1 finds them in the
        // Unity log.
        public const string LogMarker = "[ArtFollowUp]";

        private static bool scheduled;

        // What one run wrote.
        public sealed class Result
        {
            public IReadOnlyList<string> CreatedMaterials = Array.Empty<string>();
            public IReadOnlyList<string> DyedMaterials = Array.Empty<string>();
            public int CatalogChanges;
            // Whether the run was the whole follow-up, which also checks the cloth fixtures.
            public bool Whole;
            public bool WroteClothFixtures;
            // The hash of the Addressables settings that the run computed and emptied again, or null.
            public string EmptiedSettingsHash;

            public bool WroteAnything => CreatedMaterials.Count > 0 || DyedMaterials.Count > 0 || CatalogChanges > 0 || WroteClothFixtures;

            // One line per thing it wrote, or one that says nothing needed writing.
            public IEnumerable<string> Lines()
            {
                if (CreatedMaterials.Count > 0)
                {
                    yield return "Created the project materials " + string.Join(", ", CreatedMaterials) + " for the exported art.";
                }

                if (DyedMaterials.Count > 0)
                {
                    yield return "Gave the garment materials " + string.Join(", ", DyedMaterials) + " the dyes of their textures.";
                }

                if (CatalogChanges > 0)
                {
                    yield return "Wrote " + CatalogChanges + " appearance catalog(s), content asset(s), or Addressables change(s).";
                }

                if (WroteClothFixtures)
                {
                    yield return "Wrote the cloth fixtures " + ClothKinematicFixtures.RepositoryPath + "; commit them with the export.";
                }

                if (!WroteAnything)
                {
                    yield return Whole
                        ? "The materials, appearance catalogs, Addressables entries, and cloth fixtures are up to date."
                        : "The materials, appearance catalogs, and Addressables entries are up to date.";
                }

                if (EmptiedSettingsHash != null)
                {
                    yield return "Kept the hash of the Addressables settings empty, as the saved asset has it, instead of "
                        + EmptiedSettingsHash + ", which the run computed.";
                }
            }
        }

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
            EditorApplication.delayCall += RunScheduled;
        }

        private static void RunScheduled()
        {
            scheduled = false;
            var result = Run(false);
            if (result.WroteAnything)
            {
                foreach (var line in result.Lines())
                {
                    Debug.Log(line);
                }
            }
        }

        // The whole follow-up; throws when a step fails, and then has saved what the steps before wrote.
        public static Result Apply()
        {
            return Run(true);
        }

        // The steps of the follow-up; the whole one also refreshes the asset database first and writes
        // the cloth fixtures when they changed. Whatever a step throws reaches the caller after the
        // assets the steps before it wrote are saved.
        private static Result Run(bool whole)
        {
            var result = new Result { Whole = whole };
            var completed = false;
            try
            {
                if (whole)
                {
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                }

                result.CreatedMaterials = ArtMaterialCreator.CreateMissingMaterials();
                result.DyedMaterials = ArtMaterialCreator.GiveDyesToGarmentMaterials();
                result.CatalogChanges = ArtAppearanceCatalogs.UpdateAll();
                if (whole)
                {
                    result.WroteClothFixtures = ClothKinematicFixtures.WriteIfChanged();
                }

                completed = true;
            }
            finally
            {
                try
                {
                    result.EmptiedSettingsHash = KeepSettingsHashEmpty();
                    AssetDatabase.SaveAssets();
                }
                catch (Exception exception) when (!completed)
                {
                    // The exception of the step that failed is the one the caller gets.
                    Debug.LogException(exception);
                }
            }

            return result;
        }

        // For a batch-mode Unity, which tools/art/Update-UnityArt.ps1 starts:
        // -executeMethod Mmorpg.Client.Editor.ArtExportFollowUp.ApplyFromCommandLine. Logs what it wrote
        // and exits with 0, or logs the failure and exits with 1.
        public static void ApplyFromCommandLine()
        {
            var exitCode = 1;
            try
            {
                var result = Apply();
                foreach (var line in result.Lines())
                {
                    Debug.Log(LogMarker + " " + line);
                }

                Debug.Log(LogMarker + " Done.");
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Debug.LogError(LogMarker + " FAILED: " + exception);
            }

            EditorApplication.Exit(exitCode);
        }

        // The Addressables settings keep the hash of themselves in a serialized field, m_currentHash,
        // which the settings' own SetDirty empties and the first read computes again, as finding the
        // entries of the body content does, and their asset is saved with whatever the field holds.
        // The committed asset holds it empty, so settings marked dirty in another way, such as
        // EditorUtility.SetDirty, after the hash was read would be saved with it although no setting
        // changed: the change a headless Unity run can leave in AddressableAssetSettings.asset. An
        // empty hash means "compute it when it is needed", so while the saved asset holds it empty and
        // the settings are about to be saved, the field is emptied again, which is right whether a
        // setting changed or not. Returns the hash it emptied, or null.
        private static string KeepSettingsHashEmpty()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null || !EditorUtility.IsDirty(settings) || !SavedHashIsEmpty(AssetDatabase.GetAssetPath(settings)))
            {
                return null;
            }

            using (var serialized = new SerializedObject(settings))
            {
                var hash = serialized.FindProperty("m_currentHash");
                if (hash == null || !hash.hash128Value.isValid)
                {
                    return null;
                }

                var computed = hash.hash128Value.ToString();
                hash.hash128Value = default;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                return computed;
            }
        }

        // Whether the asset of the settings, as saved, holds an empty hash.
        public static bool SavedHashIsEmpty(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            var match = Regex.Match(File.ReadAllText(path), @"m_currentHash:\s+serializedVersion: \d+\s+Hash: ([0-9a-f]{32})");
            return match.Success && match.Groups[1].Value == new string('0', 32);
        }
    }
}
