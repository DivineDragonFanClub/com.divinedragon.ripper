using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DivineDragon
{
    public static class SyncOperationApplier
    {
        private static readonly Regex GuidRegex = new Regex(@"guid:\s*([a-f0-9]{32})", RegexOptions.Compiled);

        public static void Apply(
            string targetDir,
            string sourceDir,
            SyncOperations operations,
            IEnumerable<string> directoriesToCreate,
            IEnumerable<ScriptUtils.ScriptMapping> stubMappings,
            bool forceImport)
        {
            if (operations == null) throw new ArgumentNullException(nameof(operations));
            if (string.IsNullOrEmpty(targetDir)) throw new ArgumentException("Target directory is required", nameof(targetDir));
            if (string.IsNullOrEmpty(sourceDir)) throw new ArgumentException("Source directory is required", nameof(sourceDir));

            var createdDirectories = EnsureDirectories(directoriesToCreate);
            ExecuteCopies(operations.Copies, forceImport);

            var scriptRemaps = ComputeScriptRemapOperations(sourceDir, targetDir, operations.Copies, stubMappings);
            if (scriptRemaps.Count > 0)
            {
                operations.ScriptRemaps.AddRange(scriptRemaps);
                ApplyScriptRemappings(targetDir, scriptRemaps);
            }

            var synchronizer = new GuidSynchronizer(targetDir, sourceDir);
            synchronizer.Synchronize(operations, GuidSyncMode.Analyze);
            synchronizer.Synchronize(null, GuidSyncMode.Apply);

            CleanupEmptyDirectories(createdDirectories);
        }

        private static HashSet<string> EnsureDirectories(IEnumerable<string> directories)
        {
            var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (directories == null)
            {
                return created;
            }

            foreach (var dir in directories)
            {
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }

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

        private static void ExecuteCopies(IEnumerable<CopyAssetOperation> copies, bool forceImport)
        {
            if (copies == null)
            {
                return;
            }

            foreach (var copy in copies)
            {
                try
                {
                    if (!forceImport && File.Exists(copy.TargetPath))
                    {
                        continue;
                    }

                    File.Copy(copy.SourcePath, copy.TargetPath, true);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Failed to copy asset {copy.SourcePath} -> {copy.TargetPath}: {ex.Message}");
                    throw;
                }
            }
        }

        private static List<ScriptRemapOperation> ComputeScriptRemapOperations(
            string sourceDir,
            string targetDir,
            IEnumerable<CopyAssetOperation> copies,
            IEnumerable<ScriptUtils.ScriptMapping> stubMappings)
        {
            var results = new List<ScriptRemapOperation>();
            if (copies == null)
                return results;

            if (stubMappings == null)
                return results;

            var mappingList = stubMappings.ToList();
            if (mappingList.Count == 0)
                return results;

            var guidMap = new Dictionary<string, ScriptUtils.ScriptMapping>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in mappingList)
            {
                if (!string.IsNullOrEmpty(mapping.StubGuid))
                {
                    guidMap[mapping.StubGuid] = mapping;
                }
            }

            if (guidMap.Count == 0)
                return results;

            foreach (var copy in copies)
            {
                if (copy.IsMeta)
                    continue;

                if (string.IsNullOrEmpty(copy.TargetPath) || !File.Exists(copy.TargetPath))
                    continue;

                var content = File.ReadAllText(copy.TargetPath);
                if (string.IsNullOrEmpty(content))
                    continue;

                var recordedForFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (Match match in GuidRegex.Matches(content))
                {
                    string oldGuid = match.Groups[1].Value;
                    if (!guidMap.TryGetValue(oldGuid, out var mapping))
                        continue;

                    var recordKey = $"{copy.UnityPath}|{mapping.StubGuid}|{mapping.RealGuid}";
                    if (!recordedForFile.Add(recordKey))
                        continue;

                    var targetPath = copy.UnityPath;
                    var realScriptPath = UnityPathUtils.FromAbsolute(mapping.RealPath, targetDir);
                    var stubScriptPath = UnityPathUtils.FromAbsolute(mapping.StubPath, sourceDir);

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
            var remapList = remaps?.ToList();
            if (remapList == null || remapList.Count == 0)
                return;

            foreach (var group in remapList.GroupBy(r => r.TargetAssetPath))
            {
                var unityPath = group.Key;
                var absolutePath = UnityPathUtils.ToAbsolute(unityPath, targetDir);
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

                string newContent = GuidRegex.Replace(content, match =>
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

        private static void CleanupEmptyDirectories(HashSet<string> createdDirectories)
        {
            if (createdDirectories == null || createdDirectories.Count == 0)
                return;

            var sortedDirs = createdDirectories.OrderByDescending(d => d.Length).ToList();

            foreach (var dir in sortedDirs)
            {
                if (!Directory.Exists(dir))
                    continue;

                if (Directory.GetFiles(dir).Any() || Directory.GetDirectories(dir).Any())
                    continue;

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
