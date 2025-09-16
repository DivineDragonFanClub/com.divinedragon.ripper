using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            foreach (var filePath in Directory.GetFiles(projectPath, "*.meta", SearchOption.AllDirectories))
            {
                if (filePath.Contains(Path.DirectorySeparatorChar + "Library" + Path.DirectorySeparatorChar))
                    continue;

                if (MetaFileParser.TryGetGuid(filePath, out var guid))
                {
                    var relativePath = GetRelativePath(projectPath, filePath);
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

            GuidReferenceUpdater.UpdateReferences(_subordinateProjectPath, guidRemapping, report);
        }

    }
}