using System.IO;

// Type aliases for clarity
using FilePath = System.String;

namespace DivineDragon
{
    /// Shared utility methods for working with Unity files
    public static class UnityFileUtils
    {
        /// Checks if a file is a Unity YAML file by examining its header
        public static bool IsUnityYamlFile(FilePath filePath)
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
    }
}