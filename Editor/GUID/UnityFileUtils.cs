using System;
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

    public static class UnityPathUtils
    {
        public static string FromAbsolute(string absolutePath, string assetsRoot)
        {
            if (string.IsNullOrEmpty(absolutePath))
                return absolutePath;

            var normalizedPath = Path.GetFullPath(absolutePath).Replace('\\', '/');
            var normalizedRoot = Path.GetFullPath(assetsRoot).Replace('\\', '/');

            if (!normalizedRoot.EndsWith("/"))
            {
                normalizedRoot += "/";
            }

            if (normalizedPath.StartsWith(normalizedRoot))
            {
                var relative = normalizedPath.Substring(normalizedRoot.Length);
                return NormalizeAssetPath(relative);
            }

            return normalizedPath;
        }

        public static string ToAbsolute(string unityPath, string assetsRoot)
        {
            if (string.IsNullOrEmpty(unityPath))
                return unityPath;

            if (Path.IsPathRooted(unityPath))
                return unityPath;

            var normalizedRoot = Path.GetFullPath(assetsRoot);
            var normalizedUnity = NormalizeAssetPath(unityPath);
            var relative = normalizedUnity.StartsWith("Assets/")
                ? normalizedUnity.Substring("Assets/".Length)
                : normalizedUnity;

            return Path.Combine(normalizedRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        public static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "Assets";

            var normalized = path.Replace('\\', '/').TrimStart('/');

            if (normalized.Length == 0)
                return "Assets";

            if (normalized.StartsWith("Assets"))
            {
                if (normalized.Length == 6)
                {
                    return "Assets";
                }

                if (normalized.Length > 6 && normalized[6] == '/')
                {
                    return "Assets" + normalized.Substring(6);
                }
            }

            return "Assets/" + normalized;
        }

        public static string GetRelativePath(string basePath, string fullPath)
        {
            if (string.IsNullOrEmpty(basePath))
                throw new ArgumentException("Base path is required", nameof(basePath));

            if (string.IsNullOrEmpty(fullPath))
                throw new ArgumentException("Full path is required", nameof(fullPath));

            basePath = Path.GetFullPath(basePath);
            fullPath = Path.GetFullPath(fullPath);

            if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                basePath += Path.DirectorySeparatorChar;
            }

            var baseUri = new Uri(basePath);
            var fullUri = new Uri(fullPath);
            var relativeUri = baseUri.MakeRelativeUri(fullUri);
            var relativePath = Uri.UnescapeDataString(relativeUri.ToString());

            return relativePath.Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
