using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using DivineDragon.GUI.Settings; // We probably want to keep the settings in a dedicated class with accessors or something

namespace DivineDragon
{
    public class DivineRipperWindow
    {
        private Color cobaltBlue = new Color(0.0f, 0.28f, 0.67f, 1.0f);

        private const string LastExportPathKey = "DivineDragon.DivineRipper.LastExportPath";
        private const string PreviousExportPathKey = "DivineDragon.DivineRipper.PreviousExportPath";

        public static string GetLastExportPath()
        {
            return EditorPrefs.GetString(LastExportPathKey, string.Empty);
        }

        public static string GetPreviousExportPath()
        {
            return EditorPrefs.GetString(PreviousExportPathKey, string.Empty);
        }

        // Commented out because if things work as they should, you wouldn't need to gather and load a folder to begin with.
        // Worth re-enabling if maps prove to be too complex to support for now.
        #region Extract a folder
        
        // // Validator for ExtractFolder
        // [MenuItem("Divine Dragon/Divine Ripper/Extract a folder", true)]
        // private static bool ValidateExtractFolder()
        // {
        //     // Disable the MenuItem if the user hasn't provided the path to AssetRipper
        //     return !string.IsNullOrEmpty(EditorPrefs.GetString(DivineRipperSettingsProvider.AssetRipperPathKey, ""));
        // }
        
        // [MenuItem("Divine Dragon/Divine Ripper/Extract a folder", false, 1401)]
        // public static void ExtractFolder()
        // {
        //     string path = EditorUtility.OpenFolderPanel(
        //         "Select folder to extract",
        //         "",
        //         ""
        //     );
        //
        //     if (string.IsNullOrEmpty(path))
        //         return;
        //
        //     string folderName = new DirectoryInfo(path).Name;
        //     string exportPath = CreatePersistentExportFolder(folderName);
        //
        //     EditorUtility.DisplayProgressBar("AssetRipper", "Starting AssetRipper...", 0.1f);
        //
        //     try
        //     {
        //         bool success = Rip.ExtractAssets(
        //             path,
        //             exportPath,
        //             InputMode.Folder);
        //
        //         if (success)
        //         {
        //             EditorUtility.DisplayDialog("Success",
        //                 $"Assets extracted successfully. Export kept at:\n{exportPath}",
        //                 "OK");
        //         }
        //         else
        //         {
        //             EditorUtility.DisplayDialog("Error",
        //                 "Failed to extract assets. Check the console for details.",
        //                 "OK");
        //         }
        //     }
        //     catch (Exception ex)
        //     {
        //         Debug.LogError($"Error running AssetRipper: {ex.Message}");
        //
        //         EditorUtility.DisplayDialog("Error",
        //             $"Error running AssetRipper:\n{ex.Message}",
        //             "OK");
        //     }
        //     finally
        //     {
        //         EditorUtility.ClearProgressBar();
        //     }
        // }
        
        #endregion
        
        [MenuItem("Divine Dragon/Dumper/Settings", false, 1402)]
        public static void ShowDivineRipperSettings()
        {
            SettingsService.OpenProjectSettings("Project/Divine Dragon/Divine Ripper Settings");
        }
    }
}
