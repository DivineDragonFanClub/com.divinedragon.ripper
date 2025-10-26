using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
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
        private static readonly Regex StubCommentRegex = new Regex(@"Dummy class\.", RegexOptions.IgnoreCase);

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

        public class ScriptMapping
        {
            public Guid StubGuid { get; set; }
            public Guid RealGuid { get; set; }
            public TypeName TypeName { get; set; }
            public FilePath StubPath { get; set; }
            public FilePath RealPath { get; set; }
        }

        public static List<ScriptMapping> CreateStubToRealGuidMappings(string stubFolderPath)
        {
            var mappings = new List<ScriptMapping>();

            // Cache resolved types so we don't repeatedly walk assemblies
            var resolvedTypeCache = new Dictionary<TypeName, Type>();

            // Get all stub scripts in the folder
            var stubScripts = Directory.GetFiles(stubFolderPath, "*.cs", SearchOption.AllDirectories)
                .Where(IsStubScript)
                .ToList();

            if (!stubScripts.Any())
                return mappings;

            // Match stubs to real scripts
            foreach (var stubScript in stubScripts)
            {
                var stubType = ExtractTypeInfo(stubScript);
                if (stubType == null)
                    continue;

                var stubGuid = GetGuidFromMetaFile(stubScript + ".meta");
                if (string.IsNullOrEmpty(stubGuid))
                    continue;

                if (TryGetRealScriptForType(stubType.FullName, resolvedTypeCache, out var resolvedGuid, out var resolvedAssetPath))
                {
                    mappings.Add(new ScriptMapping
                    {
                        StubGuid = stubGuid,
                        RealGuid = resolvedGuid,
                        TypeName = stubType.FullName,
                        StubPath = stubScript,
                        RealPath = NormalizeAbsolutePath(resolvedAssetPath)
                    });
                    Debug.Log($"Mapped stub {stubType.FullName} ({stubGuid}) to real script ({resolvedGuid}) via reflection lookup");
                    continue;
                }

                Debug.LogWarning($"Failed to resolve real script for {stubType.FullName}; skipping GUID remap.");
            }

            return mappings;
        }

        private static bool TryGetRealScriptForType(
            TypeName fullTypeName,
            Dictionary<TypeName, Type> resolvedTypeCache,
            out Guid realGuid,
            out FilePath realAssetPath)
        {
            realGuid = null;
            realAssetPath = null;

            if (string.IsNullOrEmpty(fullTypeName))
                return false;

            if (!resolvedTypeCache.TryGetValue(fullTypeName, out var resolvedType) || resolvedType == null)
            {
                resolvedType = FindUnityObjectType(fullTypeName);
                resolvedTypeCache[fullTypeName] = resolvedType;
            }

            if (resolvedType == null)
                return false;

            var potentialGuids = AssetDatabase.FindAssets($"t:MonoScript {resolvedType.Name}");
            foreach (var guid in potentialGuids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath))
                    continue;

                var monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(assetPath);
                if (monoScript == null)
                    continue;

                var scriptClass = monoScript.GetClass();
                if (scriptClass == null)
                    continue;

                if (scriptClass == resolvedType)
                {
                    realGuid = guid;
                    realAssetPath = assetPath;
                    return true;
                }
            }

            return false;
        }

        private static Type FindUnityObjectType(TypeName fullTypeName)
        {
            if (string.IsNullOrEmpty(fullTypeName))
                return null;

            foreach (var type in TypeCache.GetTypesDerivedFrom<UnityEngine.Object>())
            {
                if (type != null && type.FullName == fullTypeName)
                    return type;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var candidate = assembly.GetType(fullTypeName);
                    if (candidate != null && typeof(UnityEngine.Object).IsAssignableFrom(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // ignore assemblies we can't inspect
                }
            }

            return null;
        }

        private static FilePath NormalizeAbsolutePath(FilePath path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path).Replace('\\', '/');
            }

            try
            {
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                var combined = Path.Combine(projectRoot, path);
                return Path.GetFullPath(combined).Replace('\\', '/');
            }
            catch
            {
                return path.Replace('\\', '/');
            }
        }
    }
}
