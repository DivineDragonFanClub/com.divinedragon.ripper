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
        public static bool RunAssetRipper(string assetRipperPath, string inputPath, string outputPath, InputMode mode, bool forceImport = false)
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

        public static bool ExtractAssets(string assetRipperPath, string inputPath, string outputPath, InputMode mode, bool forceImport = false)
        {
            bool exportSucceeded = RunAssetRipper(assetRipperPath, inputPath, outputPath, mode, forceImport);
            if (!exportSucceeded)
            {
                return false;
            }

            bool mergeSucceeded = MergeExtractedAssets(outputPath, forceImport);
            return mergeSucceeded;
        }

        private static bool MergeExtractedAssets(string ripperOutputPath, bool forceImport)
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

                var plan = SyncOperationPlanner.BuildPlan(sourceAssetsPath, projectAssetsPath, forceImport);
                var operations = plan.Operations;

                SyncOperationRunner.Run(projectAssetsPath, sourceAssetsPath, operations, plan.DirectoriesToCreate, plan.StubScriptMappings, forceImport);

                var syncReport = GuidSyncReport.CreateFromOperations(operations);

                if (syncReport != null && (syncReport.Mappings.Count > 0 || syncReport.NewFilesImported.Count > 0 || syncReport.SkippedFiles.Count > 0))
                {
                    EditorApplication.delayCall += () => GuidSyncReportWindow.ShowReport(syncReport);
                }

                var newFileCount = syncReport?.NewFilesImported.Count ?? 0;
                var skippedCount = syncReport?.SkippedFiles.Count ?? 0;

                Debug.Log($"Assets merged into project: {newFileCount} new files imported, {skippedCount} existing files skipped");
                if (forceImport && skippedCount > 0)
                {
                    Debug.Log("Force import enabled - all files were overwritten");
                }

                AssetDatabase.Refresh();
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Failed to copy extracted assets: {ex.Message}");
                return false;
            }
        }

    }
}
