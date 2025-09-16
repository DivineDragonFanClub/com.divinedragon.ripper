using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace DivineDragon
{
    /// GUID "synchronization" that coordinates the remapping of GUIDs
    /// from subordinate project (imported assets) to main project (current Unity project)
    public class GuidSynchronizer
    {
        private readonly string _mainProjectPath;
        private readonly string _subordinateProjectPath;
        private readonly Dictionary<string, GuidMapping> _guidMappings;

        // Matches: guid: 0123456789abcdef0123456789abcdef
        // Captures the GUID (group 1)
        private static readonly Regex SimpleGuidRegex = new Regex(@"guid:\s*([a-f0-9]{32})", RegexOptions.Compiled);

        // Matches: {fileID: 123456789, guid: 0123456789abcdef0123456789abcdef, type: 3}
        // Captures the GUID (group 1)
        private static readonly Regex FileIdGuidRegex = new Regex(@"\{fileID:\s*\d+,\s*guid:\s*([a-f0-9]{32}),\s*type:\s*\d+\}", RegexOptions.Compiled);

        // More would need to be added if other formats are discovered

        private class GuidMapping
        {
            public string RelativePath { get; set; }
            public string MainGuid { get; set; }
            public string SubordinateGuid { get; set; }
        }

        public GuidSynchronizer(string mainProjectPath, string subordinateProjectPath)
        {
            _mainProjectPath = mainProjectPath;
            _subordinateProjectPath = subordinateProjectPath;
            _guidMappings = new Dictionary<string, GuidMapping>();
        }

        public GuidSyncReport Synchronize()
        {
            Debug.Log("Starting GUID synchronization...");
            var report = new GuidSyncReport();

            try
            {
                ScanProjects();

                if (_guidMappings.Count == 0)
                {
                    Debug.Log("No GUID differences found. Assets are already synchronized.");
                    report.FinalizeReport();
                    return report;
                }

                Debug.Log($"Found {_guidMappings.Count} GUID differences to synchronize");

                if (_guidMappings.Count > 0)
                {
                    UpdateReferences(report);
                }

                report.FinalizeReport();

                if (report.Mappings.Count > 0)
                {
                    Debug.Log($"GUID Sync Complete: {report.Mappings.Count} UUID mappings updated");
                }

                return report;
            }
            catch (Exception ex)
            {
                Debug.LogError($"GUID synchronization failed: {ex.Message}");
                throw;
            }
        }

        private void ScanProjects()
        {
            Debug.Log("Scanning projects for GUID mappings...");

            var mainMetaFiles = ScanMetaFiles(_mainProjectPath);
            var subordinateMetaFiles = ScanMetaFiles(_subordinateProjectPath);

            foreach (var kvp in mainMetaFiles)
            {
                var relativePath = kvp.Key;
                var mainGuid = kvp.Value;

                if (subordinateMetaFiles.TryGetValue(relativePath, out var subGuid))
                {
                    if (mainGuid != subGuid)
                    {
                        _guidMappings[relativePath] = new GuidMapping
                        {
                            RelativePath = relativePath,
                            MainGuid = mainGuid,
                            SubordinateGuid = subGuid
                        };
                        // Pretty much guaranteed to be different since they are from different projects, but
                        // just in case we import from the same export or something...
                        Debug.Log($"GUID difference found for {relativePath}: {subGuid} -> {mainGuid}");
                    }
                }
            }
        }

        private Dictionary<string, string> ScanMetaFiles(string projectPath)
        {
            var metaFiles = new Dictionary<string, string>();

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

            foreach (var filePath in Directory.GetFiles(assetsPath, "*.meta", SearchOption.AllDirectories))
            {
                if (MetaFileParser.TryGetGuid(filePath, out var guid))
                {
                    var relativePath = GetRelativePath(assetsPath, filePath);
                    metaFiles[relativePath] = guid;
                }
            }

            return metaFiles;
        }
        private string GetRelativePath(string basePath, string fullPath)
        {
            var baseUri = new Uri(basePath.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? basePath
                : basePath + Path.DirectorySeparatorChar);
            var fullUri = new Uri(fullPath);
            var relativeUri = baseUri.MakeRelativeUri(fullUri);
            return Uri.UnescapeDataString(relativeUri.ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private void UpdateReferences(GuidSyncReport report)
        {
            Debug.Log($"Updating GUID references in Unity files in: {_subordinateProjectPath}");

            // Create mapping from old GUIDs to new GUIDs
            var guidRemapping = _guidMappings.Values.ToDictionary(
                m => m.SubordinateGuid,
                m => m.MainGuid
            );

            // Add all mappings to the report
            foreach (var mapping in _guidMappings.Values)
            {
                report.AddGuidMapping(mapping.RelativePath, mapping.SubordinateGuid, mapping.MainGuid);
            }

            foreach (var filePath in Directory.GetFiles(_subordinateProjectPath, "*", SearchOption.AllDirectories))
            {
                if (MetaFileParser.IsMetaFile(filePath))
                    continue;

                if (!IsUnityYamlFile(filePath))
                    continue;

                UpdateFileReferences(filePath, guidRemapping, report);
            }
        }

        /// Checks if a file is a Unity YAML file by examining its header
        private static bool IsUnityYamlFile(string filePath)
        {
            try
            {
                using (var reader = new StreamReader(filePath))
                {
                    var firstLine = reader.ReadLine();
                    return firstLine != null && (firstLine.StartsWith("%YAML") || firstLine.StartsWith("---"));
                }
            }
            catch
            {
                return false;
            }
        }

        /// Updates GUID references in a single file
        private static void UpdateFileReferences(string filePath, Dictionary<string, string> guidMappings, GuidSyncReport report)
        {
            try
            {
                string content = File.ReadAllText(filePath);
                var replacedGuids = new HashSet<string>();

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

                // Only check for complex pattern in non-meta files
                if (!MetaFileParser.IsMetaFile(filePath))
                {
                    content = FileIdGuidRegex.Replace(content, match =>
                    {
                        var oldGuid = match.Groups[1].Value;
                        if (guidMappings.TryGetValue(oldGuid, out var newGuid))
                        {
                            replacedGuids.Add(oldGuid);
                            return match.Value.Replace(oldGuid, newGuid);
                        }
                        return match.Value;
                    });
                }

                if (replacedGuids.Count > 0)
                {
                    File.WriteAllText(filePath, content);

                    foreach (var guid in replacedGuids)
                    {
                        report.AddReferenceUpdate(filePath, guid);
                    }

                    Debug.Log($"Updated GUID references in: {filePath}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to update references in {filePath}: {ex.Message}");
            }
        }

    }
}