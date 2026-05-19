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
    /// <summary>
    /// Detects GUID references in main-project YAML that resolve to no <c>.meta</c> file in
    /// either the main project or the AssetRipper output. These are dead references that the
    /// normal sync pass cannot fix, because the orphan GUID exists nowhere — there's no path-
    /// matched mapping to remap against.
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

            // 1. Build the "known GUIDs" set from every .meta file we can see, in both projects.
            //    A reference is an orphan iff its GUID isn't in this set.
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

            // 3. Walk every YAML file in main and collect orphan refs. Read in parallel because
            //    each file is independent; merge results into a single list at the end.
            var candidates = new List<string>();
            foreach (var filePath in Directory.EnumerateFiles(mainAssetsPath, "*", SearchOption.AllDirectories))
            {
                if (MetaFileParser.IsMetaFile(filePath)) continue;
                if (!UnityFileUtils.ShouldScanForReferences(filePath)) continue;
                candidates.Add(filePath);
            }

            var perFileOrphans = new ConcurrentBag<OrphanReferenceOperation>();
            // Files we rewrote get re-imported by Unity after StopAssetEditing — but we still log
            // them explicitly so they're visible in the sync report when re-opened.
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

            Parallel.ForEach(candidates, parallelOptions, filePath =>
            {
                // ScanAndApply does the read, the rewrite (if any orphans have suggestions), and
                // the write all in one pass — so a file with 3 orphan refs to the same dead atlas
                // only round-trips through disk once.
                var ops = ScanAndApply(filePath, mainAssetsPath, knownGuids, mainIndex);
                foreach (var op in ops)
                {
                    perFileOrphans.Add(op);
                }
            });

            // 4. Merge + sort for stable reporting (alphabetical by referencing asset, then by line).
            var ordered = perFileOrphans
                .OrderBy(o => o.AssetPath, StringComparer.Ordinal)
                .ThenBy(o => o.LineNumber)
                .ToList();
            operations.OrphanReferences.AddRange(ordered);

            totalStopwatch.Stop();

            int autoFixed = ordered.Count(o => o.WasAutoFixed);
            int withSuggestion = ordered.Count(o => !string.IsNullOrEmpty(o.SuggestedGuid));
            int unresolved = ordered.Count - autoFixed;
            Debug.Log($"[GUID Sync] OrphanReferenceScanner: scanned {candidates.Count} files in {totalStopwatch.ElapsedMilliseconds}ms — {ordered.Count} orphan references ({autoFixed} auto-fixed, {unresolved} need manual review, {withSuggestion - autoFixed} suggestions that failed to apply)");

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
        /// Read the file once, collect orphan refs, run filename-proximity suggestions, and — if
        /// any of the orphans landed on a suggestion — rewrite the file in place with all the
        /// applicable substitutions applied at once. Returning the ops with WasAutoFixed already
        /// set keeps the caller's bookkeeping trivial.
        /// </summary>
        private static IEnumerable<OrphanReferenceOperation> ScanAndApply(
            string filePath,
            string mainAssetsPath,
            HashSet<string> knownGuids,
            FilenameIndex mainIndex)
        {
            string content;
            try { content = File.ReadAllText(filePath); }
            catch { return Array.Empty<OrphanReferenceOperation>(); }

            // Fast-reject files that contain no `guid:` substring at all — they can't have orphan refs.
            if (string.IsNullOrEmpty(content) || content.IndexOf("guid:", StringComparison.Ordinal) < 0)
            {
                return Array.Empty<OrphanReferenceOperation>();
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

            if (perGuid.Count == 0) return Array.Empty<OrphanReferenceOperation>();

            var unityPath = ToUnityPath(filePath, mainAssetsPath);
            var refBaseName = Path.GetFileNameWithoutExtension(filePath); // e.g. "SystemBold SDF"

            // Build the ops + collect substitutions in one pass.
            var ops = new List<OrphanReferenceOperation>(perGuid.Count);
            Dictionary<string, string> substitutions = null;
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

                if (!string.IsNullOrEmpty(op.SuggestedGuid) && !string.Equals(op.SuggestedGuid, op.OrphanGuid, StringComparison.OrdinalIgnoreCase))
                {
                    if (substitutions == null)
                        substitutions = new Dictionary<string, string>(StringComparer.Ordinal);
                    substitutions[op.OrphanGuid] = op.SuggestedGuid;
                }
            }

            // No actionable suggestions → return read-only orphan list.
            if (substitutions == null) return ops;

            // Apply all substitutions on the original content. Single linear regex pass so a
            // freshly-substituted GUID never participates as a match for a later substitution.
            // We know substitutions is non-empty and each key was already found in this file, so
            // the rewritten content is guaranteed to differ — no need to compare back.
            string rewritten = ApplySubstitutions(content, substitutions);

            try
            {
                File.WriteAllText(filePath, rewritten);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GUID Sync] Could not write orphan-fix to {filePath}: {ex.Message}");
                return ops;
            }

            // Mark every op whose orphan GUID was in the substitution set as fixed.
            foreach (var op in ops)
            {
                if (substitutions.ContainsKey(op.OrphanGuid))
                {
                    op.WasAutoFixed = true;
                }
            }
            return ops;
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
