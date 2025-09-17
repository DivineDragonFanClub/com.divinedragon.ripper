using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        private static GuidSyncReport SynchronizeGuids(string mainProjectAssetsPath, string subordinateAssetsPath)
        {
            try
            {
                var synchronizer = new GuidSynchronizer(mainProjectAssetsPath, subordinateAssetsPath);
                return synchronizer.Synchronize();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"GUID synchronization failed: {ex.Message}");
                return new GuidSyncReport();
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
            var result = new CopyResult();
            var syncReport = new GuidSyncReport();

            foreach (string dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(sourceDir, targetDir));
            }

            foreach (string filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                bool isMetaFile = MetaFileParser.IsMetaFile(filePath);
                string targetFilePath = filePath.Replace(sourceDir, targetDir);
                string unityRelativeTarget = targetFilePath.Replace(Application.dataPath, "Assets").Replace('\\', '/');

                bool targetExists = File.Exists(targetFilePath);
                bool shouldOverwrite = forceImport && targetExists;
                bool shouldCopy = !targetExists || shouldOverwrite;

                if (shouldCopy)
                {
                    File.Copy(filePath, targetFilePath, true);
                }

                if (!isMetaFile)
                {
                    if (!targetExists)
                    {
                        result.NewFiles++;
                        syncReport.AddNewFile(unityRelativeTarget);
                    }
                    else
                    {
                        result.SkippedFiles++;
                        syncReport.AddSkippedFile(unityRelativeTarget);
                    }
                }
            }

            GuidSyncReport fullReport = syncReport;

            if (result.SkippedFiles > 0 && !forceImport)
            {
                Debug.Log($"Found {result.SkippedFiles} existing files - synchronizing GUIDs...");
                fullReport = SynchronizeGuids(targetDir, sourceDir) ?? new GuidSyncReport();

                foreach (var file in syncReport.NewFilesImported)
                {
                    fullReport.AddNewFile(file);
                }

                foreach (var skipped in syncReport.SkippedFiles)
                {
                    fullReport.AddSkippedFile(skipped);
                }

                fullReport.FinalizeReport();
            }
            else
            {
                if (result.SkippedFiles == 0)
                {
                    Debug.Log("All files are new - skipping GUID synchronization");
                }
                else if (forceImport)
                {
                    Debug.Log("Force import enabled - skipping GUID synchronization for overwritten files");
                }

                syncReport.FinalizeReport();
                fullReport = syncReport;
            }

            result.SyncReport = fullReport;
            return result;
        }

        private static string GetRelativePath(string basePath, string fullPath)
        {
            if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
                basePath += Path.DirectorySeparatorChar;

            if (fullPath.StartsWith(basePath))
                return fullPath.Substring(basePath.Length);

            return Path.GetFileName(fullPath);
        }
    }
}
