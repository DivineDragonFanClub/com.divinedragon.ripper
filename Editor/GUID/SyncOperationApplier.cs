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

        public static HashSet<string> EnsureDirectories(IEnumerable<string> directories)
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

        public static void ExecuteCopies(IEnumerable<CopyAssetOperation> copies, bool forceImport)
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

                    var directory = Path.GetDirectoryName(copy.TargetPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
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

        public static void ApplyScriptRemappings(string targetDir, IEnumerable<ScriptRemapOperation> remaps)
        {
            if (remaps == null)
            {
                return;
            }

            var remapList = remaps.ToList();
            if (remapList.Count == 0)
            {
                return;
            }

            foreach (var group in remapList.GroupBy(r => r.TargetAssetPath))
            {
                var unityPath = group.Key;
                var absolutePath = ConvertUnityToAbsolutePath(unityPath, targetDir);
                if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                {
                    continue;
                }

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
                    {
                        return match.Value;
                    }

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

        private static string ConvertUnityToAbsolutePath(string unityPath, string assetsRoot)
        {
            if (string.IsNullOrEmpty(unityPath))
            {
                return unityPath;
            }

            if (Path.IsPathRooted(unityPath))
            {
                return unityPath;
            }

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
    }
}
