using System;
using System.Collections.Generic;
using System.IO;
using Dragonstone;
using UnityEditor;
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
            Directory.CreateDirectory(tempDir);

            bool syncDevModeEnabled = GUI.Settings.DivineRipperSettingsProvider.IsSyncDevModeEnabled;
            string exportPath;

            if (syncDevModeEnabled)
            {
                exportPath = Rip.CreatePersistentExportFolder(exportDirectory);
            }
            else
            {
                exportPath = Rip.GenerateTemporaryDirectoryPath();
                Directory.CreateDirectory(exportPath);
            }

            try
            {

                // TODO: Replace the EditorPrefs use with a proper setting system
                using (AssetRipperRunner runner =
                       new AssetRipperRunner(
                           EditorPrefs.GetString(GUI.Settings.DivineRipperSettingsProvider.AssetRipperPathKey, "")))
                {
                    if (!runner.SetDefaultUnityVersion())
                        Debug.LogError("Couldn't set default unity version.");

                    foreach (string file in files)
                    {
                        var relativeFilePath = file.Replace(EngageAddressableSettings.GameBuildPath, null);
                        var copyPath = tempDir + relativeFilePath;
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
                try { Directory.Delete(tempDir, true); }
                catch (Exception ex) { Debug.LogWarning($"Failed to clean up temp dir {tempDir}: {ex.Message}"); }

                if (!syncDevModeEnabled && !string.IsNullOrEmpty(exportPath))
                {
                    try { Directory.Delete(exportPath, true); }
                    catch (Exception ex) { Debug.LogWarning($"Failed to clean up export dir {exportPath}: {ex.Message}"); }
                }
            }
        }
    }
}
