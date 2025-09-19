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
        public static SyncPlan BuildPlan(string sourceDir, string targetDir, bool forceImport)
        {
            if (string.IsNullOrEmpty(sourceDir)) throw new ArgumentException("Source directory is required", nameof(sourceDir));
            if (string.IsNullOrEmpty(targetDir)) throw new ArgumentException("Target directory is required", nameof(targetDir));

            var plan = new SyncPlan();
            var operations = plan.Operations;

            var allDirectories = Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories);
            foreach (var dirPath in allDirectories)
            {
                string targetDirPath = dirPath.Replace(sourceDir, targetDir);
                plan.DirectoriesToCreate.Add(targetDirPath);
            }

            var allFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);

            var existingShaderNames = ShaderUtils.GetExistingShaderNames();
            var existingAssemblyNames = AssemblyUtils.GetExistingAssemblyNames();
            var skipFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedAssemblyInfo = new List<(string assemblyName, string folderPath, SkipReason reason)>();

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

            return plan;
        }
    }
}
