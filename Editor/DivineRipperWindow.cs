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
        
        [MenuItem("Divine Dragon/Divine Ripper/Extract a bundle", false, 1000)]
        public static void ExtractBundle()
        {
            string directory = "";
                
            string path = EditorUtility.OpenFilePanel(
                "Select bundle file to extract",
                directory,
                "bundle"
            );
            
            // Ensure output directory exists
            Directory.CreateDirectory(Path.GetTempPath() + "DivineRipper/");
            
            EditorUtility.DisplayProgressBar("AssetRipper", "Starting AssetRipper...", 0.1f);
            
            try
            {
                bool success = Rip.RunAssetRipper(EditorPrefs.GetString(DivineRipperSettingsProvider.AssetRipperPathKey, ""), path, Path.GetTempPath() + "DivineRipper/");
                
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
            }
        }
        
        [MenuItem("Divine Dragon/Divine Ripper/Settings", false, 1001)]
        public static void ShowDivineRipperSettings()
        {
            SettingsService.OpenProjectSettings("Project/Divine Dragon/Divine Ripper Settings");
        }
    }
}