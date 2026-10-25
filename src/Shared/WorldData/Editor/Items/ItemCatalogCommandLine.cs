using System;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.WorldData.Editor.Items
{
    // Bakes the item catalog without the window, as Save And Bake does, for a batch run:
    // Unity -batchmode -projectPath src/Client -executeMethod
    // Mmorpg.WorldData.Editor.Items.ItemCatalogCommandLine.SaveAndBake -quit. The client
    // presentation file is written by Unity's JSON serializer, so only Unity bakes it. Structural
    // changes are baked without asking, as a command line has no one to confirm them.
    public static class ItemCatalogCommandLine
    {
        public static void SaveAndBake()
        {
            var service = new ItemCatalogEditorService(ItemCatalogEditorPaths.CreateDefault());
            var workspace = service.Load();
            var validation = service.Validate(workspace);
            if (!validation.Success)
            {
                Debug.LogError("The item catalog was not baked: " + validation.Message);
                if (Application.isBatchMode)
                {
                    EditorApplication.Exit(1);
                }

                return;
            }

            service.SaveAndBake(workspace, validation);
            AssetDatabase.Refresh();
            Debug.Log("Baked the item catalog: " + validation.Message.Replace(Environment.NewLine, " "));
        }
    }
}
