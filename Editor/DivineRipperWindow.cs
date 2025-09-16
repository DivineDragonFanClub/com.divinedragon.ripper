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

            // Create temp folder with unique GUID
            string tempOutputPath = Path.Combine(Path.GetTempPath(), "AssetRipperOutput_" + System.Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempOutputPath);

            EditorUtility.DisplayProgressBar("AssetRipper", "Starting AssetRipper...", 0.1f);

            try
            {
                bool success = Rip.RunAssetRipper(
                    EditorPrefs.GetString(DivineRipperSettingsProvider.AssetRipperPathKey, ""),
                    path,
                    tempOutputPath,
                    InputMode.File,
                    false);

                if (success)
                {
                    EditorUtility.DisplayDialog("Success",
                        $"Assets extracted successfully",
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

                // Clean up temp folder
                try
                {
                    if (Directory.Exists(tempOutputPath))
                    {
                        Directory.Delete(tempOutputPath, true);
                        Debug.Log($"Cleaned up temporary extraction folder: {tempOutputPath}");
                    }
                }
                catch (Exception cleanupEx)
                {
                    Debug.LogWarning($"Failed to clean up temp folder: {cleanupEx.Message}");
                }
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

            // Create temp folder with unique GUID
            string tempOutputPath = Path.Combine(Path.GetTempPath(), "AssetRipperOutput_" + System.Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempOutputPath);

            EditorUtility.DisplayProgressBar("AssetRipper", "Starting AssetRipper...", 0.1f);

            try
            {
                bool success = Rip.RunAssetRipper(
                    EditorPrefs.GetString(DivineRipperSettingsProvider.AssetRipperPathKey, ""),
                    path,
                    tempOutputPath,
                    InputMode.Folder,
                    false);

                if (success)
                {
                    EditorUtility.DisplayDialog("Success",
                        $"Assets extracted successfully",
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

                // Clean up temp folder
                try
                {
                    if (Directory.Exists(tempOutputPath))
                    {
                        Directory.Delete(tempOutputPath, true);
                        Debug.Log($"Cleaned up temporary extraction folder: {tempOutputPath}");
                    }
                }
                catch (Exception cleanupEx)
                {
                    Debug.LogWarning($"Failed to clean up temp folder: {cleanupEx.Message}");
                }
            }
        }
        
        [MenuItem("Divine Dragon/Divine Ripper/Settings", false, 1402)]
        public static void ShowDivineRipperSettings()
        {
            SettingsService.OpenProjectSettings("Project/Divine Dragon/Divine Ripper Settings");
        }
    }
}