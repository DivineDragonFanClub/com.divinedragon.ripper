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

        private class CopyResult
        {
            public int NewFiles;
            public int SkippedFiles;
            public GuidSyncReport SyncReport;
        }

        private static CopyResult CopyAndCollect(string sourceDir, string targetDir, bool forceImport)
        {
            var operations = new SyncOperations();
            var directoriesToCreate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var allDirectories = Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories);
            foreach (var dirPath in allDirectories)
            {
                string targetDirPath = dirPath.Replace(sourceDir, targetDir);
                directoriesToCreate.Add(targetDirPath);
            }

            var allFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);

            var existingShaderNames = ShaderUtils.GetExistingShaderNames();
            var existingAssemblyNames = AssemblyUtils.GetExistingAssemblyNames();
            var skipFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedAssemblyInfo = new List<(string assemblyName, string folderPath, SkipReason reason)>();
            var stubToRealGuidMappings = new List<ScriptUtils.ScriptMapping>();

            foreach (var filePath in allFiles)
            {
                if (!AssemblyUtils.IsAssemblyDefinitionFile(filePath))
                    continue;

                string assemblyName = AssemblyUtils.ExtractAssemblyName(filePath);

                if (string.IsNullOrEmpty(assemblyName))
                {
                    string assemblyFolder = AssemblyUtils.GetAssemblyFolder(filePath);
                    skipFolders.Add(assemblyFolder);
                    skippedAssemblyInfo.Add(("Invalid/Empty Assembly", assemblyFolder, SkipReason.InvalidAssembly));
                }
                else if (existingAssemblyNames.Contains(assemblyName))
                {
                    string assemblyFolder = AssemblyUtils.GetAssemblyFolder(filePath);
                    var mappings = ScriptUtils.CreateStubToRealGuidMappings(assemblyFolder, assemblyName);
                    stubToRealGuidMappings.AddRange(mappings);

                    skipFolders.Add(assemblyFolder);
                    skippedAssemblyInfo.Add((assemblyName, assemblyFolder, SkipReason.DuplicateAssembly));
                }
            }

            foreach (var filePath in allFiles)
            {
                bool inSkipFolder = skipFolders.Any(skipFolder => AssemblyUtils.IsPathInFolder(filePath, skipFolder));
                if (inSkipFolder)
                    continue;

                bool isMetaFile = MetaFileParser.IsMetaFile(filePath);
                string targetFilePath = filePath.Replace(sourceDir, targetDir);
                string unityRelativeTarget = targetFilePath.Replace(Application.dataPath, "Assets").Replace('\\', '/');

                bool isDuplicateShader = false;

                if (ShaderUtils.IsShaderFile(filePath) && !isMetaFile)
                {
                    string shaderName = ShaderUtils.ExtractShaderName(filePath);
                    if (!string.IsNullOrEmpty(shaderName))
                    {
                        if (existingShaderNames.Contains(shaderName))
                        {
                            isDuplicateShader = true;
                        }
                        else
                        {
                            existingShaderNames.Add(shaderName);
                        }
                    }
                }

                if (isMetaFile)
                {
                    string baseFile = filePath.Substring(0, filePath.Length - 5);
                    if (ShaderUtils.IsShaderFile(baseFile))
                    {
                        string shaderName = ShaderUtils.ExtractShaderName(baseFile);
                        if (!string.IsNullOrEmpty(shaderName) && existingShaderNames.Contains(shaderName))
                        {
                            continue;
                        }
                    }
                }

                if (isDuplicateShader)
                {
                    if (!isMetaFile)
                    {
                        operations.Skips.Add(new SkipAssetOperation
                        {
                            UnityPath = unityRelativeTarget,
                            Reason = SkipReason.DuplicateShader
                        });
                    }
                    continue;
                }

                bool targetExists = File.Exists(targetFilePath);

                if (!isMetaFile)
                {
                    if (!targetExists)
                    {
                        operations.Copies.Add(new CopyAssetOperation
                        {
                            SourcePath = filePath,
                            TargetPath = targetFilePath,
                            UnityPath = unityRelativeTarget,
                            Overwrite = true,
                            IsNew = true,
                            IsMeta = false
                        });
                    }
                    else if (forceImport)
                    {
                        operations.Copies.Add(new CopyAssetOperation
                        {
                            SourcePath = filePath,
                            TargetPath = targetFilePath,
                            UnityPath = unityRelativeTarget,
                            Overwrite = true,
                            IsNew = false,
                            IsMeta = false
                        });
                    }
                    else
                    {
                        operations.Skips.Add(new SkipAssetOperation
                        {
                            UnityPath = unityRelativeTarget,
                            Reason = SkipReason.AlreadyExists
                        });
                        continue;
                    }
                }
                else
                {
                    bool shouldCopyMeta = !targetExists || forceImport;
                    if (!shouldCopyMeta)
                    {
                        continue;
                    }

                    operations.Copies.Add(new CopyAssetOperation
                    {
                        SourcePath = filePath,
                        TargetPath = targetFilePath,
                        UnityPath = unityRelativeTarget,
                        Overwrite = true,
                        IsNew = !targetExists,
                        IsMeta = true
                    });
                }
            }

            foreach (var (assemblyName, folderPath, reason) in skippedAssemblyInfo)
            {
                string unityRelativePath = folderPath.Replace(sourceDir, "").TrimStart('/', '\\');
                operations.Skips.Add(new SkipAssetOperation
                {
                    UnityPath = unityRelativePath,
                    Reason = reason,
                    Details = assemblyName
                });
            }

            var createdDirectories = CreateDirectories(directoriesToCreate);
            SyncOperationExecutor.ExecuteCopies(operations.Copies, forceImport);

            if (stubToRealGuidMappings.Count > 0)
            {
                var scriptRemapResults = CalculateScriptRemapOperations(sourceDir, operations.Copies, stubToRealGuidMappings);
                operations.ScriptRemaps.AddRange(scriptRemapResults);
            }

            if (operations.ScriptRemaps.Count > 0)
            {
                ApplyScriptRemappings(targetDir, operations.ScriptRemaps);
            }

            var synchronizer = new GuidSynchronizer(targetDir, sourceDir);
            synchronizer.Synchronize(operations, GuidSyncMode.Analyze);

            var report = GuidSyncReport.CreateFromOperations(operations);

            var applySynchronizer = new GuidSynchronizer(targetDir, sourceDir);
            applySynchronizer.Synchronize(null, GuidSyncMode.Apply);

            CleanupEmptyDirectories(createdDirectories);

            return new CopyResult
            {
                NewFiles = operations.NewFileCount,
                SkippedFiles = operations.SkippedFileCount,
                SyncReport = report
            };
        }

        private static List<ScriptRemapOperation> CalculateScriptRemapOperations(
            string sourceDir,
            IEnumerable<CopyAssetOperation> copies,
            List<ScriptUtils.ScriptMapping> stubMappings)
        {
            var results = new List<ScriptRemapOperation>();
            if (copies == null)
                return results;

            if (stubMappings == null || stubMappings.Count == 0)
                return results;

            var guidMap = new Dictionary<string, ScriptUtils.ScriptMapping>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in stubMappings)
            {
                if (!string.IsNullOrEmpty(mapping.StubGuid))
                {
                    guidMap[mapping.StubGuid] = mapping;
                }
            }

            if (guidMap.Count == 0)
                return results;

            var guidRegex = new Regex(@"guid:\s*([a-f0-9]{32})", RegexOptions.Compiled);

            foreach (var copy in copies)
            {
                if (copy.IsMeta)
                    continue;

                if (string.IsNullOrEmpty(copy.TargetPath))
                    continue;

                if (!File.Exists(copy.TargetPath))
                    continue;

                var content = File.ReadAllText(copy.TargetPath);
                if (string.IsNullOrEmpty(content))
                    continue;

                var recordedForFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (Match match in guidRegex.Matches(content))
                {
                    string oldGuid = match.Groups[1].Value;
                    if (!guidMap.TryGetValue(oldGuid, out var mapping))
                        continue;

                    var recordKey = $"{copy.UnityPath}|{mapping.StubGuid}|{mapping.RealGuid}";
                    if (!recordedForFile.Add(recordKey))
                        continue;

                    var targetPath = copy.UnityPath;
                    var realScriptPath = ConvertAbsoluteToUnityPath(mapping.RealPath, Application.dataPath);
                    var stubScriptPath = ConvertAbsoluteToUnityPath(mapping.StubPath, sourceDir);

                    results.Add(new ScriptRemapOperation
                    {
                        TargetAssetPath = targetPath,
                        ScriptType = mapping.TypeName,
                        StubGuid = mapping.StubGuid,
                        RealGuid = mapping.RealGuid,
                        StubScriptPath = stubScriptPath,
                        RealScriptPath = realScriptPath
                    });
                }
            }

            return results;
        }

        private static void ApplyScriptRemappings(string targetDir, IEnumerable<ScriptRemapOperation> remaps)
        {
            if (remaps == null)
                return;

            var remapList = remaps.ToList();
            if (remapList.Count == 0)
                return;

            var guidRegex = new Regex(@"guid:\s*([a-f0-9]{32})", RegexOptions.Compiled);

            foreach (var group in remapList.GroupBy(r => r.TargetAssetPath))
            {
                var unityPath = group.Key;
                var absolutePath = ConvertUnityToAbsolutePath(unityPath, targetDir);
                if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                    continue;

                var replacements = new Dictionary<string, ScriptRemapOperation>(StringComparer.OrdinalIgnoreCase);
                foreach (var remap in group)
                {
                    if (!replacements.ContainsKey(remap.StubGuid))
                    {
                        replacements[remap.StubGuid] = remap;
                    }
                }

                string content = File.ReadAllText(absolutePath);
                bool modified = false;
                var logged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                string newContent = guidRegex.Replace(content, match =>
                {
                    var oldGuid = match.Groups[1].Value;
                    if (!replacements.TryGetValue(oldGuid, out var remap))
                        return match.Value;

                    modified = true;
                    var logKey = $"{unityPath}|{oldGuid}";
                    if (logged.Add(logKey))
                    {
                        Debug.Log($"Remapping GUID in {Path.GetFileName(absolutePath)}: {oldGuid} -> {remap.RealGuid}");
                    }
                    return $"guid: {remap.RealGuid}";
                });

                if (modified)
                {
                    File.WriteAllText(absolutePath, newContent);
                }
            }
        }

        private static HashSet<string> CreateDirectories(IEnumerable<string> directories)
        {
            var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (directories == null)
                return created;

            foreach (var dir in directories)
            {
                if (string.IsNullOrEmpty(dir))
                    continue;

                var normalized = Path.GetFullPath(dir);
                if (!Directory.Exists(normalized))
                {
                    Directory.CreateDirectory(normalized);
                    created.Add(normalized);
                }
                else
                {
                    Directory.CreateDirectory(normalized);
                }
            }

            return created;
        }

        private static string ConvertUnityToAbsolutePath(string unityPath, string assetsRoot)
        {
            if (string.IsNullOrEmpty(unityPath))
                return unityPath;

            if (Path.IsPathRooted(unityPath))
                return unityPath;

            string root = assetsRoot;
            if (string.IsNullOrEmpty(root))
            {
                root = Application.dataPath;
            }

            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            const string assetsPrefix = "Assets/";
            if (unityPath.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var relative = unityPath.Substring(assetsPrefix.Length);
                return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            }

            return Path.Combine(root, unityPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string GetRelativePath(string basePath, string fullPath)
        {
            if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
                basePath += Path.DirectorySeparatorChar;

            if (fullPath.StartsWith(basePath))
                return fullPath.Substring(basePath.Length);

            return Path.GetFileName(fullPath);
        }

        private static List<ScriptRemapOperation> ApplyGuidRemappings(string targetDir, string sourceDir, List<ScriptUtils.ScriptMapping> mappings)
        {
            var remapResults = new List<ScriptRemapOperation>();
            if (mappings.Count == 0)
                return remapResults;

            // Build a dictionary for quick lookup
            var guidMap = new Dictionary<string, ScriptUtils.ScriptMapping>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in mappings)
            {
                if (!string.IsNullOrEmpty(mapping.StubGuid))
                {
                    guidMap[mapping.StubGuid] = mapping;
                }
            }

            // Find all files that might have references (be naive and scan everything)
            var filesToUpdate = Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories);

            var guidRegex = new Regex(@"guid:\s*([a-f0-9]{32})", RegexOptions.Compiled);

            foreach (var file in filesToUpdate)
            {
                bool modified = false;
                var recordedForFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string content = File.ReadAllText(file);
                string newContent = guidRegex.Replace(content, match =>
                {
                    string oldGuid = match.Groups[1].Value;
                    if (guidMap.TryGetValue(oldGuid, out var mapping))
                    {
                        modified = true;
                        Debug.Log($"Remapping GUID in {Path.GetFileName(file)}: {oldGuid} -> {mapping.RealGuid}");

                        var targetPath = ConvertAbsoluteToUnityPath(file, targetDir);
                        var realScriptPath = ConvertAbsoluteToUnityPath(mapping.RealPath, Application.dataPath);
                        var stubScriptPath = ConvertAbsoluteToUnityPath(mapping.StubPath, sourceDir);
                        var recordKey = $"{targetPath}|{mapping.StubGuid}|{mapping.RealGuid}";
                        if (recordedForFile.Add(recordKey))
                        {
                            remapResults.Add(new ScriptRemapOperation
                            {
                                TargetAssetPath = targetPath,
                                ScriptType = mapping.TypeName,
                                StubGuid = mapping.StubGuid,
                                RealGuid = mapping.RealGuid,
                                StubScriptPath = stubScriptPath,
                                RealScriptPath = realScriptPath
                            });
                        }

                        return $"guid: {mapping.RealGuid}";
                    }
                    return match.Value;
                });

                if (modified)
                {
                    File.WriteAllText(file, newContent);
                }
            }

            return remapResults;
        }

        private static string ConvertAbsoluteToUnityPath(string absolutePath, string assetsRoot)
        {
            if (string.IsNullOrEmpty(absolutePath) || string.IsNullOrEmpty(assetsRoot))
            {
                return absolutePath;
            }

            var normalizedPath = Path.GetFullPath(absolutePath).Replace('\\', '/');
            var normalizedRoot = Path.GetFullPath(assetsRoot).Replace('\\', '/');

            if (!normalizedRoot.EndsWith("/", StringComparison.Ordinal))
            {
                normalizedRoot += "/";
            }

            if (normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return "Assets/" + normalizedPath.Substring(normalizedRoot.Length);
            }

            return normalizedPath;
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
