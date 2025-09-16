using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DivineDragon
{
    /// Parses Unity .meta files to extract and update GUIDs
    public static class MetaFileParser
    {
        // Regex to match GUID line in meta files - can be with or without quotes
        private static readonly Regex GuidRegex = new Regex(@"^guid:\s*['""]?([a-f0-9]{32})['""]?\s*$", RegexOptions.Multiline);

        // Regex to match and preserve GUID line formatting - some meta files have quotes, some don't
        private static readonly Regex GuidReplaceRegex = new Regex(@"^(guid:\s*)(['""]?)([a-f0-9]{32})(['""]?)\s*$", RegexOptions.Multiline);

        /// Extracts the GUID from a Unity .meta file
        public static bool TryGetGuid(string metaFilePath, out string guid)
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
        /// NOTE: Since we never existing files, and definitely don't copy meta files,
        /// this is mostly just for bookkeeping and testing purposes, and for the sake of making a "coherent" subordinate project
        /// that could still be used independently if desired.
        public static bool TryUpdateGuid(string metaFilePath, string newGuid)
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

        /// Just checking if ends in .meta for now, nothing too fancy
        public static bool IsMetaFile(string filePath)
        {
            return filePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
        }
    }
}