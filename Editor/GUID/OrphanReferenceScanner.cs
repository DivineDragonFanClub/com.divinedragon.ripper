using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace DivineDragon
{
    /// <summary>
    /// Detects GUID references in main-project YAML that no asset can resolve. A cheap <c>.meta</c>
    /// prefilter (main project + AssetRipper output) screens out the obvious hits; survivors are
    /// confirmed against Unity's AssetDatabase, which is authoritative and also sees packages
    /// (PackageCache, embedded, and file: packages) and built-in resources — none of which have a
    /// <c>.meta</c> the prefilter could find. A reference is an orphan only if BOTH miss, so package
    /// and built-in references are no longer false-flagged (and destructively "auto-fixed").
    ///
    /// Where possible, attaches a filename-proximity suggestion (e.g. font asset references an
    /// orphan atlas-texture GUID → search for "&lt;font basename&gt;*Atlas.png" in main).
    /// </summary>
    public static class OrphanReferenceScanner
    {
        // Same patterns used elsewhere in the ripper for inline and bare-key GUID forms.
        private static readonly Regex GuidRegex = new Regex(@"guid:\s*([a-f0-9]{32})", RegexOptions.Compiled);
        // Pulls "m_Foo: ..." (or any YAML key, including indented) off a line so we can attach
        // field context to each orphan. Plain regex per line — we never need YAML structure.
        private static readonly Regex KeyOnLineRegex = new Regex(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*:", RegexOptions.Compiled);

        // Field names whose values are typically texture assets. Used to bias suggestions
        // toward image extensions when the orphan is on or under one of these fields.
        private static readonly HashSet<string> TextureFieldNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_AtlasTextures", "m_Texture", "m_MainTex", "m_AtlasTexture",
            "m_Sprite", "m_Cubemap", "m_Texture2D", "m_BaseMap",
        };

        private static readonly HashSet<string> TextureExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".psd", ".tga", ".tif", ".tiff", ".exr", ".hdr", ".bmp", ".gif", ".asset"
        };

        public static void ScanAndRecord(string mainAssetsPath, string subordinateAssetsPath, SyncOperations operations)
        {
            if (operations == null) return;
            if (string.IsNullOrEmpty(mainAssetsPath) || !Directory.Exists(mainAssetsPath)) return;

            var totalStopwatch = Stopwatch.StartNew();

            // 1. Cheap PREFILTER: every .meta GUID we can see across both Assets trees. A hit means
            //    "definitely resolvable, skip"; a miss means "candidate — confirm against Unity below".
            //    The prefilter still earns its keep: assets the subordinate import just copied into the
            //    main project aren't imported into the AssetDatabase yet (we run inside StartAssetEditing),
            //    but their .meta files are already on disk, so they won't be mistaken for orphans.
            var knownGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectMetaGuids(mainAssetsPath, knownGuids);
            if (!string.IsNullOrEmpty(subordinateAssetsPath) && Directory.Exists(subordinateAssetsPath))
            {
                CollectMetaGuids(subordinateAssetsPath, knownGuids);
            }

            // 2. Build a filename index of the main project so we can propose a fix without
            //    re-walking the tree per orphan. Key on filename-without-extension because that's
            //    how AssetRipper outputs tend to relate (e.g. "SystemBold SDF" → "SystemBold SDF Atlas.png").
            var mainIndex = BuildMainFilenameIndex(mainAssetsPath);

            var files = new List<string>();
            foreach (var filePath in Directory.EnumerateFiles(mainAssetsPath, "*", SearchOption.AllDirectories))
            {
                if (MetaFileParser.IsMetaFile(filePath)) continue;
                if (!UnityFileUtils.ShouldScanForReferences(filePath)) continue;
                files.Add(filePath);
            }

            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

            // PHASE 1 — PARALLEL detect + suggest. No writes, no Unity API: ScanFileForOrphans only
            //    reads files and the prebuilt in-memory FilenameIndex, so it's safe to fan out.
            var perFileBag = new ConcurrentBag<FileOrphans>();
            Parallel.ForEach(files, parallelOptions, filePath =>
            {
                var found = ScanFileForOrphans(filePath, mainAssetsPath, knownGuids, mainIndex);
                if (found != null) perFileBag.Add(found);
            });
            var perFile = perFileBag.ToList(); // ForEach is a barrier — the bag is done being written.

            // PHASE 2 — MAIN-THREAD batch resolve. Check every DISTINCT candidate GUID against Unity
            //    once. This is the only main-thread-bound step (AssetDatabase is main-thread-only on
            //    2020.3). GUIDToAssetPath resolves packages (PackageCache + embedded + file:) and
            //    built-in resources — exactly what the .meta walk structurally can't see. `resolvable`
            //    is immutable after this loop, so PHASE 3 can read it from worker threads safely.
            var resolvable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var guid in perFile.SelectMany(f => f.Ops)
                                        .Select(o => o.OrphanGuid)
                                        .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
                {
                    resolvable.Add(guid);
                }
            }

            // PHASE 3 — PARALLEL patch. Same shape as the original write pass, but gated on the
            //    read-only `resolvable` set: candidates Unity can resolve are dropped (never reported,
            //    never rewritten); the rest are real orphans and get their suggested fix applied.
            int candidateOpCount = perFile.Sum(f => f.Ops.Count);
            var confirmedBag = new ConcurrentBag<OrphanReferenceOperation>();
            Parallel.ForEach(perFile, parallelOptions, file =>
            {
                var confirmed = file.Ops.Where(op => !resolvable.Contains(op.OrphanGuid)).ToList();
                if (confirmed.Count == 0) return;

                Dictionary<string, string> substitutions = null;
                foreach (var op in confirmed)
                {
                    if (string.IsNullOrEmpty(op.SuggestedGuid)) continue;
                    if (string.Equals(op.SuggestedGuid, op.OrphanGuid, StringComparison.OrdinalIgnoreCase)) continue;
                    if (substitutions == null)
                        substitutions = new Dictionary<string, string>(StringComparer.Ordinal);
                    substitutions[op.OrphanGuid] = op.SuggestedGuid;
                }

                if (substitutions != null && TryApplySubstitutions(file.AbsolutePath, substitutions))
                {
                    foreach (var op in confirmed)
                    {
                        if (substitutions.ContainsKey(op.OrphanGuid)) op.WasAutoFixed = true;
                    }
                }

                foreach (var op in confirmed) confirmedBag.Add(op);
            });

            // Merge + sort for stable reporting (alphabetical by referencing asset, then by line).
            var ordered = confirmedBag
                .OrderBy(o => o.AssetPath, StringComparer.Ordinal)
                .ThenBy(o => o.LineNumber)
                .ToList();
            operations.OrphanReferences.AddRange(ordered);

            totalStopwatch.Stop();

            int autoFixed = ordered.Count(o => o.WasAutoFixed);
            int unresolved = ordered.Count - autoFixed;
            int droppedResolvable = candidateOpCount - ordered.Count;
            Debug.Log($"[GUID Sync] OrphanReferenceScanner: scanned {files.Count} files in {totalStopwatch.ElapsedMilliseconds}ms — {ordered.Count} confirmed orphans ({autoFixed} auto-fixed, {unresolved} need manual review), {droppedResolvable} candidate refs dropped as Unity-resolvable (packages/built-ins)");

            // Surface the first few so they're visible without opening the report window.
            int previewCount = Math.Min(5, ordered.Count);
            for (int i = 0; i < previewCount; i++)
            {
                var o = ordered[i];
                if (o.WasAutoFixed)
                {
                    Debug.Log($"[GUID Sync] Auto-fixed orphan ref in {o.AssetPath}:{o.LineNumber} → {o.OrphanGuid} → {o.SuggestedGuid} ({o.SuggestedAssetPath})");
                }
                else if (!string.IsNullOrEmpty(o.SuggestedGuid))
                {
                    Debug.LogWarning(
                        $"[GUID Sync] Orphan ref in {o.AssetPath}:{o.LineNumber} → {o.OrphanGuid} " +
                        $"(suggested fix: {o.SuggestedGuid} = {o.SuggestedAssetPath} [{o.SuggestionReason}], could not apply — check write permissions)");
                }
                else
                {
                    Debug.LogWarning($"[GUID Sync] Orphan ref in {o.AssetPath}:{o.LineNumber} → {o.OrphanGuid} (no suggestion — manual fix required)");
                }
            }
            if (ordered.Count > previewCount)
            {
                Debug.Log($"[GUID Sync] ... and {ordered.Count - previewCount} more orphan references (see sync report)");
            }
        }

        /// <summary>
        /// Detection only: read the file once and aggregate orphan candidates — GUIDs absent from
        /// the cheap .meta prefilter — with their field context, attaching a filename-proximity
        /// suggestion to each. Deliberately does NOT touch the AssetDatabase or write anything: the
        /// main-thread batch resolve in ScanAndRecord confirms which candidates are real orphans
        /// before TryApplySubstitutions rewrites them. Returns null when the file has no candidates.
        /// </summary>
        private static FileOrphans ScanFileForOrphans(
            string filePath,
            string mainAssetsPath,
            HashSet<string> knownGuids,
            FilenameIndex mainIndex)
        {
            string content;
            try { content = File.ReadAllText(filePath); }
            catch { return null; }

            // Fast-reject files that contain no `guid:` substring at all — they can't have orphan refs.
            if (string.IsNullOrEmpty(content) || content.IndexOf("guid:", StringComparison.Ordinal) < 0)
            {
                return null;
            }

            var lines = content.Split('\n');

            // Aggregate per-guid stats inside this file, so a fontasset with 3 refs to the same
            // dead atlas becomes one OrphanReferenceOperation with Occurrences=3 rather than three.
            var perGuid = new Dictionary<string, (int firstLine, int count, string contextKey)>(StringComparer.Ordinal);

            string lastTopLevelKey = null; // most recent "key:" at any indent. Cheap parent-key tracker.

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // Track the most recent YAML key on the line so we can attach context to orphans
                // that appear on subsequent (sequence-continuation) lines.
                var keyMatch = KeyOnLineRegex.Match(line);
                if (keyMatch.Success)
                {
                    lastTopLevelKey = keyMatch.Groups[1].Value;
                }

                foreach (Match gm in GuidRegex.Matches(line))
                {
                    var guid = gm.Groups[1].Value;
                    if (knownGuids.Contains(guid)) continue;

                    string context = lastTopLevelKey ?? string.Empty;

                    if (perGuid.TryGetValue(guid, out var entry))
                    {
                        perGuid[guid] = (entry.firstLine, entry.count + 1, entry.contextKey);
                    }
                    else
                    {
                        perGuid[guid] = (i + 1, 1, context);
                    }
                }
            }

            if (perGuid.Count == 0) return null;

            var unityPath = ToUnityPath(filePath, mainAssetsPath);
            var refBaseName = Path.GetFileNameWithoutExtension(filePath); // e.g. "SystemBold SDF"

            // Build the candidate ops with suggestions attached. No substitutions or writes here —
            // PHASE 2's AssetDatabase gate decides which of these are real before PHASE 3 patches.
            var ops = new List<OrphanReferenceOperation>(perGuid.Count);
            foreach (var kvp in perGuid)
            {
                var op = new OrphanReferenceOperation
                {
                    AssetPath = unityPath,
                    OrphanGuid = kvp.Key,
                    LineNumber = kvp.Value.firstLine,
                    Occurrences = kvp.Value.count,
                };
                AttachSuggestion(op, filePath, refBaseName, kvp.Value.contextKey, mainIndex);
                ops.Add(op);
            }

            return new FileOrphans { AbsolutePath = filePath, Ops = ops };
        }

        /// <summary>
        /// Re-read and rewrite a single file, substituting confirmed-orphan GUIDs for their
        /// suggested replacements. PHASE 1 didn't write, so the on-disk content still matches what
        /// was scanned. Pure File IO + regex — no Unity API — so this is safe to run from the
        /// PHASE 3 parallel patch. Returns true iff the file was changed on disk.
        /// </summary>
        private static bool TryApplySubstitutions(string filePath, Dictionary<string, string> substitutions)
        {
            string content;
            try { content = File.ReadAllText(filePath); }
            catch { return false; }

            // Single linear regex pass so a freshly-substituted GUID never participates as a match
            // for a later substitution.
            string rewritten = ApplySubstitutions(content, substitutions);
            if (string.Equals(rewritten, content, StringComparison.Ordinal)) return false;

            try
            {
                File.WriteAllText(filePath, rewritten);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GUID Sync] Could not write orphan-fix to {filePath}: {ex.Message}");
                return false;
            }
            return true;
        }

        /// <summary>Per-file detection result: absolute path plus its orphan candidate ops.</summary>
        private sealed class FileOrphans
        {
            public string AbsolutePath;
            public List<OrphanReferenceOperation> Ops;
        }

        /// <summary>
        /// Walks the content once and replaces each occurrence of any key in <paramref name="substitutions"/>
        /// with the corresponding value. Single-pass — does not re-examine the replacement output, so a
        /// just-inserted GUID never participates as a match for a later substitution.
        /// </summary>
        private static string ApplySubstitutions(string content, Dictionary<string, string> substitutions)
        {
            // All GUIDs are 32 hex chars; we use Regex once for both finding and replacing so the
            // rewrite remains O(n) over the file rather than O(n*subs) like repeated string.Replace.
            // Faster path: regex.Replace with a callback that does a dictionary lookup.
            return GuidRegex.Replace(content, match =>
            {
                var guid = match.Groups[1].Value;
                if (substitutions.TryGetValue(guid, out var replacement))
                {
                    // Preserve the `guid: ` prefix as it appeared in the source.
                    return match.Value.Substring(0, match.Value.Length - guid.Length) + replacement;
                }
                return match.Value;
            });
        }

        /// <summary>
        /// Filename-proximity guess. For an orphan reference in file <paramref name="referencingPath"/>
        /// with base name <paramref name="refBaseName"/>, look for main-project files whose basename
        /// starts with <paramref name="refBaseName"/> (i.e. sibling assets that "belong" to the same
        /// logical asset). If we can additionally narrow by extension via the YAML field context, do
        /// that. Suggest only when there is exactly one candidate to avoid silently steering the user
        /// toward the wrong match.
        /// </summary>
        private static void AttachSuggestion(
            OrphanReferenceOperation op,
            string referencingPath,
            string refBaseName,
            string contextKey,
            FilenameIndex mainIndex)
        {
            if (string.IsNullOrEmpty(refBaseName)) return;

            string referencingFullPath = Path.GetFullPath(referencingPath);
            bool isTextureField = !string.IsNullOrEmpty(contextKey) && TextureFieldNames.Contains(contextKey);

            // Try progressively shorter prefixes so a material file like "SystemBold SDF Material"
            // still finds "SystemBold SDF Atlas.png" by trimming " Material" off the end. We accept
            // the first prefix that produces a clear winner.
            foreach (string prefix in EnumeratePrefixCandidates(refBaseName))
            {
                if (TryFindSuggestion(prefix, refBaseName, contextKey, isTextureField, referencingFullPath, mainIndex, op))
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Yields the full basename first, then strips one whitespace-delimited trailing token
        /// at a time (e.g. "SystemBold SDF Material" → "SystemBold SDF" → "SystemBold"). Stops when
        /// only the very first token remains; matching on a single token is too weak.
        /// </summary>
        private static IEnumerable<string> EnumeratePrefixCandidates(string baseName)
        {
            yield return baseName;
            int spaceCount = baseName.Count(c => c == ' ');
            for (int i = 0; i < spaceCount; i++)
            {
                int lastSpace = baseName.LastIndexOf(' ');
                if (lastSpace <= 0) yield break;
                baseName = baseName.Substring(0, lastSpace);
                // Stop once we're down to one bare token — too generic to be useful.
                if (!baseName.Contains(' ')) yield break;
                yield return baseName;
            }
        }

        private static bool TryFindSuggestion(
            string prefix,
            string refBaseNameOriginal,
            string contextKey,
            bool isTextureField,
            string referencingFullPath,
            FilenameIndex mainIndex,
            OrphanReferenceOperation op)
        {
            var prefixMatches = mainIndex.PrefixMatch(prefix);
            if (prefixMatches == null || prefixMatches.Count == 0) return false;

            // Exclude the referencing file itself — a font asset isn't its own atlas.
            var filtered = prefixMatches
                .Where(e => !string.Equals(Path.GetFullPath(e.AbsolutePath), referencingFullPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (filtered.Count == 0) return false;

            // If we recognize the YAML field as a texture-typed field, restrict candidates to
            // texture extensions. That's the case that solves the original report (the font asset's
            // m_AtlasTextures pointing at the .png and the materials' m_Texture doing the same).
            if (isTextureField)
            {
                filtered = filtered.Where(e => TextureExtensions.Contains(Path.GetExtension(e.AbsolutePath))).ToList();
                if (filtered.Count == 0) return false;
            }

            // Prefer the closest base-name match. The shortest extra-text past the prefix wins
            // (e.g. "SystemBold SDF Atlas.png" beats "SystemBoldJP SDF Atlas.png" when the
            // referencing file was specifically "SystemBold SDF Material").
            filtered.Sort((a, b) =>
            {
                var an = Path.GetFileNameWithoutExtension(a.AbsolutePath);
                var bn = Path.GetFileNameWithoutExtension(b.AbsolutePath);
                return (an.Length - prefix.Length).CompareTo(bn.Length - prefix.Length);
            });

            // Accept the top candidate if it's a clear winner: either the only result, or
            // strictly closer than the runner-up by basename length, or different extension
            // (which already implies one matches the context-filter and the other doesn't).
            bool clearWinner = filtered.Count == 1
                || filtered[0].BaseName.Length != filtered[1].BaseName.Length
                || Path.GetExtension(filtered[0].AbsolutePath) != Path.GetExtension(filtered[1].AbsolutePath);

            if (!clearWinner) return false;

            var pick = filtered[0];
            op.SuggestedAssetPath = pick.UnityPath;
            op.SuggestedGuid = pick.Guid;
            // Reason mentions the trimmed prefix when it differs from the original, so the user
            // sees how confident we were ("name match" vs. "trimmed-prefix match").
            bool trimmed = !string.Equals(prefix, refBaseNameOriginal, StringComparison.Ordinal);
            string baseReason = trimmed ? $"name match (trimmed '{prefix}')" : "name match";
            op.SuggestionReason = string.IsNullOrEmpty(contextKey)
                ? baseReason
                : $"{baseReason} + field '{contextKey}'";
            return true;
        }

        private static void CollectMetaGuids(string assetsPath, HashSet<string> sink)
        {
            foreach (var metaPath in Directory.EnumerateFiles(assetsPath, "*.meta", SearchOption.AllDirectories))
            {
                if (MetaFileParser.TryGetGuid(metaPath, out var guid) && !string.IsNullOrEmpty(guid))
                {
                    sink.Add(guid);
                }
            }
        }

        private static FilenameIndex BuildMainFilenameIndex(string mainAssetsPath)
        {
            var index = new FilenameIndex();
            foreach (var metaPath in Directory.EnumerateFiles(mainAssetsPath, "*.meta", SearchOption.AllDirectories))
            {
                var assetPath = metaPath.Substring(0, metaPath.Length - ".meta".Length);
                if (!File.Exists(assetPath)) continue; // dangling .meta — skip
                if (!MetaFileParser.TryGetGuid(metaPath, out var guid) || string.IsNullOrEmpty(guid)) continue;

                index.Add(new FilenameEntry
                {
                    BaseName = Path.GetFileNameWithoutExtension(assetPath),
                    AbsolutePath = assetPath,
                    UnityPath = ToUnityPath(assetPath, mainAssetsPath),
                    Guid = guid,
                });
            }
            return index;
        }

        private static string ToUnityPath(string absolutePath, string mainAssetsPath)
        {
            // Mirror UnityPathUtils.FromAbsolute behaviour: produce "Assets/..." regardless of which
            // path separator the OS is using, and tolerate trailing slashes on the root.
            var normalized = absolutePath.Replace('\\', '/');
            var rootNormalized = mainAssetsPath.Replace('\\', '/').TrimEnd('/');

            int idx = normalized.IndexOf(rootNormalized, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return normalized;

            var tail = normalized.Substring(idx + rootNormalized.Length).TrimStart('/');
            return "Assets/" + tail;
        }

        // -- Filename index -------------------------------------------------------

        private struct FilenameEntry
        {
            public string BaseName;
            public string AbsolutePath;
            public string UnityPath;
            public string Guid;
        }

        /// <summary>
        /// Buckets main-project files by the first whitespace-delimited token of their basename so
        /// the prefix lookup is fast — "SystemBold SDF Atlas.png" lands in the "SystemBold" bucket
        /// and a query for "SystemBold SDF" only scans that bucket.
        /// </summary>
        private class FilenameIndex
        {
            private readonly Dictionary<string, List<FilenameEntry>> _byFirstToken
                = new Dictionary<string, List<FilenameEntry>>(StringComparer.OrdinalIgnoreCase);

            public void Add(FilenameEntry entry)
            {
                if (string.IsNullOrEmpty(entry.BaseName)) return;
                string token = FirstToken(entry.BaseName);
                if (!_byFirstToken.TryGetValue(token, out var list))
                {
                    list = new List<FilenameEntry>();
                    _byFirstToken[token] = list;
                }
                list.Add(entry);
            }

            public List<FilenameEntry> PrefixMatch(string baseName)
            {
                if (string.IsNullOrEmpty(baseName)) return null;
                string token = FirstToken(baseName);
                if (!_byFirstToken.TryGetValue(token, out var list)) return null;
                var result = new List<FilenameEntry>();
                foreach (var e in list)
                {
                    if (e.BaseName.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(e);
                    }
                }
                return result;
            }

            private static string FirstToken(string baseName)
            {
                int space = baseName.IndexOf(' ');
                return space < 0 ? baseName : baseName.Substring(0, space);
            }
        }
    }
}
