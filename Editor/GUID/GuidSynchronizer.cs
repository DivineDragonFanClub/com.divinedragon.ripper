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

                UpdateMetaFiles(report);

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

            // We only care about the Assets folder - that's where all the actual content lives
            // projectPath should already be pointing to the Assets folder, but let's be explicit
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

        /// Currently somewhat pointless since we never copy over existing files, and thus have no
        /// need to update their meta files. But leaving this here in case we ever do end up copying
        /// files that already exist in the main project, with their meta files for some reason...
        /// Could be good for making repairs?
        private void UpdateMetaFiles(GuidSyncReport report)
        {
            Debug.Log("Updating meta files...");

            foreach (var mapping in _guidMappings.Values)
            {
                var metaPath = Path.Combine(_subordinateProjectPath, mapping.RelativePath);

                if (MetaFileParser.TryUpdateGuid(metaPath, mapping.MainGuid))
                {
                    report.AddGuidMapping(mapping.RelativePath, mapping.SubordinateGuid, mapping.MainGuid);
                    Debug.Log($"Updated meta file: {mapping.RelativePath}");
                }
                else
                {
                    Debug.LogWarning($"Failed to update meta file: {metaPath}");
                }
            }
        }
        private void UpdateReferences(GuidSyncReport report)
        {
            Debug.Log("Updating GUID references in Unity files...");

            var guidRemapping = _guidMappings.Values.ToDictionary(
                m => m.SubordinateGuid,
                m => m.MainGuid
            );

            UpdateAllReferences(_subordinateProjectPath, guidRemapping, report);
        }

        /// Updates all GUID references in files within the specified directory
        private static void UpdateAllReferences(string directory, Dictionary<string, string> guidMappings, GuidSyncReport report)
        {
            Debug.Log($"Updating GUID references in: {directory}");

            var reverseMapping = new Dictionary<string, string>(guidMappings);

            foreach (var filePath in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (MetaFileParser.IsMetaFile(filePath))
                    continue;

                if (!IsUnityYamlFile(filePath))
                    continue;

                UpdateFileReferences(filePath, reverseMapping, report);
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
                string originalContent = content;
                var replacedGuids = new HashSet<string>();

                // Check if this is a meta file
                bool isMetaFile = MetaFileParser.IsMetaFile(filePath);

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
                if (!isMetaFile)
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

                if (content != originalContent)
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