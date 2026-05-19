using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace DivineDragon
{
    public static class SyncOperationRunner
    {
        private static readonly Regex GuidRegex = new Regex(@"guid:\s*([a-f0-9]{32})", RegexOptions.Compiled);

        public static void Run(
            string targetDir,
            string sourceDir,
            SyncOperations operations,
            IEnumerable<string> directoriesToCreate,
            IEnumerable<ScriptUtils.ScriptMapping> stubMappings)
        {
            if (operations == null) throw new ArgumentNullException(nameof(operations));
            if (string.IsNullOrEmpty(targetDir)) throw new ArgumentException("Target directory is required", nameof(targetDir));
            if (string.IsNullOrEmpty(sourceDir)) throw new ArgumentException("Source directory is required", nameof(sourceDir));

            var timing = operations.Timing;

            // Directory creation
            var dirStopwatch = Stopwatch.StartNew();
            var createdDirectories = EnsureDirectories(directoriesToCreate);
            dirStopwatch.Stop();
            Debug.Log($"[GUID Sync] Directory creation took: {dirStopwatch.ElapsedMilliseconds}ms");
            timing.DirectoryCreateMs = dirStopwatch.ElapsedMilliseconds;

            // File copying
            var copyStopwatch = Stopwatch.StartNew();
            ExecuteCopies(operations.Copies);
            copyStopwatch.Stop();
            Debug.Log($"[GUID Sync] File copying took: {copyStopwatch.ElapsedMilliseconds}ms ({operations.Copies.Count} files)");
            timing.CopyMs = copyStopwatch.ElapsedMilliseconds;

            // Decode AssetRipper's \uXXXX escapes in double-quoted YAML scalars to raw UTF-8.
            // Must run before the GUID synchronizer's read/write/regex pass so the in-memory
            // string and the file on disk stay byte-aligned with what the synchronizer expects.
            var normalizeStopwatch = Stopwatch.StartNew();
            int normalizedFiles = NormalizeYamlEscapes(operations.Copies);
            normalizeStopwatch.Stop();
            Debug.Log($"[GUID Sync] YAML string normalization took: {normalizeStopwatch.ElapsedMilliseconds}ms ({normalizedFiles} files modified)");

            var scriptStopwatch = Stopwatch.StartNew();
            var scriptRemaps = ApplyScriptRemapsInPlace(sourceDir, targetDir, operations.Copies, stubMappings);
            if (scriptRemaps.Count > 0)
            {
                operations.ScriptRemaps.AddRange(scriptRemaps);
            }
            scriptStopwatch.Stop();
            Debug.Log($"[GUID Sync] Script remapping took: {scriptStopwatch.ElapsedMilliseconds}ms ({scriptRemaps.Count} remaps)");
            timing.ScriptRemapMs = scriptStopwatch.ElapsedMilliseconds;

            var guidStopwatch = Stopwatch.StartNew();
            var synchronizer = new GuidSynchronizer(targetDir, sourceDir);
            synchronizer.Synchronize(operations);
            guidStopwatch.Stop();
            Debug.Log($"[GUID Sync] Total GUID sync took: {guidStopwatch.ElapsedMilliseconds}ms");
            timing.GuidTotalMs = guidStopwatch.ElapsedMilliseconds;
            // Legacy fields kept populated for the report window: total goes to Apply, Analyze is now a no-op.
            timing.GuidAnalyzeMs = 0;
            timing.GuidApplyMs = guidStopwatch.ElapsedMilliseconds;

            // Cleanup
            var cleanupStopwatch = Stopwatch.StartNew();
            CleanupEmptyDirectories(createdDirectories);
            cleanupStopwatch.Stop();
            Debug.Log($"[GUID Sync] Directory cleanup took: {cleanupStopwatch.ElapsedMilliseconds}ms");
            timing.CleanupMs = cleanupStopwatch.ElapsedMilliseconds;
        }

        private static HashSet<string> EnsureDirectories(IEnumerable<string> directories)
        {
            var created = new HashSet<string>();

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

        private static void ExecuteCopies(IEnumerable<CopyAssetOperation> copies)
        {
            if (copies == null)
            {
                return;
            }

            var copyList = copies as IList<CopyAssetOperation> ?? copies.ToList();
            if (copyList.Count == 0)
            {
                return;
            }

            // File copies are independent (different paths, different bytes) so this is
            // embarrassingly parallel. Capped at ProcessorCount to avoid trashing the disk.
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
            var failures = new ConcurrentQueue<(string source, string target, Exception ex)>();

            Parallel.ForEach(copyList, parallelOptions, copy =>
            {
                try
                {
                    File.Copy(copy.SourcePath, copy.TargetPath, true);
                }
                catch (Exception ex)
                {
                    failures.Enqueue((copy.SourcePath, copy.TargetPath, ex));
                }
            });

            if (!failures.IsEmpty)
            {
                foreach (var (source, target, ex) in failures)
                {
                    Debug.LogError($"Failed to copy asset {source} -> {target}: {ex.Message}");
                }
                throw failures.First().ex;
            }
        }

        private static int NormalizeYamlEscapes(IEnumerable<CopyAssetOperation> copies)
        {
            if (copies == null)
            {
                return 0;
            }

            var copyList = copies as IList<CopyAssetOperation> ?? copies.ToList();
            if (copyList.Count == 0)
            {
                return 0;
            }

            int modifiedCount = 0;
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

            Parallel.ForEach(copyList, parallelOptions, copy =>
            {
                if (copy.Kind == FileType.Meta) return;
                if (string.IsNullOrEmpty(copy.TargetPath) || !File.Exists(copy.TargetPath)) return;
                if (!UnityFileUtils.ShouldScanForReferences(copy.TargetPath)) return;

                if (YamlStringNormalizer.NormalizeFile(copy.TargetPath))
                {
                    System.Threading.Interlocked.Increment(ref modifiedCount);
                }
            });

            return modifiedCount;
        }

        private static List<ScriptRemapOperation> ApplyScriptRemapsInPlace(
            string sourceDir,
            string targetDir,
            IEnumerable<CopyAssetOperation> copies,
            IEnumerable<ScriptUtils.ScriptMapping> stubMappings)
        {
            var results = new List<ScriptRemapOperation>();
            if (copies == null || stubMappings == null)
                return results;

            var guidMap = new Dictionary<string, ScriptUtils.ScriptMapping>();
            foreach (var mapping in stubMappings)
            {
                if (!string.IsNullOrEmpty(mapping.StubGuid))
                    guidMap[mapping.StubGuid] = mapping;
            }
            if (guidMap.Count == 0)
                return results;

            var copyList = copies as IList<CopyAssetOperation> ?? copies.ToList();
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
            var perFileResults = new ConcurrentBag<ScriptRemapOperation>();

            Parallel.ForEach(copyList, parallelOptions, copy =>
            {
                if (copy.Kind == FileType.Meta) return;
                if (string.IsNullOrEmpty(copy.TargetPath) || !File.Exists(copy.TargetPath)) return;

                string content;
                try { content = File.ReadAllText(copy.TargetPath); }
                catch { return; }

                if (string.IsNullOrEmpty(content)) return;
                // Fast-reject files that contain no GUID references at all (binary blobs, simple SOs, etc.)
                if (content.IndexOf("guid:", StringComparison.Ordinal) < 0) return;

                var recordedForFile = new HashSet<string>();
                bool modified = false;

                string newContent = GuidRegex.Replace(content, match =>
                {
                    var oldGuid = match.Groups[1].Value;
                    if (!guidMap.TryGetValue(oldGuid, out var mapping))
                        return match.Value;

                    var recordKey = $"{copy.UnityPath}|{mapping.StubGuid}|{mapping.RealGuid}";
                    if (recordedForFile.Add(recordKey))
                    {
                        perFileResults.Add(new ScriptRemapOperation
                        {
                            TargetAssetPath = copy.UnityPath,
                            ScriptType = mapping.TypeName,
                            StubGuid = mapping.StubGuid,
                            RealGuid = mapping.RealGuid,
                            StubScriptPath = UnityPathUtils.FromAbsolute(mapping.StubPath, sourceDir),
                            RealScriptPath = UnityPathUtils.FromAbsolute(mapping.RealPath, targetDir)
                        });
                    }

                    modified = true;
                    return $"guid: {mapping.RealGuid}";
                });

                if (modified)
                {
                    try { File.WriteAllText(copy.TargetPath, newContent); }
                    catch (Exception ex) { Debug.LogWarning($"Failed to rewrite script remaps in {copy.TargetPath}: {ex.Message}"); }
                }
            });

            results.AddRange(perFileResults);
            return results;
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
