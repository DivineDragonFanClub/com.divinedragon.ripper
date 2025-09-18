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
            var initialReport = new GuidSyncReport();

            var allDirectories = Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories);
            var allFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);

            // Build shader registry for deduplication
            var existingShaderNames = ShaderUtils.GetExistingShaderNames();
            var skippedDuplicateShaders = new List<string>();

            foreach (var dirPath in allDirectories)
            {
                Directory.CreateDirectory(dirPath.Replace(sourceDir, targetDir));
            }

            var newFiles = new List<string>();
            var skippedFiles = new List<string>();
            var filesToCopy = new List<string>();

            foreach (var filePath in allFiles)
            {
                bool isMetaFile = MetaFileParser.IsMetaFile(filePath);
                string targetFilePath = filePath.Replace(sourceDir, targetDir);
                string unityRelativeTarget = targetFilePath.Replace(Application.dataPath, "Assets").Replace('\\', '/');

                // Check for duplicate shaders by name
                if (ShaderUtils.IsShaderFile(filePath) && !isMetaFile)
                {
                    string shaderName = ShaderUtils.ExtractShaderName(filePath);
                    if (!string.IsNullOrEmpty(shaderName) && existingShaderNames.Contains(shaderName))
                    {
                        Debug.Log($"Skipping duplicate shader: {Path.GetFileName(filePath)} (shader name: {shaderName})");
                        skippedDuplicateShaders.Add(unityRelativeTarget);
                        continue; // Skip this shader and its meta file
                    }
                }

                // Skip meta files for shaders we're not copying
                if (isMetaFile)
                {
                    string baseFile = filePath.Substring(0, filePath.Length - 5); // Remove .meta
                    if (ShaderUtils.IsShaderFile(baseFile))
                    {
                        string shaderName = ShaderUtils.ExtractShaderName(baseFile);
                        if (!string.IsNullOrEmpty(shaderName) && existingShaderNames.Contains(shaderName))
                        {
                            continue; // Skip meta file for duplicate shader
                        }
                    }
                }

                bool targetExists = File.Exists(targetFilePath);

                if (!isMetaFile)
                {
                    if (!targetExists)
                    {
                        result.NewFiles++;
                        newFiles.Add(unityRelativeTarget);
                    }
                    else
                    {
                        result.SkippedFiles++;
                        skippedFiles.Add(unityRelativeTarget);
                    }
                }

                filesToCopy.Add(filePath);
            }

            foreach (var path in newFiles)
            {
                initialReport.AddNewFile(path);
            }

            foreach (var path in skippedFiles)
            {
                initialReport.AddSkippedFile(path);
            }

            // Add duplicate shaders to report
            foreach (var path in skippedDuplicateShaders)
            {
                initialReport.AddDuplicateShader(path);
            }

            GuidSyncReport fullReport;
            if (result.SkippedFiles > 0)
            {
                Debug.Log($"Found {result.SkippedFiles} existing files - synchronizing GUIDs...");
                fullReport = SynchronizeGuids(targetDir, sourceDir) ?? new GuidSyncReport();

                foreach (var path in newFiles)
                {
                    fullReport.AddNewFile(path);
                }

                foreach (var path in skippedFiles)
                {
                    fullReport.AddSkippedFile(path);
                }

                foreach (var path in skippedDuplicateShaders)
                {
                    fullReport.AddDuplicateShader(path);
                }

                fullReport.FinalizeReport();
            }
            else
            {
                Debug.Log("All files are new - skipping GUID synchronization");
                initialReport.FinalizeReport();
                fullReport = initialReport;
            }

            // Only copy files that aren't duplicate shaders
            foreach (var filePath in filesToCopy)
            {
                string targetFilePath = filePath.Replace(sourceDir, targetDir);
                bool targetExists = File.Exists(targetFilePath);
                if (targetExists && !forceImport)
                    continue;

                File.Copy(filePath, targetFilePath, true);
            }

            if (skippedDuplicateShaders.Count > 0)
            {
                Debug.Log($"Skipped {skippedDuplicateShaders.Count} duplicate shaders");
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
