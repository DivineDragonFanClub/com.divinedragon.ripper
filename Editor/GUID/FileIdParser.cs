using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

// Type aliases for clarity
using Guid = System.String;
using FilePath = System.String;
using FileID = System.Int64;

namespace DivineDragon
{
    /// Parses FileIDs from Unity YAML files
    public static class FileIdParser
    {
        // Matches Unity YAML object definitions like: --- !u!1 &1483090000831832
        // Captures the FileID (group 1)
        private static readonly Regex YamlFileIdDefinitionRegex = new Regex(@"^--- !u!\d+ &(-?\d+)", RegexOptions.Multiline | RegexOptions.Compiled);

        // Matches internal FileID references: {fileID: 123456}
        // Captures the FileID (group 1)
        private static readonly Regex InternalFileIdRegex = new Regex(@"\{fileID:\s*(-?\d+)\}", RegexOptions.Compiled);

        // Matches external FileID references: {fileID: 123456, guid: abc..., type: 3}
        // Captures: FileID (group 1), GUID (group 2), Type (group 3)
        private static readonly Regex ExternalFileIdRegex = new Regex(@"\{fileID:\s*(-?\d+),\s*guid:\s*([a-f0-9]{32}),\s*type:\s*(\d+)\}", RegexOptions.Compiled);

        /// Extracts all FileID definitions from a Unity YAML file
        public static List<FileID> ExtractFileIdDefinitions(FilePath filePath)
        {
            var fileIds = new List<FileID>();
            var uniqueTracker = new HashSet<FileID>();

            try
            {
                if (!File.Exists(filePath))
                {
                    Debug.LogWarning($"File not found for FileID extraction: {filePath}");
                    return fileIds;
                }

                string content = File.ReadAllText(filePath);
                var matches = YamlFileIdDefinitionRegex.Matches(content);

                foreach (Match match in matches)
                {
                    if (match.Success && long.TryParse(match.Groups[1].Value, out FileID fileId))
                    {
                        if (uniqueTracker.Add(fileId))
                        {
                            fileIds.Add(fileId);
                        }
                    }
                }

                Debug.Log($"Extracted {fileIds.Count} FileID definitions from {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to extract FileIDs from {filePath}: {ex.Message}");
            }

            return fileIds;
        }


        /// Creates a mapping of FileIDs between source and destination files
        /// This maps FileIDs from the subordinate project to corresponding FileIDs in the main project
        public static Dictionary<FileID, FileID> CreateFileIdMapping(
            IReadOnlyList<FileID> sourceFileIds,
            IReadOnlyList<FileID> destinationFileIds)
        {
            var mapping = new Dictionary<FileID, FileID>();

            // Map FileIDs by order of appearance. Unity assigns them deterministically within a single asset file.
            int minCount = Math.Min(sourceFileIds.Count, destinationFileIds.Count);
            for (int i = 0; i < minCount; i++)
            {
                mapping[sourceFileIds[i]] = destinationFileIds[i];
                Debug.Log($"FileID mapping: {sourceFileIds[i]} -> {destinationFileIds[i]}");
            }

            if (sourceFileIds.Count != destinationFileIds.Count)
            {
                Debug.LogWarning($"FileID count mismatch: source has {sourceFileIds.Count}, destination has {destinationFileIds.Count}");
            }

            return mapping;
        }
    }
}
