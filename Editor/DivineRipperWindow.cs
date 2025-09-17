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

        private static string CreatePersistentExportFolder(string suggestedName)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string exportRoot = Path.Combine(projectRoot, "AssetRipperExports");
            Directory.CreateDirectory(exportRoot);

            string safeName = string.IsNullOrEmpty(suggestedName)
                ? "Export"
                : suggestedName.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_');

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                safeName = safeName.Replace(invalid, '_');
            }

            string stampedFolder = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}";
            string exportPath = Path.Combine(exportRoot, stampedFolder);
            Directory.CreateDirectory(exportPath);

            return exportPath;
        }

        private static void RememberExportPath(string exportPath)
        {
            string lastPath = EditorPrefs.GetString(LastExportPathKey, string.Empty);
            if (!string.IsNullOrEmpty(lastPath))
            {
                EditorPrefs.SetString(PreviousExportPathKey, lastPath);
            }

            EditorPrefs.SetString(LastExportPathKey, exportPath);
        }
        
        // Validator for ExtractBundle
        [MenuItem("Divine Dragon/Divine Ripper/Extract a bundle", true)]
        private static bool ValidateExtractBundle()
        {
            // Disable the MenuItem if the user hasn't provided the path to AssetRipper
            return !string.IsNullOrEmpty(EditorPrefs.GetString(DivineRipperSettingsProvider.AssetRipperPathKey, ""));
        }
        
        [MenuItem("Divine Dragon/Divine Ripper/Extract a bundle", false, 1400)]
        public static void ExtractBundle()
        {
            string directory = "";

            string path = EditorUtility.OpenFilePanel(
                "Select bundle file to extract",
                directory,
                "bundle"
            );

            if (string.IsNullOrEmpty(path))
                return;

            string inputName = Path.GetFileNameWithoutExtension(path);
            string exportPath = CreatePersistentExportFolder(inputName);

            EditorUtility.DisplayProgressBar("AssetRipper", "Starting AssetRipper...", 0.1f);

            try
            {
                bool success = Rip.RunAssetRipper(
                    EditorPrefs.GetString(DivineRipperSettingsProvider.AssetRipperPathKey, ""),
                    path,
                    exportPath,
                    InputMode.File,
                    false);

                if (success)
                {
                    RememberExportPath(exportPath);
                    EditorUtility.DisplayDialog("Success",
                        $"Assets extracted successfully. Export kept at:\n{exportPath}",
                        "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog("Error",
                        "Failed to extract assets. Check the console for details.",
                        "OK");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error running AssetRipper: {ex.Message}");

                EditorUtility.DisplayDialog("Error",
                    $"Error running AssetRipper:\n{ex.Message}",
                    "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        // Validator for ExtractFolder
        [MenuItem("Divine Dragon/Divine Ripper/Extract a folder", true)]
        private static bool ValidateExtractFolder()
        {
            // Disable the MenuItem if the user hasn't provided the path to AssetRipper
            return !string.IsNullOrEmpty(EditorPrefs.GetString(DivineRipperSettingsProvider.AssetRipperPathKey, ""));
        }

        [MenuItem("Divine Dragon/Divine Ripper/Extract a folder", false, 1401)]
        public static void ExtractFolder()
        {
            string path = EditorUtility.OpenFolderPanel(
                "Select folder to extract",
                "",
                ""
            );

            if (string.IsNullOrEmpty(path))
                return;

            string folderName = new DirectoryInfo(path).Name;
            string exportPath = CreatePersistentExportFolder(folderName);

            EditorUtility.DisplayProgressBar("AssetRipper", "Starting AssetRipper...", 0.1f);

            try
            {
                bool success = Rip.RunAssetRipper(
                    EditorPrefs.GetString(DivineRipperSettingsProvider.AssetRipperPathKey, ""),
                    path,
                    exportPath,
                    InputMode.Folder,
                    false);

                if (success)
                {
                    RememberExportPath(exportPath);
                    EditorUtility.DisplayDialog("Success",
                        $"Assets extracted successfully. Export kept at:\n{exportPath}",
                        "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog("Error",
                        "Failed to extract assets. Check the console for details.",
                        "OK");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error running AssetRipper: {ex.Message}");

                EditorUtility.DisplayDialog("Error",
                    $"Error running AssetRipper:\n{ex.Message}",
                    "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
        
        [MenuItem("Divine Dragon/Divine Ripper/Settings", false, 1402)]
        public static void ShowDivineRipperSettings()
        {
            SettingsService.OpenProjectSettings("Project/Divine Dragon/Divine Ripper Settings");
        }
    }
}
