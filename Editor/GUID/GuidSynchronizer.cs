using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

// Type aliases for clarity
using Guid = System.String;
using FilePath = System.String;
using RelativePath = System.String;
using DirectoryPath = System.String;
using FileID = System.Int64;

namespace DivineDragon
{
    /// GUID "synchronization" that coordinates the remapping of GUIDs
    /// from subordinate project (imported assets) to main project (current Unity project)
    public enum GuidSyncMode
    {
        Analyze,
        Apply
    }

    public class GuidSynchronizer
    {
        private readonly DirectoryPath _mainProjectPath;
        private readonly DirectoryPath _subordinateProjectPath;
        private readonly Dictionary<RelativePath, GuidMapping> _guidMappings;

        // Matches: guid: 0123456789abcdef0123456789abcdef
        // Captures the GUID (group 1)
        private static readonly Regex SimpleGuidRegex = new Regex(@"guid:\s*([a-f0-9]{32})", RegexOptions.Compiled);

        // Matches: {fileID: 123456789, guid: 0123456789abcdef0123456789abcdef, type: 3}
        // Captures: FileID (group 1), GUID (group 2), Type (group 3)
        private static readonly Regex FileIdGuidRegex = new Regex(@"\{fileID:\s*(-?\d+),\s*guid:\s*([a-f0-9]{32}),\s*type:\s*(\d+)\}", RegexOptions.Compiled);

        private class GuidMapping
        {
            public RelativePath RelativePath { get; set; }
            public Guid MainGuid { get; set; }
            public Guid SubordinateGuid { get; set; }
            public FileID? MainMainObjectFileId { get; set; }
            public FileID? SubordinateMainObjectFileId { get; set; }
            public Dictionary<FileID, FileID> FileIdMappings { get; } = new Dictionary<FileID, FileID>();
        }

        public GuidSynchronizer(DirectoryPath mainProjectPath, DirectoryPath subordinateProjectPath)
        {
            _mainProjectPath = NormalizeAssetsPath(mainProjectPath);
            _subordinateProjectPath = NormalizeAssetsPath(subordinateProjectPath);
            _guidMappings = new Dictionary<RelativePath, GuidMapping>();
        }

        private class UpdateResult
        {
            public Dictionary<FilePath, HashSet<Guid>> FileUpdates { get; } = new Dictionary<FilePath, HashSet<Guid>>();
            public Dictionary<FilePath, List<(Guid guid, FileID oldFileId, FileID newFileId)>> FileIdUpdates { get; } = new Dictionary<FilePath, List<(Guid, FileID, FileID)>>();
        }

        public void Synchronize(SyncOperations operations, GuidSyncMode mode)
        {
            if (mode == GuidSyncMode.Analyze && operations == null)
            {
                throw new ArgumentNullException(nameof(operations));
            }

            try
            {
                ScanProjects();

                if (_guidMappings.Count == 0)
                {
                    return;
                }

                // Do the actual work
                var updateResult = UpdateReferences(mode == GuidSyncMode.Apply);

                if (mode == GuidSyncMode.Analyze && operations != null)
                {
                    RecordOperations(updateResult, operations);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"GUID synchronization failed: {ex.Message}");
                throw;
            }
        }

