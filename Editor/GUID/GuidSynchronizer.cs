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

namespace DivineDragon
{
    /// GUID "synchronization" that coordinates the remapping of GUIDs
    /// from subordinate project (imported assets) to main project (current Unity project)
    public class GuidSynchronizer
    {
        private readonly DirectoryPath _mainProjectPath;
        private readonly DirectoryPath _subordinateProjectPath;
        private readonly Dictionary<RelativePath, GuidMapping> _guidMappings;

        // Matches: guid: 0123456789abcdef0123456789abcdef
        // Captures the GUID (group 1)
        private static readonly Regex SimpleGuidRegex = new Regex(@"guid:\s*([a-f0-9]{32})", RegexOptions.Compiled);

        // Matches: {fileID: 123456789, guid: 0123456789abcdef0123456789abcdef, type: 3}
        // Captures the GUID (group 1)
        private static readonly Regex FileIdGuidRegex = new Regex(@"\{fileID:\s*\d+,\s*guid:\s*([a-f0-9]{32}),\s*type:\s*\d+\}", RegexOptions.Compiled);

        // More would need to be added if other formats are discovered

        private class GuidMapping
        {
            public RelativePath RelativePath { get; set; }
            public Guid MainGuid { get; set; }
            public Guid SubordinateGuid { get; set; }
        }

        public GuidSynchronizer(DirectoryPath mainProjectPath, DirectoryPath subordinateProjectPath)
        {
            _mainProjectPath = mainProjectPath;
            _subordinateProjectPath = subordinateProjectPath;
            _guidMappings = new Dictionary<RelativePath, GuidMapping>();
        }

        public GuidSyncReport Synchronize()
        {
            Debug.Log("Starting GUID synchronization...");

            try
            {
                ScanProjects();

                if (_guidMappings.Count == 0)
                {
                    Debug.Log("No GUID differences found. Assets are already synchronized.");
                    var emptyReport = new GuidSyncReport();
                    emptyReport.FinalizeReport();
                    return emptyReport;
                }

                Debug.Log($"Found {_guidMappings.Count} GUID differences to synchronize");

                // Do the actual work
                var fileUpdates = UpdateReferences();

                // Build the report from the results
                var report = BuildReport(fileUpdates);
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

        private GuidSyncReport BuildReport(Dictionary<FilePath, HashSet<Guid>> fileUpdates)
        {
            var report = new GuidSyncReport();

            // Add all GUID mappings
            foreach (var mapping in _guidMappings.Values)
            {
                report.AddGuidMapping(mapping.RelativePath, mapping.SubordinateGuid, mapping.MainGuid);
            }

            // Add file update references
            foreach (var kvp in fileUpdates)
            {
                foreach (var guid in kvp.Value)
                {
                    report.AddReferenceUpdate(kvp.Key, guid);
                }
            }

            return report;
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

        private Dictionary<RelativePath, Guid> ScanMetaFiles(DirectoryPath projectPath)
        {
            var metaFiles = new Dictionary<RelativePath, Guid>();

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
        private RelativePath GetRelativePath(DirectoryPath basePath, FilePath fullPath)
        {
            var baseUri = new Uri(basePath.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? basePath
                : basePath + Path.DirectorySeparatorChar);
            var fullUri = new Uri(fullPath);
            var relativeUri = baseUri.MakeRelativeUri(fullUri);
            return Uri.UnescapeDataString(relativeUri.ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private Dictionary<FilePath, HashSet<Guid>> UpdateReferences()
        {
            Debug.Log($"Updating GUID references in Unity files in: {_subordinateProjectPath}");

            // Create mapping from old GUIDs to new GUIDs
            var guidRemapping = _guidMappings.Values.ToDictionary<GuidMapping, Guid, Guid>(
                m => m.SubordinateGuid,
                m => m.MainGuid
            );

            // Track which files had which GUIDs updated
            var fileUpdates = new Dictionary<FilePath, HashSet<Guid>>();

            foreach (var filePath in Directory.GetFiles(_subordinateProjectPath, "*", SearchOption.AllDirectories))
            {
                if (MetaFileParser.IsMetaFile(filePath))
                    continue;

                if (!IsUnityYamlFile(filePath))
                    continue;

                var replacedGuids = UpdateFileReferences(filePath, guidRemapping);
                if (replacedGuids.Count > 0)
                {
                    fileUpdates[filePath] = replacedGuids;
                }
            }

            return fileUpdates;
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
        /// Returns the set of GUIDs that were replaced in the file
        private static HashSet<Guid> UpdateFileReferences(FilePath filePath, Dictionary<Guid, Guid> guidMappings)
        {
            var replacedGuids = new HashSet<Guid>();

            try
            {
                string content = File.ReadAllText(filePath);

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
                    Debug.Log($"Updated GUID references in: {filePath}");
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