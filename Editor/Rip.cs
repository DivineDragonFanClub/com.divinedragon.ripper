using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DivineDragon
{
    public enum InputMode
    {
        File,
        Folder
    }

    public class Rip
    {
        public static bool RunAssetRipper(string assetRipperPath, string inputPath, string outputPath, InputMode mode)
        {
            using (AssetRipperRunner ripperRunner = new AssetRipperRunner(assetRipperPath))
            {
                Debug.Log($"AssetRipper running: {ripperRunner.Running}");

                bool success = ripperRunner.SetDefaultUnityVersion();
                Debug.Log($"SetDefaultUnityVersion success: {success}");
                if (!success) return false;

                if (mode == InputMode.File)
                {
                    success = ripperRunner.AddFile(inputPath);
                    Debug.Log($"AddFile success: {success}");
                }
                else
                {
                    success = ripperRunner.LoadFolder(inputPath);
                    Debug.Log($"LoadFolder success: {success}");
                }
                if (!success) return false;

                success = ripperRunner.ExportProject(outputPath);
                Debug.Log($"Export success: {success}");

                return success;
            }
        }

        // TODO: Remove the requirement for a output path, this should determine itself or use a temporary path.
        public static bool ExtractAssets(string inputPath, InputMode mode)
        {
            // Leaving this here for now until the to-do is addressed
            string inputName = Path.GetFileNameWithoutExtension(inputPath);
            string exportPath = CreatePersistentExportFolder(inputName);
            
            bool exportSucceeded = RunAssetRipper(EditorPrefs.GetString(GUI.Settings.DivineRipperSettingsProvider.AssetRipperPathKey, ""), inputPath, exportPath, mode);
            
            if (!exportSucceeded)
            {
                return false;
            }

            RememberExportPath(exportPath);
            
            bool mergeSucceeded = MergeExtractedAssets(exportPath);
            return mergeSucceeded;
        }

        internal static bool MergeExtractedAssets(string ripperOutputPath)
        {
            try
            {
                string sourceAssetsPath = Path.Combine(ripperOutputPath, "ExportedProject", "Assets");

                if (!Directory.Exists(sourceAssetsPath))
                {
                    Debug.LogError($"AssetRipper output Assets folder not found: {sourceAssetsPath}");
                    return false;
                }

                string projectAssetsPath = Application.dataPath;

                var plan = SyncOperationPlanner.BuildPlan(sourceAssetsPath, projectAssetsPath);
                var operations = plan.Operations;

                SyncOperationRunner.Run(projectAssetsPath, sourceAssetsPath, operations, plan.DirectoriesToCreate, plan.StubScriptMappings);

                var syncReport = GuidSyncReport.CreateFromOperations(operations);

                if (syncReport != null && (syncReport.Mappings.Count > 0 || syncReport.NewFilesImported.Count > 0 || syncReport.SkippedFiles.Count > 0))
                {
                    EditorApplication.delayCall += () => GuidSyncReportWindow.ShowReport(syncReport);
                }

                var newFileCount = syncReport?.NewFilesImported.Count ?? 0;
                var skippedCount = syncReport?.SkippedFiles.Count ?? 0;

                Debug.Log($"Assets merged into project: {newFileCount} new files imported, {skippedCount} existing files skipped");
                AssetDatabase.Refresh();
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Failed to copy extracted assets: {ex.Message}");
                return false;
            }
        }
        
        // Do we actually need to persist? Especially in the directory?
        // Wouldn't it be better to generate a temporary directory and delete it when we're finished?
        // EDIT: Doge said it's for debugging purposes
        internal static string CreatePersistentExportFolder(string suggestedName)
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
        
        internal static void RememberExportPath(string exportPath)
        {
            string lastPath = EditorPrefs.GetString(DivineRipperWindow.GetLastExportPath(), string.Empty);
            
            if (!string.IsNullOrEmpty(lastPath))
            {
                EditorPrefs.SetString(DivineRipperWindow.GetPreviousExportPath(), lastPath);
            }

            EditorPrefs.SetString(DivineRipperWindow.GetLastExportPath(), exportPath);
        }

        /// <summary>
        /// Generates a random directory name located in the temporary directory of the user's Operating System.
        /// As a result, the path is inconsistent and shouldn't be stored or reused.
        /// </summary>
        /// <returns>Returns a randomized directory path to the user's temporary folder</returns>
        internal static string GenerateTemporaryDirectoryPath()
        {
            return Path.GetTempPath() + Path.GetRandomFileName();
        }
    }
}
