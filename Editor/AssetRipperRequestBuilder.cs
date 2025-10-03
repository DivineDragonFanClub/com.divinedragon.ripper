using System;
using System.Collections.Generic;
using System.IO;
using Dragonstone;
using UnityEditor;
using UnityEditor.Scripting;
using UnityEngine;

namespace DivineDragon
{
    public class AssetRipperRequestBuilder
    {
        private List<string> files = new List<string>();
        
        public AssetRipperRequestBuilder()
        {
            
        }

        public AssetRipperRequestBuilder AddFile(string filepath)
        {
            // TODO: Ensure the file exists before queuing it.
            files.Add(filepath);
            
            return this;
        }

        public bool Export(string exportDirectory)
        {
            if (string.IsNullOrEmpty(exportDirectory))
            {
                Debug.LogError("Export directory path cannot be null or empty.");
            }
            
            string tempDir = Rip.GenerateTemporaryDirectoryPath();
            Debug.Log($"Temp directory path: {tempDir}");
            Directory.CreateDirectory(tempDir);

            try
            {
                string exportPath = Rip.CreatePersistentExportFolder(exportDirectory);

                // TODO: Replace the EditorPrefs use with a proper setting system
                // Look at BurstEditorOptions?
                using (AssetRipperRunner runner =
                       new AssetRipperRunner(
                           EditorPrefs.GetString(GUI.Settings.DivineRipperSettingsProvider.AssetRipperPathKey, "")))
                {
                    // TODO: We probably shouldn't have to call this ourselves as users. What if we forget?
                    if (!runner.SetDefaultUnityVersion())
                        Debug.LogError("Couldn't set default unity version.");

                    // TODO: Replace with grouping files in a temporary directory, LoadFolder then delete the temporary folder after extraction
                    foreach (string file in files)
                    {
                        var relativeFilePath = file.Replace(EngageAddressableSettings.GameBuildPath, null);
                        Debug.Log(relativeFilePath);
                        var copyPath = tempDir + relativeFilePath;
                        Debug.Log(copyPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(copyPath));
                        File.Copy(file, copyPath);
                    }

                    runner.LoadFolder(tempDir);

                    runner.ExportProject(exportPath);
                }

                Rip.RememberExportPath(exportPath);

                bool mergeSucceeded = Rip.MergeExtractedAssets(exportPath);
                return mergeSucceeded;
            }
            catch (AssetRipperApiException ex)
            {
                Debug.LogError($"AssetRipperRunner ran into an API exception: {ex.Message}");
                return false;
            }
            finally
            {
                // Make sure we delete the temporary directory no matter what when we're done
                Directory.Delete(tempDir, true);
            }
        }
    }
}