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

                if (success)
                {
                    success = CopyExtractedAssets(outputPath, forceImport);
                }

                return success;
            }
        }

        private static bool CopyExtractedAssets(string ripperOutputPath, bool forceImport)
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

                var combinedResult = CopyAndCollect(sourceAssetsPath, projectAssetsPath, forceImport);

                var syncReport = combinedResult.SyncReport;
                if (syncReport != null && (syncReport.Mappings.Count > 0 || syncReport.NewFilesImported.Count > 0 || syncReport.SkippedFiles.Count > 0))
                {
                    EditorApplication.delayCall += () => GuidSyncReportWindow.ShowReport(syncReport);
                }

                Debug.Log($"Assets merged into project: {combinedResult.NewFiles} new files imported, {combinedResult.SkippedFiles} existing files skipped");
                if (forceImport && combinedResult.SkippedFiles > 0)
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

        private class CopyResult
        {
            public int NewFiles;
            public int SkippedFiles;
            public GuidSyncReport SyncReport;
        }

        private static CopyResult CopyAndCollect(string sourceDir, string targetDir, bool forceImport)
        {
            var plan = SyncOperationPlanner.BuildPlan(sourceDir, targetDir, forceImport);
            var operations = plan.Operations;

            SyncOperationApplier.Apply(targetDir, sourceDir, operations, plan.DirectoriesToCreate, plan.StubScriptMappings, forceImport);

            var report = GuidSyncReport.CreateFromOperations(operations);

            return new CopyResult
            {
                NewFiles = operations.NewFileCount,
                SkippedFiles = operations.SkippedFileCount,
                SyncReport = report
            };
        }

    }
}
