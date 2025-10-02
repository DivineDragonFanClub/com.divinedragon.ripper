using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

// Type aliases for clarity
using Guid = System.String;
using FilePath = System.String;
using FileID = System.Int64;

namespace DivineDragon
{
    /// Parses Unity .meta files to extract and update GUIDs
    public static class MetaFileParser
    {
        // Regex to match GUID line in meta files - can be with or without quotes
        private static readonly Regex GuidRegex = new Regex(@"^guid:\s*['""]?([a-f0-9]{32})['""]?\s*$", RegexOptions.Multiline);

        // Regex to match and preserve GUID line formatting - some meta files have quotes, some don't
        private static readonly Regex GuidReplaceRegex = new Regex(@"^(guid:\s*)(['""]?)([a-f0-9]{32})(['""]?)\s*$", RegexOptions.Multiline);

        // Regex to match the main object FileID line in meta files (if present)
        private static readonly Regex MainObjectFileIdRegex = new Regex(@"^\s*mainObjectFileID:\s*(-?\d+)\s*$", RegexOptions.Multiline);

        /// Extracts the GUID from a Unity .meta file
        public static bool TryGetGuid(FilePath metaFilePath, out Guid guid)
        {
            guid = null;

            try
            {
                if (!File.Exists(metaFilePath))
                {
                    Debug.LogWarning($"Meta file not found: {metaFilePath}, was it deleted or moved during processing?");
                    return false;
                }

                string content = File.ReadAllText(metaFilePath);
                Match match = GuidRegex.Match(content);

                if (match.Success && match.Groups.Count > 1)
                {
                    guid = match.Groups[1].Value;
                    return true;
                }

                Debug.LogWarning($"No GUID found in meta file: {metaFilePath}, this doesn't seem right...");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to read meta file {metaFilePath}: {ex.Message}, what's going on?");
                return false;
            }
        }

        /// Updates the GUID in a Unity .meta file while preserving formatting
        /// NOTE: This method is currently unused but kept for potential future use cases
        /// where we might need to update GUIDs in meta files directly.
        public static bool TryUpdateGuid(FilePath metaFilePath, Guid newGuid)
        {
            try
            {
                if (!File.Exists(metaFilePath))
                {
                    Debug.LogError($"Meta file not found: {metaFilePath}");
                    return false;
                }

                string content = File.ReadAllText(metaFilePath);
                bool updated = false;

                // Replace GUID while preserving the original formatting (quotes or no quotes)
                string newContent = GuidReplaceRegex.Replace(content, match =>
                {
                    if (match.Groups.Count >= 5)
                    {
                        updated = true;
                        // Preserve the original formatting
                        return $"{match.Groups[1].Value}{match.Groups[2].Value}{newGuid}{match.Groups[4].Value}";
                    }
                    return match.Value;
                });

                if (!updated)
                {
                    Debug.LogError($"No GUID found to update in meta file: {metaFilePath}");
                    return false;
                }

                // Write back to file
                File.WriteAllText(metaFilePath, newContent);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to update meta file {metaFilePath}: {ex.Message}");
                return false;
            }
        }

        /// Extracts GUID and, when available, the main object's FileID from a Unity .meta file
        public static bool TryGetGuidAndMainFileId(FilePath metaFilePath, out Guid guid, out FileID? mainFileId)
        {
            guid = null;
            mainFileId = null;

            try
            {
                if (!File.Exists(metaFilePath))
                {
                    Debug.LogWarning($"Meta file not found: {metaFilePath}");
                    return false;
                }

                string content = File.ReadAllText(metaFilePath);

                // Extract GUID
                Match guidMatch = GuidRegex.Match(content);
                if (!guidMatch.Success || guidMatch.Groups.Count <= 1)
                {
                    Debug.LogWarning($"No GUID found in meta file: {metaFilePath}");
                    return false;
                }
                guid = guidMatch.Groups[1].Value;

                // Extract main object FileID if present. Not all meta files contain this value (e.g., folders, scripts).
                Match fileIdMatch = MainObjectFileIdRegex.Match(content);
                if (fileIdMatch.Success && fileIdMatch.Groups.Count > 1 && long.TryParse(fileIdMatch.Groups[1].Value, out var parsedFileId))
                {
                    mainFileId = parsedFileId;
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to read meta file {metaFilePath}: {ex.Message}");
                return false;
            }
        }

        /// Just checking if ends in .meta for now, nothing too fancy
        public static bool IsMetaFile(FilePath filePath)
        {
            return filePath.EndsWith(".meta");
        }
    }
}
