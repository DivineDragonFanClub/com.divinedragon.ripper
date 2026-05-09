using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

// Type aliases for clarity
using Guid = System.String;
using FilePath = System.String;
using RelativePath = System.String;
using DirectoryPath = System.String;
using FileID = System.Int64;
using FileIdRemapKey = System.ValueTuple<string, long, long>;

namespace DivineDragon
{
    /// GUID "synchronization" that coordinates the remapping of GUIDs
    /// from subordinate project (imported assets) to main project (current Unity project)
    public class GuidSynchronizer
    {
        private readonly DirectoryPath _mainProjectPath;
        private readonly DirectoryPath _subordinateProjectPath;
        private readonly Dictionary<RelativePath, GuidMapping> _guidMappings;

        private static readonly Dictionary<FilePath, MetaCacheEntry> _mainMetaCache
            = new Dictionary<FilePath, MetaCacheEntry>(StringComparer.Ordinal);
        private static readonly object _mainMetaCacheLock = new object();

        private readonly struct MetaCacheEntry
        {
            public readonly DateTime LastWriteTimeUtc;
            public readonly long Length;
            public readonly Guid Guid;
            public readonly FileID? MainFileId;

            public MetaCacheEntry(DateTime lastWriteTimeUtc, long length, Guid guid, FileID? mainFileId)
            {
                LastWriteTimeUtc = lastWriteTimeUtc;
                Length = length;
                Guid = guid;
                MainFileId = mainFileId;
            }
        }

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
            public Dictionary<FilePath, HashSet<FileIdRemapKey>> FileIdUpdates { get; } = new Dictionary<FilePath, HashSet<FileIdRemapKey>>();
        }