        private void RecordOperations(UpdateResult updateResult, SyncOperations operations)
        {
            var replacedGuids = new HashSet<Guid>(updateResult.FileUpdates.SelectMany(kvp => kvp.Value));
            var mappingBySubGuid = _guidMappings.Values.ToDictionary(m => m.SubordinateGuid, m => m);

            foreach (var mapping in _guidMappings.Values)
            {
                if (replacedGuids.Contains(mapping.SubordinateGuid))
                {
                    var assetPath = ConvertRelativeAssetPath(mapping.RelativePath);
                    var assetName = Path.GetFileNameWithoutExtension(assetPath);

                    operations.GuidRemaps.Add(new GuidRemapOperation
                    {
                        AssetPath = assetPath,
                        AssetName = assetName,
                        OldGuid = mapping.SubordinateGuid,
                        NewGuid = mapping.MainGuid
                    });
                }
            }

            foreach (var kvp in updateResult.FileUpdates)
            {
                foreach (var guid in kvp.Value)
                {
                    if (mappingBySubGuid.TryGetValue(guid, out var mapping))
                    {
                        var assetPath = ConvertRelativeAssetPath(mapping.RelativePath);
                        var assetName = Path.GetFileNameWithoutExtension(assetPath);
                        var referencingPath = ConvertToUnityPathForReport(kvp.Key);

                        operations.Dependencies.Add(new DependencyOperation
                        {
                            AssetPath = referencingPath,
                            DependencyName = assetName,
                            DependencyPath = assetPath,
                            OldGuid = mapping.SubordinateGuid,
                            NewGuid = mapping.MainGuid
                        });
                    }
                }
            }

            // Add fileID remapping info
            foreach (var kvp in updateResult.FileIdUpdates)
            {
                var unityPath = ConvertToUnityPathForReport(kvp.Key);

                var grouped = kvp.Value
                    .GroupBy(r => (r.guid, r.oldFileId, r.newFileId))
                    .Select(g => g.Key);

                foreach (var remap in grouped)
                {
                    var targetPath = unityPath;
                    if (mappingBySubGuid.TryGetValue(remap.guid, out var guidMapping))
                    {
                        targetPath = ConvertRelativeAssetPath(guidMapping.RelativePath);
                    }

                    operations.FileIdRemaps.Add(new FileIdRemapOperation
                    {
                        AssetPath = targetPath,
                        Guid = remap.guid,
                        OldFileId = remap.oldFileId,
                        NewFileId = remap.newFileId
                    });
                }
            }
        }

        private static string ConvertRelativeAssetPath(RelativePath relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return relativePath;
            }

            var normalized = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "Assets/" + normalized.TrimStart('/');
            }

            if (normalized.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(0, normalized.Length - ".meta".Length);
            }

