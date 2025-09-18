using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DivineDragon
{
    public static class ShaderUtils
    {
        private static readonly Regex ShaderNameRegex = new Regex(@"Shader\s+""([^""]+)""", RegexOptions.Compiled | RegexOptions.Multiline);

        public static HashSet<string> GetExistingShaderNames()
        {
            var shaderNames = new HashSet<string>();

            Shader[] allShaders = Resources.FindObjectsOfTypeAll<Shader>();
            foreach (var shader in allShaders)
            {
                if (shader != null && !string.IsNullOrEmpty(shader.name))
                {
                    shaderNames.Add(shader.name);
                }
            }

            Debug.Log($"Found {shaderNames.Count} existing shaders in project");
            return shaderNames;
        }

        public static string ExtractShaderName(string shaderFilePath)
        {
            if (!File.Exists(shaderFilePath))
                return null;

            try
            {
                string content = File.ReadAllText(shaderFilePath);

                var match = ShaderNameRegex.Match(content);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Failed to extract shader name from {shaderFilePath}: {ex.Message}");
            }

            return null;
        }

        public static bool IsShaderFile(string filePath)
        {
            return !string.IsNullOrEmpty(filePath) && filePath.EndsWith(".shader", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}