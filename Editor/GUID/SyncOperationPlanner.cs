using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            foreach (var filePath in allFiles)
            {
                if (skipFolders.Any(skipFolder => AssemblyUtils.IsPathInFolder(filePath, skipFolder)))
                    continue;

                bool isMetaFile = MetaFileParser.IsMetaFile(filePath);
                var relativeFile = UnityPathUtils.GetRelativePath(sourceDir, filePath);
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
