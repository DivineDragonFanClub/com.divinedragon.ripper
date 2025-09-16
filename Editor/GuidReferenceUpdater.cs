using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DivineDragon
{
    public static class GuidReferenceUpdater
    {
        // Matches: guid: 0123456789abcdef0123456789abcdef
        // Captures the GUID (group 1)
        private static readonly Regex SimpleGuidRegex = new Regex(@"guid:\s*([a-f0-9]{32})", RegexOptions.Compiled);

        // Matches: {fileID: 123456789, guid: 0123456789abcdef0123456789abcdef, type: 3}
        // Captures the GUID (group 1)
        private static readonly Regex FileIdGuidRegex = new Regex(@"\{fileID:\s*\d+,\s*guid:\s*([a-f0-9]{32}),\s*type:\s*\d+\}", RegexOptions.Compiled);

        // More would need to be added if other formats are discovered


        /// Updates all GUID references in files within the specified directory
        public static void UpdateReferences(string directory, Dictionary<string, string> guidMappings, GuidSyncReport report)
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