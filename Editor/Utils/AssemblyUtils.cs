using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace DivineDragon
{
    public static class AssemblyUtils
    {
        [Serializable]
        private class AssemblyDefinition
        {
            public string name;
        }

        public static HashSet<string> GetExistingAssemblyNames()
        {
            var assemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var compilationAssemblies = CompilationPipeline.GetAssemblies();
            foreach (var assembly in compilationAssemblies)
            {
                assemblyNames.Add(assembly.name);
            }

            Debug.Log($"Found {assemblyNames.Count} existing assemblies in project");
            return assemblyNames;
        }

        public static string ExtractAssemblyName(string asmdefPath)
        {
            if (!File.Exists(asmdefPath))
                return null;

            try
            {
                string jsonContent = File.ReadAllText(asmdefPath);
                var asmDef = JsonUtility.FromJson<AssemblyDefinition>(jsonContent);

                // Return null for empty or whitespace-only names (invalid assemblies)
                if (string.IsNullOrWhiteSpace(asmDef?.name))
                    return null;

                return asmDef.name;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to extract assembly name from {asmdefPath}: {ex.Message}");
            }

            return null;
        }

        public static bool IsAssemblyDefinitionFile(string filePath)
        {
            return !string.IsNullOrEmpty(filePath) &&
                   filePath.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase);
        }

        public static string GetAssemblyFolder(string asmdefPath)
        {
            return Path.GetDirectoryName(asmdefPath);
        }

        public static bool IsPathInFolder(string filePath, string folderPath)
        {
            var normalizedFile = Path.GetFullPath(filePath).Replace('\\', '/');
            var normalizedFolder = Path.GetFullPath(folderPath).Replace('\\', '/');

            if (!normalizedFolder.EndsWith("/"))
                normalizedFolder += "/";

            return normalizedFile.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase);
        }
    }
}