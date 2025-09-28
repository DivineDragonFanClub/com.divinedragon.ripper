using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DivineDragon
{
    public class SyncPlan
    {
        public SyncOperations Operations { get; } = new SyncOperations();
        public HashSet<string> DirectoriesToCreate { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public List<ScriptUtils.ScriptMapping> StubScriptMappings { get; } = new List<ScriptUtils.ScriptMapping>();
    }

    public static class SyncOperationPlanner
    {
        internal const string PrivateFolderSuffix = "_Resources";
        internal const string SharedScriptsRoot = "Assets/Scripts";
        private static readonly Regex GuidReferenceRegex = new Regex(@"guid:\s*([a-f0-9]{32})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static SyncPlan BuildPlan(string sourceDir, string targetDir)
        {
            if (string.IsNullOrEmpty(sourceDir)) throw new ArgumentException("Source directory is required", nameof(sourceDir));
            if (string.IsNullOrEmpty(targetDir)) throw new ArgumentException("Target directory is required", nameof(targetDir));

            var plan = new SyncPlan();
            var operations = plan.Operations;

            var allDirectories = Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories);
            foreach (var dirPath in allDirectories)
            {
                var relativeDir = UnityPathUtils.GetRelativePath(sourceDir, dirPath);
                var targetDirPath = Path.Combine(targetDir, relativeDir);
                plan.DirectoriesToCreate.Add(targetDirPath);
            }

            var allFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);

            var existingShaderNames = ShaderUtils.GetExistingShaderNames();
            var existingAssemblyNames = AssemblyUtils.GetExistingAssemblyNames();
            var skipFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedAssemblyInfo = new List<(string assemblyName, string folderPath, SkipReason reason)>();

            IdentifyAssemblyConflicts(plan, allFiles, existingAssemblyNames, skipFolders, skippedAssemblyInfo);

            var relocationOverrides = BuildPrivateDependencyOverrides(sourceDir, allFiles, skipFolders);
            RegisterRelocatedDirectories(plan, relocationOverrides, targetDir);

            foreach (var filePath in allFiles)
            {
                if (skipFolders.Any(skipFolder => AssemblyUtils.IsPathInFolder(filePath, skipFolder)))
                    continue;

                bool isMetaFile = MetaFileParser.IsMetaFile(filePath);
                var relativeFile = NormalizeRelativePath(UnityPathUtils.GetRelativePath(sourceDir, filePath));
                if (relocationOverrides.TryGetValue(relativeFile, out var relocatedRelative))
                {
                    relativeFile = relocatedRelative;
                }
                string targetFilePath = Path.Combine(targetDir, relativeFile);
                string unityRelativeTarget = UnityPathUtils.FromAbsolute(targetFilePath, targetDir);

                if (ShouldSkipShader(existingShaderNames, filePath, isMetaFile))
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

                if (isMetaFile)
                {
                    PlanMetaCopy(operations, filePath, targetFilePath, unityRelativeTarget);
                }
                else
                {
                    PlanAssetCopy(operations, filePath, targetFilePath, unityRelativeTarget);
                }
            }

            RecordAssemblySkips(plan, sourceDir, skippedAssemblyInfo);

            return plan;
        }

        private static Dictionary<string, string> BuildPrivateDependencyOverrides(
            string sourceDir,
            IEnumerable<string> allFiles,
            HashSet<string> skipFolders)
        {
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (allFiles == null)
                return overrides;

            var guidToUnityPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var unityToRelative = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var unityMetaToRelative = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var unityToAbsolute = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var assetFiles = new List<(string filePath, string unityPath)>();

            foreach (var file in allFiles)
            {
                if (string.IsNullOrEmpty(file))
                    continue;

                if (IsInSkippedFolder(file, skipFolders))
                    continue;

                var unityPath = UnityPathUtils.FromAbsolute(file, sourceDir);
                var relativePath = NormalizeRelativePath(UnityPathUtils.GetRelativePath(sourceDir, file));

                if (MetaFileParser.IsMetaFile(file))
                {
                    unityMetaToRelative[unityPath] = relativePath;

                    if (MetaFileParser.TryGetGuid(file, out var guid))
                    {
                        var assetFile = file.Substring(0, file.Length - 5);
                        if (File.Exists(assetFile))
                        {
                            var assetUnity = UnityPathUtils.FromAbsolute(assetFile, sourceDir);
                            guidToUnityPath[guid] = assetUnity;
                        }
                    }
                }
                else
                {
                    unityToRelative[unityPath] = relativePath;
                    unityToAbsolute[unityPath] = file;
                    assetFiles.Add((file, unityPath));
                }
            }

            var dependencyGraph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (filePath, unityPath) in assetFiles)
            {
                if (IsInSkippedFolder(filePath, skipFolders))
                    continue;

                if (!UnityFileUtils.IsUnityYamlFile(filePath))
                    continue;

                string content;
                try
                {
                    content = File.ReadAllText(filePath);
                }
                catch
                {
                    continue;
                }

                foreach (Match match in GuidReferenceRegex.Matches(content))
                {
                    if (!match.Success || match.Groups.Count < 2)
                        continue;

                    var referencedGuid = match.Groups[1].Value;
                    if (!guidToUnityPath.TryGetValue(referencedGuid, out var dependencyUnityPath))
                        continue;

                    if (string.Equals(dependencyUnityPath, unityPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!dependencyGraph.TryGetValue(unityPath, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        dependencyGraph[unityPath] = set;
                    }

                    set.Add(dependencyUnityPath);
                }
            }

            var visitedShare = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queuedPrivate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string assetUnity, string baseFolder)>();

            foreach (var assetUnity in unityToRelative.Keys)
            {
                if (!IsShareUnityPath(assetUnity))
                    continue;

                var baseFolder = GetDependencyBucket(assetUnity);
                if (string.IsNullOrEmpty(baseFolder))
                    continue;

                if (visitedShare.Add(assetUnity))
                {
                    queue.Enqueue((assetUnity, baseFolder));
                }
            }

            while (queue.Count > 0)
            {
                var (currentUnity, baseFolder) = queue.Dequeue();
                if (!dependencyGraph.TryGetValue(currentUnity, out var dependencies))
                    continue;

                foreach (var dependencyUnity in dependencies)
                {
                    if (IsShareUnityPath(dependencyUnity))
                    {
                        var shareBase = GetDependencyBucket(dependencyUnity);
                        if (!string.IsNullOrEmpty(shareBase) && visitedShare.Add(dependencyUnity))
                        {
                            queue.Enqueue((dependencyUnity, shareBase));
                        }
                        continue;
                    }

                    if (!unityToRelative.TryGetValue(dependencyUnity, out var dependencyRelative))
                        continue;

                    if (unityToAbsolute.TryGetValue(dependencyUnity, out var dependencyAbsolute) && IsInSkippedFolder(dependencyAbsolute, skipFolders))
                        continue;

                    var relocatedUnity = ComputeRelocatedUnityPath(baseFolder, dependencyUnity);
                    relocatedUnity = ApplySpecialRelocations(dependencyUnity, relocatedUnity);
                    if (string.IsNullOrEmpty(relocatedUnity) || string.Equals(relocatedUnity, dependencyUnity, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var relocatedRelative = UnityToRelativePath(relocatedUnity);
                    if (string.IsNullOrEmpty(relocatedRelative))
                        continue;

                    if (!overrides.TryGetValue(dependencyRelative, out var existingRelative))
                    {
                        overrides[dependencyRelative] = relocatedRelative;
                    }
                    else if (!PathEquals(existingRelative, relocatedRelative))
                    {
                        continue;
                    }

                    if (unityMetaToRelative.TryGetValue(dependencyUnity + ".meta", out var dependencyMetaRelative))
                    {
                        var relocatedMetaRelative = relocatedRelative + ".meta";
                        if (!overrides.TryGetValue(dependencyMetaRelative, out var existingMetaRelative))
                        {
                            overrides[dependencyMetaRelative] = relocatedMetaRelative;
                        }
                        else if (!PathEquals(existingMetaRelative, relocatedMetaRelative))
                        {
                            continue;
                        }
                    }

                    if (queuedPrivate.Add(dependencyUnity))
                    {
                        queue.Enqueue((dependencyUnity, baseFolder));
                    }
                }
            }

            return overrides;
        }

        private static void RegisterRelocatedDirectories(SyncPlan plan, Dictionary<string, string> overrides, string targetDir)
        {
            if (overrides == null || overrides.Count == 0)
            {
                return;
            }

            foreach (var relocated in overrides.Values)
            {
                if (string.IsNullOrEmpty(relocated))
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(relocated);
                if (string.IsNullOrEmpty(directory))
                {
                    continue;
                }

                var absolute = Path.Combine(targetDir, directory);
                plan.DirectoriesToCreate.Add(absolute);
            }
        }

        private static string ComputeRelocatedUnityPath(string baseFolderUnity, string dependencyUnityPath)
        {
            if (string.IsNullOrEmpty(baseFolderUnity))
                return dependencyUnityPath;

            var normalizedBase = UnityPathUtils.NormalizeAssetPath(baseFolderUnity);
            if (!normalizedBase.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return dependencyUnityPath;

            var normalizedDependency = UnityPathUtils.NormalizeAssetPath(dependencyUnityPath);
            if (!normalizedDependency.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || normalizedDependency.Length <= 7)
                return dependencyUnityPath;

            var relativeToAssets = normalizedDependency.Substring(7);
            var relocated = $"{normalizedBase}/{relativeToAssets}";
            return UnityPathUtils.NormalizeAssetPath(relocated);
        }

        private static string ApplySpecialRelocations(string dependencyUnityPath, string computedRelocatedUnity)
        {
            if (string.IsNullOrEmpty(dependencyUnityPath))
            {
                return computedRelocatedUnity;
            }

            var normalizedDependency = UnityPathUtils.NormalizeAssetPath(dependencyUnityPath);

            if (IsScriptsPath(normalizedDependency))
            {
                return normalizedDependency;
            }

            // More in the future?

            return computedRelocatedUnity;
        }

        private static bool IsScriptsPath(string unityPath)
        {
            if (string.IsNullOrEmpty(unityPath))
            {
                return false;
            }

            if (string.Equals(unityPath, SharedScriptsRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (unityPath.StartsWith(SharedScriptsRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static string GetDependencyBucket(string unityPath)
        {
            if (string.IsNullOrEmpty(unityPath))
                return null;

            var normalized = UnityPathUtils.NormalizeAssetPath(unityPath);
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return null;

            var lastSlash = normalized.LastIndexOf('/');
            var lastDot = normalized.LastIndexOf('.');

            if (lastDot > lastSlash)
            {
                normalized = normalized.Substring(0, lastDot);
            }

            if (!normalized.EndsWith(PrivateFolderSuffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = string.Concat(normalized, PrivateFolderSuffix);
            }

            return normalized;
        }

        private static bool IsShareUnityPath(string unityPath)
        {
            return !string.IsNullOrEmpty(unityPath) && unityPath.StartsWith("Assets/Share/", StringComparison.OrdinalIgnoreCase);
        }

        private static string UnityToRelativePath(string unityPath)
        {
            if (string.IsNullOrEmpty(unityPath))
            {
                return unityPath;
            }

            var normalized = UnityPathUtils.NormalizeAssetPath(unityPath);
            if (string.Equals(normalized, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && normalized.Length > 7)
            {
                var trimmed = normalized.Substring(7);
                return NormalizeRelativePath(trimmed);
            }

            return NormalizeRelativePath(normalized);
        }

        private static string NormalizeRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            var normalized = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            return normalized.TrimStart(Path.DirectorySeparatorChar);
        }

        private static bool PathEquals(string left, string right)
        {
            return string.Equals(NormalizeRelativePath(left), NormalizeRelativePath(right), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInSkippedFolder(string path, HashSet<string> skipFolders)
        {
            if (skipFolders == null || skipFolders.Count == 0 || string.IsNullOrEmpty(path))
                return false;

            foreach (var folder in skipFolders)
            {
                if (AssemblyUtils.IsPathInFolder(path, folder))
                    return true;
            }

            return false;
        }

        private static void IdentifyAssemblyConflicts(
            SyncPlan plan,
            IEnumerable<string> allFiles,
            HashSet<string> existingAssemblyNames,
            HashSet<string> skipFolders,
            List<(string assemblyName, string folderPath, SkipReason reason)> skippedAssemblyInfo)
        {
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
                    plan.StubScriptMappings.AddRange(mappings);

                    skipFolders.Add(assemblyFolder);
                    skippedAssemblyInfo.Add((assemblyName, assemblyFolder, SkipReason.DuplicateAssembly));
                }
            }
        }

        private static bool ShouldSkipShader(HashSet<string> existingShaderNames, string filePath, bool isMetaFile)
        {
            if (isMetaFile)
            {
                string baseFile = filePath.Substring(0, filePath.Length - 5);
                if (ShaderUtils.IsShaderFile(baseFile))
                {
                    string shaderName = ShaderUtils.ExtractShaderName(baseFile);
                    if (!string.IsNullOrEmpty(shaderName) && existingShaderNames.Contains(shaderName))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (!ShaderUtils.IsShaderFile(filePath))
            {
                return false;
            }

            var shader = ShaderUtils.ExtractShaderName(filePath);
            if (string.IsNullOrEmpty(shader))
            {
                return false;
            }

            if (existingShaderNames.Contains(shader))
            {
                return true;
            }

            existingShaderNames.Add(shader);
            return false;
        }

        private static void PlanAssetCopy(
            SyncOperations operations,
            string sourcePath,
            string targetPath,
            string unityPath)
        {
            if (File.Exists(targetPath))
            {
                operations.Skips.Add(new SkipAssetOperation
                {
                    UnityPath = unityPath,
                    Reason = SkipReason.AlreadyExists
                });
                return;
            }

            operations.Copies.Add(new CopyAssetOperation
            {
                SourcePath = sourcePath,
                TargetPath = targetPath,
                UnityPath = unityPath,
                IsNew = true,
                Kind = FileType.Asset
            });
        }

        private static void PlanMetaCopy(
            SyncOperations operations,
            string sourcePath,
            string targetPath,
            string unityPath)
        {
            if (File.Exists(targetPath))
            {
                return;
            }

            operations.Copies.Add(new CopyAssetOperation
            {
                SourcePath = sourcePath,
                TargetPath = targetPath,
                UnityPath = unityPath,
                IsNew = true,
                Kind = FileType.Meta
            });
        }

        private static void RecordAssemblySkips(
            SyncPlan plan,
            string sourceDir,
            List<(string assemblyName, string folderPath, SkipReason reason)> skippedAssemblyInfo)
        {
            foreach (var (assemblyName, folderPath, reason) in skippedAssemblyInfo)
            {
                var relativeFolder = UnityPathUtils.GetRelativePath(sourceDir, folderPath);
                string unityRelativePath = UnityPathUtils.NormalizeAssetPath(relativeFolder);
                plan.Operations.Skips.Add(new SkipAssetOperation
                {
                    UnityPath = unityRelativePath,
                    Reason = reason,
                    Details = assemblyName
                });
            }
        }
    }
}
