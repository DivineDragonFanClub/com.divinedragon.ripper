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

    internal class FileScanResult
    {
        public int NewFilesCount { get; set; }
        public int SkippedFilesCount { get; set; }
        public List<string> NewFilesList { get; set; } = new List<string>();
        public List<string> SkippedFilesList { get; set; } = new List<string>();
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

                var quickReport = new GuidSyncReport();

                // First pass: scan files to determine what needs to be copied and what needs GUID sync
                var scanResult = ScanDirectorySelective(sourceAssetsPath, projectAssetsPath, false);

                foreach (var file in scanResult.NewFilesList)
                {
                    quickReport.AddNewFile(file);
                }

                // Convert skipped files to Unity project paths
                foreach (var skippedFile in scanResult.SkippedFilesList)
                {
                    string targetPath = skippedFile.Replace(sourceAssetsPath, projectAssetsPath);
                    string relativePath = targetPath.Replace(Application.dataPath, "Assets");
                    relativePath = relativePath.Replace('\\', '/');
                    quickReport.AddSkippedFile(relativePath);
                }

                GuidSyncReport syncReport = null;

                if (scanResult.SkippedFilesCount > 0)
                {
                    Debug.Log($"Found {scanResult.SkippedFilesCount} existing files - synchronizing GUIDs...");
                    syncReport = SynchronizeGuids(projectAssetsPath, sourceAssetsPath);

                    if (syncReport != null)
                    {
                        foreach (var file in scanResult.NewFilesList)
                        {
                            syncReport.AddNewFile(file);
                        }

                        foreach (var skippedFile in quickReport.SkippedFiles)
                        {
                            syncReport.AddSkippedFile(skippedFile);
                        }

                        syncReport.FinalizeReport();
                    }
                }
                else
                {
                    Debug.Log("All files are new - skipping GUID synchronization");
                    syncReport = quickReport;
                    syncReport.FinalizeReport();
                }

                if (syncReport != null && (syncReport.Mappings.Count > 0 || syncReport.NewFilesImported.Count > 0 || syncReport.SkippedFiles.Count > 0))
                {
                    EditorApplication.delayCall += () => GuidSyncReportWindow.ShowReport(syncReport);
                }

                // Second pass: actually copy the files
                var copyResult = CopyDirectorySelective(sourceAssetsPath, projectAssetsPath, forceImport);

                Debug.Log($"Assets merged into project: {copyResult.NewFilesCount} new files imported, {copyResult.SkippedFilesCount} existing files skipped");
                if (forceImport && copyResult.SkippedFilesCount > 0)
                {
                    Debug.Log("Force import enabled - all files were overwritten");
                }

                // Refresh the AssetDatabase to show the new files in Unity
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

        private static void CopyDirectory(string sourceDir, string targetDir)
        {
            // Create all directories
            foreach (string dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(sourceDir, targetDir));
            }

            // Copy all files
            foreach (string filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string targetFilePath = filePath.Replace(sourceDir, targetDir);
                File.Copy(filePath, targetFilePath, true);
            }
        }

        private static FileScanResult ScanDirectorySelective(string sourceDir, string targetDir, bool forceImport)
        {
            var result = new FileScanResult();

            // Scan files to see what needs to be copied
            foreach (string filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string targetFilePath = filePath.Replace(sourceDir, targetDir);
                bool isMetaFile = MetaFileParser.IsMetaFile(filePath);

                // Check if file already exists
                if (File.Exists(targetFilePath) && !forceImport)
                {
                    // Only count non-meta files for statistics (Unity treats asset + meta as one unit)
                    if (!isMetaFile)
                    {
                        result.SkippedFilesCount++;
                        result.SkippedFilesList.Add(filePath);
                    }
                    continue;
                }

                if (!isMetaFile)
                {
                    result.NewFilesCount++;
                    string relativePath = targetFilePath.Replace(Application.dataPath, "Assets");
                    relativePath = relativePath.Replace('\\', '/');
                    result.NewFilesList.Add(relativePath);
                }
            }

            return result;
        }

        private static FileScanResult CopyDirectorySelective(string sourceDir, string targetDir, bool forceImport)
        {
            var result = new FileScanResult();

            // Create all directories
            foreach (string dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(sourceDir, targetDir));
            }

            // Copy files
            foreach (string filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string targetFilePath = filePath.Replace(sourceDir, targetDir);
                bool isMetaFile = MetaFileParser.IsMetaFile(filePath);

                // Check if file already exists
                if (File.Exists(targetFilePath) && !forceImport)
                {
                    // Only count non-meta files for statistics (Unity treats asset + meta as one unit)
                    if (!isMetaFile)
                    {
                        result.SkippedFilesCount++;
                        result.SkippedFilesList.Add(filePath);
                    }
                    continue; // Skip existing files unless force import is enabled
                }

                File.Copy(filePath, targetFilePath, true);

                if (!isMetaFile)
                {
                    result.NewFilesCount++;
                }
            }

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