            return normalized;
        }

        private static string ConvertToUnityPathForReport(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return filePath;
            }

            int assetsIndex = filePath.IndexOf("Assets", StringComparison.OrdinalIgnoreCase);
            if (assetsIndex >= 0)
            {
                string relativePath = filePath.Substring(assetsIndex);
                return relativePath.Replace('\\', '/');
            }

            return filePath.Replace('\\', '/');
        }

        private void ScanProjects()
        {
            _guidMappings.Clear();

            var mainMetaFiles = ScanMetaFiles(_mainProjectPath, skipSharePrivate: true);
            var subordinateMetaFiles = ScanMetaFiles(_subordinateProjectPath);

            foreach (var kvp in mainMetaFiles)
            {
                var relativePath = kvp.Key;
                var (mainGuid, mainMainFileId) = kvp.Value;

                if (subordinateMetaFiles.TryGetValue(relativePath, out var subData))
                {
                    var (subGuid, subMainFileId) = subData;
                    if (mainGuid != subGuid)
                    {
                        var mapping = new GuidMapping
                        {
                            RelativePath = relativePath,
                            MainGuid = mainGuid,
                            SubordinateGuid = subGuid,
                            MainMainObjectFileId = mainMainFileId,
                            SubordinateMainObjectFileId = subMainFileId
                        };

                        PopulateFileIdMappings(mapping, _mainProjectPath, _subordinateProjectPath);

                        _guidMappings[relativePath] = mapping;
                    }
                }
            }
        }

        private static DirectoryPath NormalizeAssetsPath(DirectoryPath projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
            {
                return projectPath;
            }

            var trimmed = projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.EndsWith("Assets", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            return Path.Combine(trimmed, "Assets");
        }

        private Dictionary<RelativePath, (Guid guid, FileID? fileId)> ScanMetaFiles(DirectoryPath projectPath, bool skipSharePrivate = false)
        {
            var metaFiles = new Dictionary<RelativePath, (Guid, FileID?)>();

            // We only care about the Assets folder - that's where all the actual content that needs syncing lives
            // projectPath should already be pointing to the Assets folder, but guarding
            string assetsPath = projectPath;
            if (!projectPath.EndsWith("Assets", StringComparison.OrdinalIgnoreCase))
            {
                assetsPath = Path.Combine(projectPath, "Assets");
            }

            if (!Directory.Exists(assetsPath))
            {
                Debug.LogWarning($"Assets folder not found at: {assetsPath}");
                return metaFiles;
            }

            foreach (var filePath in Directory.GetFiles(assetsPath, "*.meta", SearchOption.AllDirectories))
            {
                if (skipSharePrivate && IsSharePrivatePath(filePath, assetsPath))
                {
                    continue;
                }

                if (MetaFileParser.TryGetGuidAndMainFileId(filePath, out var guid, out var fileId))
                {
                    var relativePath = GetRelativePath(assetsPath, filePath);
                    metaFiles[relativePath] = (guid, fileId);
                }
            }

            return metaFiles;
        }

        private void PopulateFileIdMappings(GuidMapping mapping, DirectoryPath mainAssetsRoot, DirectoryPath subordinateAssetsRoot)
        {
            if (mapping == null)
            {
                return;
            }

            if (mapping.SubordinateMainObjectFileId.HasValue && mapping.MainMainObjectFileId.HasValue)
            {
                mapping.FileIdMappings[mapping.SubordinateMainObjectFileId.Value] = mapping.MainMainObjectFileId.Value;
            }

            var assetRelativePath = RemoveMetaExtension(mapping.RelativePath);
            if (string.IsNullOrEmpty(assetRelativePath))
            {
                return;
            }

            var mainAssetPath = Path.Combine(mainAssetsRoot, assetRelativePath);
            var subordinateAssetPath = Path.Combine(subordinateAssetsRoot, assetRelativePath);

            if (!File.Exists(mainAssetPath) || !File.Exists(subordinateAssetPath))
            {
                return;
            }

            if (!UnityFileUtils.IsUnityYamlFile(mainAssetPath) || !UnityFileUtils.IsUnityYamlFile(subordinateAssetPath))
            {
                return;
            }

            var mainFileIds = FileIdParser.ExtractFileIdDefinitions(mainAssetPath);
            var subordinateFileIds = FileIdParser.ExtractFileIdDefinitions(subordinateAssetPath);

            if (mainFileIds.Count == 0 || subordinateFileIds.Count == 0)
            {
                return;
            }

            var perAssetMapping = FileIdParser.CreateFileIdMapping(subordinateFileIds, mainFileIds);
            foreach (var kvp in perAssetMapping)
            {
                mapping.FileIdMappings[kvp.Key] = kvp.Value;
            }
        }

        private static RelativePath RemoveMetaExtension(RelativePath relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return relativePath;
            }

            const string metaExtension = ".meta";
            return relativePath.EndsWith(metaExtension, StringComparison.OrdinalIgnoreCase)
                ? relativePath.Substring(0, relativePath.Length - metaExtension.Length)
                : relativePath;
        }

        private static bool IsSharePrivatePath(string absolutePath, string assetsRoot)
        {
            if (string.IsNullOrEmpty(absolutePath) || string.IsNullOrEmpty(assetsRoot))
            {
                return false;
            }

            var unityPath = UnityPathUtils.FromAbsolute(absolutePath, assetsRoot);
            return IsSharePrivateUnityPath(unityPath);
        }

        private static bool IsSharePrivateUnityPath(string unityPath)
        {
            if (string.IsNullOrEmpty(unityPath))
            {
                return false;
            }

            var normalized = UnityPathUtils.NormalizeAssetPath(unityPath);
            if (!normalized.StartsWith("Assets/Share/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Check for private folder segments or their meta files
            if (normalized.IndexOf("/" + SyncOperationPlanner.PrivateFolderSuffix + "/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (normalized.EndsWith(SyncOperationPlanner.PrivateFolderSuffix + ".meta", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private RelativePath GetRelativePath(DirectoryPath basePath, FilePath fullPath)
        {
            var baseUri = new Uri(basePath.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? basePath
                : basePath + Path.DirectorySeparatorChar);
            var fullUri = new Uri(fullPath);
            var relativeUri = baseUri.MakeRelativeUri(fullUri);
            return Uri.UnescapeDataString(relativeUri.ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private UpdateResult UpdateReferences(bool applyChanges)
        {
            // Create mapping from old GUIDs to new GUIDs
            var guidRemapping = _guidMappings.Values.ToDictionary<GuidMapping, Guid, Guid>(
                m => m.SubordinateGuid,
                m => m.MainGuid
            );

            // Create FileID mappings per GUID
            var fileIdMappings = new Dictionary<Guid, Dictionary<FileID, FileID>>();
            foreach (var mapping in _guidMappings.Values)
            {
                if (mapping.FileIdMappings.Count == 0)
                    continue;

                if (!fileIdMappings.TryGetValue(mapping.SubordinateGuid, out var perGuidMapping))
                {
                    perGuidMapping = new Dictionary<FileID, FileID>();
                    fileIdMappings[mapping.SubordinateGuid] = perGuidMapping;
                }

                foreach (var kvp in mapping.FileIdMappings)
                {
                    perGuidMapping[kvp.Key] = kvp.Value;
                }
            }

            // Track which files had which GUIDs updated
            var fileUpdates = new Dictionary<FilePath, HashSet<Guid>>();
            var fileIdUpdates = new Dictionary<FilePath, List<(Guid guid, FileID oldFileId, FileID newFileId)>>();

            foreach (var filePath in Directory.GetFiles(_mainProjectPath, "*", SearchOption.AllDirectories))
            {
                if (MetaFileParser.IsMetaFile(filePath))
                    continue;

                if (!UnityFileUtils.IsUnityYamlFile(filePath))
                    continue;

                var replacedGuids = UpdateFileReferences(filePath, guidRemapping, fileIdMappings, fileIdUpdates, applyChanges);
                if (replacedGuids.Count > 0)
                {
                    fileUpdates[filePath] = replacedGuids;
                }
            }

            var result = new UpdateResult();
            foreach (var kvp in fileUpdates)
            {
                result.FileUpdates[kvp.Key] = kvp.Value;
            }

            foreach (var kvp in fileIdUpdates)
            {
                result.FileIdUpdates[kvp.Key] = kvp.Value;
            }

            return result;
        }


        /// Updates GUID references in a single file
        /// Returns the set of GUIDs that were replaced in the file
        private static HashSet<Guid> UpdateFileReferences(
            FilePath filePath,
            Dictionary<Guid, Guid> guidMappings,
            Dictionary<Guid, Dictionary<FileID, FileID>> fileIdMappings,
            Dictionary<FilePath, List<(Guid guid, FileID oldFileId, FileID newFileId)>> fileIdUpdates,
            bool applyChanges)
        {
            var replacedGuids = new HashSet<Guid>();

            try
            {
                string content = File.ReadAllText(filePath);

                // For non-meta YAML assets, update compound fileID/guid references first so we operate on the original GUID values
                if (!MetaFileParser.IsMetaFile(filePath))
                {
                    content = FileIdGuidRegex.Replace(content, match =>
                    {
                        var oldFileId = long.Parse(match.Groups[1].Value);
                        var oldGuid = match.Groups[2].Value;
                        var typeId = match.Groups[3].Value;

                        if (guidMappings.TryGetValue(oldGuid, out var newGuid))
                        {
                            replacedGuids.Add(oldGuid);

                            // Check if we have a FileID mapping for this GUID
                            var finalFileId = oldFileId;
                            if (fileIdMappings != null && fileIdMappings.TryGetValue(oldGuid, out var fileIdMap))
                            {
                                if (fileIdMap.TryGetValue(oldFileId, out var mappedFileId))
                                {
                                    finalFileId = mappedFileId;
                                }
                            }

                            // Build the replacement string with updated GUID and FileID
                            if (finalFileId != oldFileId)
                            {
                                fileIdUpdates ??= new Dictionary<FilePath, List<(Guid, FileID, FileID)>>();
                                if (!fileIdUpdates.TryGetValue(filePath, out var updates))
                                {
                                    updates = new List<(Guid, FileID, FileID)>();
                                    fileIdUpdates[filePath] = updates;
                                }

                                if (!updates.Exists(t => t.guid == oldGuid && t.oldFileId == oldFileId && t.newFileId == finalFileId))
                                {
                                    updates.Add((oldGuid, oldFileId, finalFileId));
                                }
                            }

                            return $"{{fileID: {finalFileId}, guid: {newGuid}, type: {typeId}}}";
                        }
                        return match.Value;
                    });
                }

                // Always check for simple GUID pattern (used in meta files and some asset files)
                content = SimpleGuidRegex.Replace(content, match =>
                {
                    var oldGuid = match.Groups[1].Value;
                    if (guidMappings.TryGetValue(oldGuid, out var newGuid))
                    {
                        replacedGuids.Add(oldGuid);
                        return $"guid: {newGuid}";
                    }
                    return match.Value;
                });

                if (replacedGuids.Count > 0 && applyChanges)
                {
                    File.WriteAllText(filePath, content);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to update references in {filePath}: {ex.Message}");
            }

            return replacedGuids;
        }

    }
}
