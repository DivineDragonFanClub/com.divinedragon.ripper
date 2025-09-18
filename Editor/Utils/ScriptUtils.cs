using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

// Type aliases for clarity
using TypeName = System.String;
using FilePath = System.String;
using Guid = System.String;

namespace DivineDragon
{
    public static class ScriptUtils
    {
        private static readonly Regex NamespaceRegex = new Regex(@"^\s*namespace\s+([^\s{]+)", RegexOptions.Multiline);
        private static readonly Regex ClassRegex = new Regex(@"^\s*public\s+(?:partial\s+)?(?:abstract\s+)?(?:sealed\s+)?class\s+([^\s:<{]+)", RegexOptions.Multiline);
        private static readonly Regex StubCommentRegex = new Regex(@"Dummy class\.|This could have happened for several reasons:", RegexOptions.IgnoreCase);

        public class TypeInfo
        {
            public string Namespace { get; set; }
            public string ClassName { get; set; }
            public string FullName => string.IsNullOrEmpty(Namespace) ? ClassName : $"{Namespace}.{ClassName}";

            public override bool Equals(object obj)
            {
                if (obj is TypeInfo other)
                {
                    return FullName == other.FullName;
                }
                return false;
            }

            public override int GetHashCode()
            {
                return FullName?.GetHashCode() ?? 0;
            }
        }

        public static TypeInfo ExtractTypeInfo(string scriptPath)
        {
            if (!File.Exists(scriptPath))
                return null;

            try
            {
                string content = File.ReadAllText(scriptPath);

                var namespaceMatch = NamespaceRegex.Match(content);
                var classMatch = ClassRegex.Match(content);

                if (!classMatch.Success)
                    return null;

                return new TypeInfo
                {
                    Namespace = namespaceMatch.Success ? namespaceMatch.Groups[1].Value.Trim() : "",
                    ClassName = classMatch.Groups[1].Value.Trim()
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to extract type info from {scriptPath}: {ex.Message}");
                return null;
            }
        }

        public static bool IsStubScript(string scriptPath)
        {
            if (!File.Exists(scriptPath))
                return false;

            try
            {
                string content = File.ReadAllText(scriptPath);
                return StubCommentRegex.IsMatch(content);
            }
            catch
            {
                return false;
            }
        }

        public static Guid GetGuidFromMetaFile(FilePath metaFilePath)
        {
            if (MetaFileParser.TryGetGuid(metaFilePath, out Guid guid))
            {
                return guid;
            }
            return null;
        }

        public static List<string> GetScriptsInAssembly(string assemblyName)
        {
            var scripts = new List<string>();

            var assembly = CompilationPipeline.GetAssemblies()
                .FirstOrDefault(a => a.name == assemblyName);

            if (assembly == null)
                return scripts;

            // Get all source files in the assembly
            if (assembly.sourceFiles != null)
            {
                scripts.AddRange(assembly.sourceFiles);
            }

            return scripts;
        }

        public class ScriptMapping
        {
            public Guid StubGuid { get; set; }
            public Guid RealGuid { get; set; }
            public TypeName TypeName { get; set; }
            public FilePath StubPath { get; set; }
        }

        public static List<ScriptMapping> CreateStubToRealGuidMappings(
            string stubFolderPath,
            string assemblyName)
        {
            var mappings = new List<ScriptMapping>();

            // Get all stub scripts in the folder
            var stubScripts = Directory.GetFiles(stubFolderPath, "*.cs", SearchOption.AllDirectories)
                .Where(IsStubScript)
                .ToList();

            if (!stubScripts.Any())
                return mappings;

            // Get all real scripts in the assembly
            var realScripts = GetScriptsInAssembly(assemblyName);

            // Build type info for real scripts
            var realTypeMap = new Dictionary<TypeName, FilePath>();
            foreach (var realScript in realScripts)
            {
                var typeInfo = ExtractTypeInfo(realScript);
                if (typeInfo != null)
                {
                    realTypeMap[typeInfo.FullName] = realScript;
                }
            }

            // Match stubs to real scripts
            foreach (var stubScript in stubScripts)
            {
                var stubType = ExtractTypeInfo(stubScript);
                if (stubType == null)
                    continue;

                if (realTypeMap.TryGetValue(stubType.FullName, out var realScript))
                {
                    var stubGuid = GetGuidFromMetaFile(stubScript + ".meta");
                    var realGuid = GetGuidFromMetaFile(realScript + ".meta");

                    if (!string.IsNullOrEmpty(stubGuid) && !string.IsNullOrEmpty(realGuid))
                    {
                        mappings.Add(new ScriptMapping
                        {
                            StubGuid = stubGuid,
                            RealGuid = realGuid,
                            TypeName = stubType.FullName,
                            StubPath = stubScript
                        });
                        Debug.Log($"Mapped stub {stubType.FullName} ({stubGuid}) to real script ({realGuid})");
                    }
                }
            }

            return mappings;
        }
    }
}