        /// Single-pass scan + update + record. Previously this ran twice (Analyze then Apply),
        /// re-doing the entire project walk both times.
        public void Synchronize(SyncOperations operations)
        {
            if (operations == null)
            {
                throw new ArgumentNullException(nameof(operations));
            }

            try
            {
                var scanStopwatch = Stopwatch.StartNew();
                ScanProjects();
                scanStopwatch.Stop();
                Debug.Log($"[GUID Sync]     ScanProjects: {scanStopwatch.ElapsedMilliseconds}ms, found {_guidMappings.Count} mappings");

                if (_guidMappings.Count == 0)
                {
                    Debug.Log($"[GUID Sync]     No GUID mappings found, skipping");
                    return;
                }

                var updateStopwatch = Stopwatch.StartNew();
                var updateResult = UpdateReferences(applyChanges: true);
                updateStopwatch.Stop();
                Debug.Log($"[GUID Sync]     UpdateReferences: {updateStopwatch.ElapsedMilliseconds}ms, processed {updateResult.FileUpdates.Count} files");

                var recordStopwatch = Stopwatch.StartNew();
                RecordOperations(updateResult, operations);
                recordStopwatch.Stop();
                Debug.Log($"[GUID Sync]     RecordOperations: {recordStopwatch.ElapsedMilliseconds}ms");
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

                foreach (var remap in kvp.Value)
                {
                    var (guid, oldFileId, newFileId) = remap;
                    var targetPath = unityPath;
                    if (mappingBySubGuid.TryGetValue(guid, out var guidMapping))
                    {
                        targetPath = ConvertRelativeAssetPath(guidMapping.RelativePath);
                    }

                    operations.FileIdRemaps.Add(new FileIdRemapOperation
                    {
                        AssetPath = targetPath,
                        Guid = guid,
                        OldFileId = oldFileId,
                        NewFileId = newFileId
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
            if (!normalized.StartsWith("Assets/"))
            {
                normalized = "Assets/" + normalized.TrimStart('/');
            }

            if (normalized.EndsWith(".meta"))
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

            int assetsIndex = filePath.IndexOf("Assets");
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

            var mainMetaFiles = ScanMetaFiles(_mainProjectPath, skipSharePrivate: true, useCache: true);
            var subordinateMetaFiles = ScanMetaFiles(_subordinateProjectPath, useCache: false);

            // Find the (relativePath -> mapping) pairs that actually need a FileID populate.
            // Cheap scan first, then run the per-mapping file reads in parallel.
            var pending = new List<GuidMapping>();
            foreach (var kvp in mainMetaFiles)
            {
                var relativePath = kvp.Key;
                var (mainGuid, mainMainFileId) = kvp.Value;

                if (!subordinateMetaFiles.TryGetValue(relativePath, out var subData))
                    continue;

                var (subGuid, subMainFileId) = subData;
                if (mainGuid == subGuid)
                    continue;

                pending.Add(new GuidMapping
                {
                    RelativePath = relativePath,
                    MainGuid = mainGuid,
                    SubordinateGuid = subGuid,
                    MainMainObjectFileId = mainMainFileId,
                    SubordinateMainObjectFileId = subMainFileId
                });
            }

            var parallelOptions = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
            System.Threading.Tasks.Parallel.ForEach(pending, parallelOptions, mapping =>
            {
                PopulateFileIdMappings(mapping, _mainProjectPath, _subordinateProjectPath);
            });

            foreach (var mapping in pending)
            {
                _guidMappings[mapping.RelativePath] = mapping;
            }
        }

        private static DirectoryPath NormalizeAssetsPath(DirectoryPath projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
            {
                return projectPath;
            }

            var trimmed = projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.EndsWith("Assets"))
            {
                return trimmed;
            }

            return Path.Combine(trimmed, "Assets");
        }

        private Dictionary<RelativePath, (Guid guid, FileID? fileId)> ScanMetaFiles(DirectoryPath projectPath, bool skipSharePrivate = false, bool useCache = false)
        {
            var metaFiles = new Dictionary<RelativePath, (Guid, FileID?)>();

            // We only care about the Assets folder - that's where all the actual content that needs syncing lives
            // projectPath should already be pointing to the Assets folder, but guarding
            string assetsPath = projectPath;
            if (!projectPath.EndsWith("Assets"))
            {
                assetsPath = Path.Combine(projectPath, "Assets");
            }

            if (!Directory.Exists(assetsPath))
            {
                Debug.LogWarning($"Assets folder not found at: {assetsPath}");
                return metaFiles;
            }

            int cacheHits = 0, cacheMisses = 0;

            foreach (var filePath in Directory.EnumerateFiles(assetsPath, "*.meta", SearchOption.AllDirectories))
            {
                if (skipSharePrivate && IsSharePrivatePath(filePath, assetsPath))
                {
                    continue;
                }

                Guid guid = null;
                FileID? fileId = null;
                bool resolved = false;

                if (useCache)
                {
                    FileInfo info;
                    try { info = new FileInfo(filePath); }
                    catch { info = null; }

                    if (info != null && info.Exists)
                    {
                        var mtime = info.LastWriteTimeUtc;
                        var length = info.Length;

                        lock (_mainMetaCacheLock)
                        {
                            if (_mainMetaCache.TryGetValue(filePath, out var cached) &&
                                cached.LastWriteTimeUtc == mtime &&
                                cached.Length == length)
                            {
                                guid = cached.Guid;
                                fileId = cached.MainFileId;
                                resolved = true;
                                cacheHits++;
                            }
                        }

                        if (!resolved && MetaFileParser.TryGetGuidAndMainFileId(filePath, out guid, out fileId))
                        {
                            lock (_mainMetaCacheLock)
                            {
                                _mainMetaCache[filePath] = new MetaCacheEntry(mtime, length, guid, fileId);
                            }
                            resolved = true;
                            cacheMisses++;
                        }
                    }
                }
                else
                {
                    resolved = MetaFileParser.TryGetGuidAndMainFileId(filePath, out guid, out fileId);
                }

                if (resolved)
                {
                    var relativePath = GetRelativePath(assetsPath, filePath);
                    metaFiles[relativePath] = (guid, fileId);
                }
            }

            if (useCache)
            {
                Debug.Log($"[GUID Sync]     Meta-file cache: {cacheHits} hits, {cacheMisses} misses");
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
            return relativePath.EndsWith(metaExtension)
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
            if (!normalized.StartsWith("Assets/Share/"))
            {
                return false;
            }

            // Check for private folder segments or their meta files
            if (normalized.IndexOf("/" + SyncOperationPlanner.PrivateFolderSuffix + "/") >= 0)
            {
                return true;
            }

            if (normalized.EndsWith(SyncOperationPlanner.PrivateFolderSuffix + ".meta"))
            {
                return true;
            }

            return false;
        }

        private RelativePath GetRelativePath(DirectoryPath basePath, FilePath fullPath)
        {
            var separator = basePath.EndsWith(Path.DirectorySeparatorChar.ToString()) ? basePath : basePath + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(separator, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(separator.Length);
            }

            var baseUri = new Uri(separator);
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

            // Track which files had which GUIDs updated. Concurrent dictionaries because the
            // main project YAML walk runs in parallel below.
            var fileUpdates = new System.Collections.Concurrent.ConcurrentDictionary<FilePath, HashSet<Guid>>();
            var fileIdUpdates = new System.Collections.Concurrent.ConcurrentDictionary<FilePath, HashSet<FileIdRemapKey>>();

            // Materialize the filtered file list once so Parallel.ForEach can partition it.
            var candidates = new List<FilePath>();
            foreach (var filePath in Directory.EnumerateFiles(_mainProjectPath, "*", SearchOption.AllDirectories))
            {
                if (MetaFileParser.IsMetaFile(filePath))
                    continue;

                if (!UnityFileUtils.ShouldScanForReferences(filePath))
                    continue;

                candidates.Add(filePath);
            }

            // Each iteration is independent: read+regex+write a single file. Each call
            // returns its own state which we merge into the concurrent dictionaries.
            var parallelOptions = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
            System.Threading.Tasks.Parallel.ForEach(candidates, parallelOptions, filePath =>
            {
                var (replacedGuids, perFileIdRemaps) = UpdateFileReferences(filePath, guidRemapping, fileIdMappings, applyChanges);
                if (replacedGuids.Count > 0)
                {
                    fileUpdates[filePath] = replacedGuids;
                }
                if (perFileIdRemaps.Count > 0)
                {
                    fileIdUpdates[filePath] = perFileIdRemaps;
                }
            });

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


        /// Updates GUID references in a single file. Returns both the set of replaced GUIDs
        /// and any FileID remaps produced for this file. Returning state instead of mutating
        /// a shared dictionary makes this safe to call in parallel.
        private static (HashSet<Guid> replacedGuids, HashSet<FileIdRemapKey> fileIdRemaps) UpdateFileReferences(
            FilePath filePath,
            Dictionary<Guid, Guid> guidMappings,
            Dictionary<Guid, Dictionary<FileID, FileID>> fileIdMappings,
            bool applyChanges)
        {
            var replacedGuids = new HashSet<Guid>();
            var fileIdRemaps = new HashSet<FileIdRemapKey>();

            try
            {
                string content = File.ReadAllText(filePath);

                // Fast-reject: if the file has no `guid:` substring, neither regex can match.
                if (content.IndexOf("guid:", StringComparison.Ordinal) < 0)
                {
                    return (replacedGuids, fileIdRemaps);
                }

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

                            var finalFileId = oldFileId;
                            if (fileIdMappings != null && fileIdMappings.TryGetValue(oldGuid, out var fileIdMap))
                            {
                                if (fileIdMap.TryGetValue(oldFileId, out var mappedFileId))
                                {
                                    finalFileId = mappedFileId;
                                }
                            }

                            if (finalFileId != oldFileId)
                            {
                                fileIdRemaps.Add((oldGuid, oldFileId, finalFileId));
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

            return (replacedGuids, fileIdRemaps);
        }

    }
}
