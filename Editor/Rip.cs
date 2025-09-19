using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

            // Build assembly registry and identify folders to skip
            var existingAssemblyNames = AssemblyUtils.GetExistingAssemblyNames();
            var skipFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedAssemblyInfo = new List<(string assemblyName, string folderPath)>();

            // Find all .asmdef files and check for duplicates or invalid assemblies
            var stubToRealGuidMappings = new List<ScriptUtils.ScriptMapping>();

            foreach (var filePath in allFiles)
            {
                if (AssemblyUtils.IsAssemblyDefinitionFile(filePath))
                {
                    string assemblyName = AssemblyUtils.ExtractAssemblyName(filePath);

                    // Skip invalid assemblies (null or empty names)
                    if (string.IsNullOrEmpty(assemblyName))
                    {
                        string assemblyFolder = AssemblyUtils.GetAssemblyFolder(filePath);
                        skipFolders.Add(assemblyFolder);
                        skippedAssemblyInfo.Add(("Invalid/Empty Assembly", assemblyFolder));
                    }
                    // Skip duplicate assemblies
                    else if (existingAssemblyNames.Contains(assemblyName))
                    {
                        string assemblyFolder = AssemblyUtils.GetAssemblyFolder(filePath);

                        // Create GUID mappings for stub scripts to real scripts
                        var mappings = ScriptUtils.CreateStubToRealGuidMappings(assemblyFolder, assemblyName);
                        stubToRealGuidMappings.AddRange(mappings);

                        skipFolders.Add(assemblyFolder);
                        skippedAssemblyInfo.Add((assemblyName, assemblyFolder));
                    }
                }
            }

            var createdDirectories = new HashSet<string>();
            foreach (var dirPath in allDirectories)
            {
                string targetDirPath = dirPath.Replace(sourceDir, targetDir);
                Directory.CreateDirectory(targetDirPath);
                createdDirectories.Add(targetDirPath);
            }

            var newFiles = new List<string>();
            var skippedFiles = new List<string>();
            var filesToCopy = new List<string>();

            foreach (var filePath in allFiles)
            {
                // Skip files in assembly folders that are being skipped
                bool inSkipFolder = false;
                foreach (var skipFolder in skipFolders)
                {
                    if (AssemblyUtils.IsPathInFolder(filePath, skipFolder))
                    {
                        inSkipFolder = true;
                        break;
                    }
                }
                if (inSkipFolder)
                    continue;

                bool isMetaFile = MetaFileParser.IsMetaFile(filePath);
                string targetFilePath = filePath.Replace(sourceDir, targetDir);
                string unityRelativeTarget = targetFilePath.Replace(Application.dataPath, "Assets").Replace('\\', '/');

                bool isDuplicateShader = false;

                // Check for duplicate shaders by name
                if (ShaderUtils.IsShaderFile(filePath) && !isMetaFile)
                {
                    string shaderName = ShaderUtils.ExtractShaderName(filePath);
                    if (!string.IsNullOrEmpty(shaderName))
                    {
                        if (existingShaderNames.Contains(shaderName))
                        {
                            skippedDuplicateShaders.Add(unityRelativeTarget);
                            isDuplicateShader = true;
                        }
                        else
                        {
                            existingShaderNames.Add(shaderName);
                        }
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

                if (isDuplicateShader)
                {
                    // Record as skipped so GUID synchronization maps old shader GUID to project shader GUID
                    if (!isMetaFile)
                    {
                        result.SkippedFiles++;
                        if (!skippedFiles.Contains(unityRelativeTarget))
                        {
                            skippedFiles.Add(unityRelativeTarget);
                        }
                    }
                    continue;
                }

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

            // Add duplicate assemblies to report
            foreach (var (assemblyName, folderPath) in skippedAssemblyInfo)
            {
                string unityRelativePath = folderPath.Replace(sourceDir, "").TrimStart('/', '\\');
                initialReport.AddDuplicateAssembly(assemblyName, unityRelativePath);
            }

            // Add stub to real GUID mappings
            foreach (var mapping in stubToRealGuidMappings)
            {
                // Add as a GUID mapping for the report to handle
                string relativePath = mapping.StubPath.Replace(sourceDir, "").TrimStart('/', '\\');
                initialReport.AddGuidMapping(relativePath, mapping.StubGuid, mapping.RealGuid);
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

                foreach (var (assemblyName, folderPath) in skippedAssemblyInfo)
                {
                    string unityRelativePath = folderPath.Replace(sourceDir, "").TrimStart('/', '\\');
                    fullReport.AddDuplicateAssembly(assemblyName, unityRelativePath);
                }

                foreach (var mapping in stubToRealGuidMappings)
                {
                    string relativePath = mapping.StubPath.Replace(sourceDir, "").TrimStart('/', '\\');
                    fullReport.AddGuidMapping(relativePath, mapping.StubGuid, mapping.RealGuid);
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

            CleanupEmptyDirectories(createdDirectories);

            // Apply stub to real GUID mappings to imported files
            if (stubToRealGuidMappings.Count > 0)
            {
                ApplyGuidRemappings(targetDir, stubToRealGuidMappings);
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

        private static void ApplyGuidRemappings(string targetDir, List<ScriptUtils.ScriptMapping> mappings)
        {
            if (mappings.Count == 0)
                return;

            // Build a dictionary for quick lookup
            var guidMap = new Dictionary<string, string>();
            foreach (var mapping in mappings)
            {
                guidMap[mapping.StubGuid] = mapping.RealGuid;
            }

            // Find all files that might have references (prefabs, scenes, assets)
            var filesToUpdate = new List<string>();
            filesToUpdate.AddRange(Directory.GetFiles(targetDir, "*.prefab", SearchOption.AllDirectories));
            filesToUpdate.AddRange(Directory.GetFiles(targetDir, "*.unity", SearchOption.AllDirectories));
            filesToUpdate.AddRange(Directory.GetFiles(targetDir, "*.asset", SearchOption.AllDirectories));

            var guidRegex = new Regex(@"guid:\s*([a-f0-9]{32})", RegexOptions.Compiled);

            foreach (var file in filesToUpdate)
            {
                bool modified = false;
                string content = File.ReadAllText(file);
                string newContent = guidRegex.Replace(content, match =>
                {
                    string oldGuid = match.Groups[1].Value;
                    if (guidMap.TryGetValue(oldGuid, out string newGuid))
                    {
                        modified = true;
                        Debug.Log($"Remapping GUID in {Path.GetFileName(file)}: {oldGuid} -> {newGuid}");
                        return $"guid: {newGuid}";
                    }
                    return match.Value;
                });

                if (modified)
                {
                    File.WriteAllText(file, newContent);
                }
            }
        }

        private static void CleanupEmptyDirectories(HashSet<string> createdDirectories)
        {
            var sortedDirs = createdDirectories.OrderByDescending(d => d.Length).ToList();

            foreach (var dir in sortedDirs)
            {
                if (Directory.Exists(dir))
                {
                    if (!Directory.GetFiles(dir).Any() && !Directory.GetDirectories(dir).Any())
                    {
                        try
                        {
                            Directory.Delete(dir);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"Failed to delete empty directory {dir}: {ex.Message}");
                        }
                    }
                }
            }
        }
    }
}
