using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

// Type aliases for clarity
using Guid = System.String;
using FilePath = System.String;
using RelativePath = System.String;
using AssetPath = System.String;
using FileID = System.Int64;

namespace DivineDragon
{
    /// Report is good for proving that the process works and for debugging
    /// It contains lists of new files, skipped files, and GUID mappings between main and subordinate projects
    /// Perhaps poorly named since it also is needed for our actual GUID mapping and dependency updating logic
    [Serializable]
    public class AssemblySkipInfo
    {
        public string AssemblyName { get; set; }
        public string FolderPath { get; set; }
    }

    [Serializable]
    public class GuidSyncReport
    {
        public List<FilePath> NewFilesImported { get; private set; }

        public List<FilePath> SkippedFiles { get; private set; }

        public List<FilePath> DuplicateShaders { get; private set; }

        public List<AssemblySkipInfo> DuplicateAssemblies { get; private set; }

        public List<ScriptGuidRemapping> ScriptGuidRemappings { get; private set; }

        public List<GuidMapping> Mappings { get; private set; }

        public List<FileIdRemapping> FileIdRemappings { get; private set; }

        [SerializeField]
        private List<FileDependencyMapping> _fileDependencyMappings;

        private readonly Dictionary<Guid, GuidMapping> _byOldGuid = new Dictionary<Guid, GuidMapping>();
        private readonly HashSet<string> _fileIdRemapKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _scriptRemapKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string SummaryText { get; private set; }

        public GuidSyncReport()
        {
            NewFilesImported = new List<FilePath>();
            SkippedFiles = new List<FilePath>();
            DuplicateShaders = new List<FilePath>();
            DuplicateAssemblies = new List<AssemblySkipInfo>();
            ScriptGuidRemappings = new List<ScriptGuidRemapping>();
            Mappings = new List<GuidMapping>();
            FileIdRemappings = new List<FileIdRemapping>();
            _fileDependencyMappings = new List<FileDependencyMapping>();
        }

        public Dictionary<FilePath, List<DependencyUpdate>> FileDependencyUpdates
        {
            get
            {
                var dict = new Dictionary<FilePath, List<DependencyUpdate>>();
                if (_fileDependencyMappings != null)
                {
                    foreach (var mapping in _fileDependencyMappings)
                    {
                        dict[mapping.FilePath] = mapping.Dependencies;
                    }
                }
                return dict;
            }
        }

        public void AddGuidMapping(AssetPath assetPath, Guid oldGuid, Guid newGuid)
        {
            if (!_byOldGuid.TryGetValue(oldGuid, out var existing) || existing.NewGuid != newGuid)
            {
                var mapping = new GuidMapping
                {
                    OldGuid = oldGuid,
                    NewGuid = newGuid,
                    AssetName = System.IO.Path.GetFileNameWithoutExtension(assetPath.Replace(".meta", "")),
                    AssetPath = assetPath
                };
                Mappings.Add(mapping);
                _byOldGuid[oldGuid] = mapping;
            }
        }

        public void AddReferenceUpdate(FilePath filePath, Guid guid)
        {
            _byOldGuid.TryGetValue(guid, out var mapping);
            if (mapping != null)
            {
                string unityFilePath = ConvertToUnityPath(filePath);

                var fileMapping = _fileDependencyMappings.FirstOrDefault(m => m.FilePath == unityFilePath);
                if (fileMapping == null)
                {
                    fileMapping = new FileDependencyMapping
                    {
                        FilePath = unityFilePath,
                        Dependencies = new List<DependencyUpdate>()
                    };
                    _fileDependencyMappings.Add(fileMapping);
                }

                // Check if we already have this dependency tracked for this file
                var existingDep = fileMapping.Dependencies.FirstOrDefault(d => d.OldGuid == guid);
                if (existingDep == null)
                {
                    fileMapping.Dependencies.Add(new DependencyUpdate
                    {
                        DependencyName = mapping.AssetName,
                        DependencyPath = mapping.AssetPath,
                        OldGuid = mapping.OldGuid,
                        NewGuid = mapping.NewGuid,
                        FileIdRemappings = new List<FileIdRemapping>()
                    });
                }
            }
        }

        private string ConvertToUnityPath(string filePath)
        {
            // Convert to Unity project relative path
            string assetsKeyword = "Assets" + System.IO.Path.DirectorySeparatorChar;
            int assetsIndex = filePath.LastIndexOf(assetsKeyword);

            if (assetsIndex >= 0)
            {
                string relativePath = filePath.Substring(assetsIndex);
                return relativePath.Replace('\\', '/');
            }

            return filePath;
        }

        private string NormalizeUnityPath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return filePath;
            }

            var normalized = filePath.Replace('\\', '/');

            int assetsIndex = normalized.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
            if (assetsIndex >= 0)
            {
                normalized = normalized.Substring(assetsIndex);
            }

            if (normalized.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(0, normalized.Length - ".meta".Length);
            }

            return normalized;
        }

        public void AddNewFile(FilePath filePath)
        {
            if (!NewFilesImported.Contains(filePath))
            {
                NewFilesImported.Add(filePath);
            }
        }

        public void AddSkippedFile(FilePath filePath)
        {
            if (!SkippedFiles.Contains(filePath))
            {
                SkippedFiles.Add(filePath);
            }
        }

        public void AddDuplicateShader(FilePath filePath)
        {
            if (!DuplicateShaders.Contains(filePath))
            {
                DuplicateShaders.Add(filePath);
            }
        }

        public void AddDuplicateAssembly(string assemblyName, string folderPath)
        {
            if (!DuplicateAssemblies.Any(a => a.AssemblyName == assemblyName))
            {
                DuplicateAssemblies.Add(new AssemblySkipInfo
                {
                    AssemblyName = assemblyName,
                    FolderPath = folderPath
                });
            }
        }

        public void AddScriptGuidRemapping(
            string assetPath,
            string scriptType,
            Guid stubGuid,
            Guid realGuid,
            string stubScriptPath = null,
            string realScriptPath = null)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(scriptType) || string.IsNullOrEmpty(stubGuid) || string.IsNullOrEmpty(realGuid))
            {
                return;
            }

            var normalizedAssetPath = NormalizeUnityPath(assetPath);
            var normalizedStubPath = string.IsNullOrEmpty(stubScriptPath) ? null : NormalizeUnityPath(stubScriptPath);
            var normalizedRealPath = string.IsNullOrEmpty(realScriptPath) ? null : NormalizeUnityPath(realScriptPath);

            var key = $"{normalizedAssetPath}|{scriptType}|{stubGuid}|{realGuid}";
            if (_scriptRemapKeys.Add(key))
            {
                ScriptGuidRemappings.Add(new ScriptGuidRemapping
                {
                    TargetAssetPath = normalizedAssetPath,
                    ScriptType = scriptType,
                    StubGuid = stubGuid,
                    RealGuid = realGuid,
                    StubScriptPath = normalizedStubPath,
                    RealScriptPath = normalizedRealPath
                });
            }
        }

        public void AddFileIdRemapping(Guid fileGuid, FilePath filePath, FileID oldFileId, FileID newFileId)
        {
            var key = $"{fileGuid}|{filePath}|{oldFileId}|{newFileId}";
            if (_fileIdRemapKeys.Add(key))
            {
                FileIdRemappings.Add(new FileIdRemapping
                {
                    FileGuid = fileGuid,
                    FilePath = filePath,
                    OldFileId = oldFileId,
                    NewFileId = newFileId
                });
            }
        }

        public void FinalizeReport()
        {
            // Associate FileID remappings with their corresponding dependencies
            foreach (var fileMapping in _fileDependencyMappings)
            {
                foreach (var dep in fileMapping.Dependencies)
                {
                    // Find FileID remappings for this dependency's GUID
                    var relatedRemappings = FileIdRemappings.Where(r => r.FileGuid == dep.OldGuid).ToList();
                    dep.FileIdRemappings.AddRange(relatedRemappings);
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"New Files Imported: {NewFilesImported.Count}");
            sb.AppendLine($"Files Skipped: {SkippedFiles.Count}");
            if (DuplicateShaders.Count > 0)
            {
                sb.AppendLine($"Duplicate Shaders Skipped: {DuplicateShaders.Count}");
            }
            if (DuplicateAssemblies.Count > 0)
            {
                sb.AppendLine($"Duplicate Assemblies Skipped: {DuplicateAssemblies.Count}");
            }
            sb.AppendLine($"Script Stub Remappings: {ScriptGuidRemappings.Count}");
            sb.AppendLine($"UUID Mappings: {Mappings.Count}");
            sb.AppendLine($"FileID Remappings: {FileIdRemappings.Count}");

            SummaryText = sb.ToString();
        }

        public string ToJson()
        {
            string EscapeString(string value)
            {
                if (value == null)
                {
                    return "null";
                }

                var sb = new StringBuilder(value.Length + 4);
                sb.Append('"');

                foreach (var ch in value)
                {
                    switch (ch)
                    {
                        case '"':
                            sb.Append("\\\"");
                            break;
                        case '\\':
                            sb.Append("\\\\");
                            break;
                        case '\n':
                            sb.Append("\\n");
                            break;
                        case '\r':
                            sb.Append("\\r");
                            break;
                        case '\t':
                            sb.Append("\\t");
                            break;
                        default:
                            if (ch < 0x20)
                            {
                                sb.AppendFormat("\\u{0:X4}", (int)ch);
                            }
                            else
                            {
                                sb.Append(ch);
                            }
                            break;
                    }
                }

                sb.Append('"');
                return sb.ToString();
            }

            string NormalizePath(string path)
            {
                if (string.IsNullOrEmpty(path))
                {
                    return path;
                }

                return path.Replace(".meta", string.Empty).Replace('\\', '/');
            }

            List<(long oldId, long newId)> CollectFileIdRemapsForPath(string path)
            {
                var normalized = NormalizePath(path);
                var results = new List<(long, long)>();

                var seen = new HashSet<string>();
                foreach (var remap in FileIdRemappings)
                {
                    if (NormalizePath(remap.FilePath) != normalized)
                    {
                        continue;
                    }

                    var key = $"{remap.OldFileId}|{remap.NewFileId}";
                    if (seen.Add(key))
                    {
                        results.Add((remap.OldFileId, remap.NewFileId));
                    }
                }

                return results;
            }

            List<(long oldId, long newId)> CollectFileIdRemaps(IEnumerable<FileIdRemapping> remaps)
            {
                var results = new List<(long, long)>();
                if (remaps == null)
                {
                    return results;
                }

                var seen = new HashSet<string>();
                foreach (var remap in remaps)
                {
                    var key = $"{remap.OldFileId}|{remap.NewFileId}";
                    if (seen.Add(key))
                    {
                        results.Add((remap.OldFileId, remap.NewFileId));
                    }
                }

                return results;
            }

            void AppendIndent(StringBuilder builder, int indent)
            {
                builder.Append(' ', indent * 2);
            }

            void AppendFileIdRemapObject(StringBuilder builder, List<(long oldId, long newId)> remaps, int indent)
            {
                if (remaps == null || remaps.Count == 0)
                {
                    builder.Append("null");
                    return;
                }

                builder.Append("{\n");
                for (int i = 0; i < remaps.Count; i++)
                {
                    var (oldId, newId) = remaps[i];
                    AppendIndent(builder, indent + 1);
                    builder.Append('"');
                    builder.Append(oldId);
                    builder.Append("\": ");
                    builder.Append(newId);
                    if (i < remaps.Count - 1)
                    {
                        builder.Append(',');
                    }
                    builder.Append('\n');
                }
                AppendIndent(builder, indent);
                builder.Append('}');
            }

            void AppendStringArray(StringBuilder builder, IReadOnlyList<string> values, int indent)
            {
                if (values == null || values.Count == 0)
                {
                    builder.Append("[]");
                    return;
                }

                builder.Append("[\n");
                for (int i = 0; i < values.Count; i++)
                {
                    AppendIndent(builder, indent + 1);
                    builder.Append(EscapeString(values[i] ?? string.Empty));
                    if (i < values.Count - 1)
                    {
                        builder.Append(',');
                    }
                    builder.Append('\n');
                }
                AppendIndent(builder, indent);
                builder.Append(']');
            }

            var dependencyUpdates = FileDependencyUpdates;
            var builder = new StringBuilder();
            builder.Append("{\n");

            AppendIndent(builder, 1);
            builder.Append("\"newFiles\": [\n");
            for (int i = 0; i < NewFilesImported.Count; i++)
            {
                var file = NewFilesImported[i];
                AppendIndent(builder, 2);
                builder.Append("{\n");

                AppendIndent(builder, 3);
                builder.Append("\"filePath\": ");
                builder.Append(EscapeString(file));
                builder.Append(",\n");

                AppendIndent(builder, 3);
                builder.Append("\"dependencies\": ");
                if (dependencyUpdates.TryGetValue(file, out var deps) && deps != null && deps.Count > 0)
                {
                    builder.Append("[\n");
                    for (int j = 0; j < deps.Count; j++)
                    {
                        var dep = deps[j];
                        AppendIndent(builder, 4);
                        builder.Append("{\n");

                        AppendIndent(builder, 5);
                        builder.Append("\"dependencyName\": ");
                        builder.Append(EscapeString(dep.DependencyName));
                        builder.Append(",\n");

                        AppendIndent(builder, 5);
                        builder.Append("\"dependencyPath\": ");
                        builder.Append(EscapeString(dep.DependencyPath?.Replace(".meta", string.Empty)));
                        builder.Append(",\n");

                        AppendIndent(builder, 5);
                        builder.Append("\"oldGuid\": ");
                        builder.Append(EscapeString(dep.OldGuid));
                        builder.Append(",\n");

                        AppendIndent(builder, 5);
                        builder.Append("\"newGuid\": ");
                        builder.Append(EscapeString(dep.NewGuid));
                        builder.Append(",\n");

                        AppendIndent(builder, 5);
                        builder.Append("\"fileIdRemappings\": ");
                        AppendFileIdRemapObject(builder, CollectFileIdRemaps(dep.FileIdRemappings), 5);
                        builder.Append('\n');

                        AppendIndent(builder, 4);
                        builder.Append('}');
                        if (j < deps.Count - 1)
                        {
                            builder.Append(',');
                        }
                        builder.Append('\n');
                    }
                    AppendIndent(builder, 3);
                    builder.Append("],\n");
                }
                else
                {
                    builder.Append("[],\n");
                }

                AppendIndent(builder, 3);
                builder.Append("\"fileIdRemappings\": ");
                AppendFileIdRemapObject(builder, CollectFileIdRemapsForPath(file), 3);
                builder.Append('\n');

                AppendIndent(builder, 2);
                builder.Append('}');
                if (i < NewFilesImported.Count - 1)
                {
                    builder.Append(',');
                }
                builder.Append('\n');
            }
            AppendIndent(builder, 1);
            builder.Append("],\n");

            AppendIndent(builder, 1);
            builder.Append("\"skippedFiles\": ");
            AppendStringArray(builder, SkippedFiles, 1);
            builder.Append(",\n");

            AppendIndent(builder, 1);
            builder.Append("\"uuidMappings\": [\n");
            for (int i = 0; i < Mappings.Count; i++)
            {
                var mapping = Mappings[i];
                AppendIndent(builder, 2);
                builder.Append("{\n");

                AppendIndent(builder, 3);
                builder.Append("\"assetPath\": ");
                builder.Append(EscapeString(mapping.AssetPath?.Replace(".meta", string.Empty)));
                builder.Append(",\n");

                AppendIndent(builder, 3);
                builder.Append("\"assetName\": ");
                builder.Append(EscapeString(mapping.AssetName));
                builder.Append(",\n");

                AppendIndent(builder, 3);
                builder.Append("\"oldGuid\": ");
                builder.Append(EscapeString(mapping.OldGuid));
                builder.Append(",\n");

                AppendIndent(builder, 3);
                builder.Append("\"newGuid\": ");
                builder.Append(EscapeString(mapping.NewGuid));
                builder.Append(",\n");

                AppendIndent(builder, 3);
                builder.Append("\"fileIdRemappings\": ");
                AppendFileIdRemapObject(builder, CollectFileIdRemapsForPath(mapping.AssetPath), 3);
                builder.Append('\n');

                AppendIndent(builder, 2);
                builder.Append('}');
                if (i < Mappings.Count - 1)
                {
                    builder.Append(',');
                }
                builder.Append('\n');
            }
            AppendIndent(builder, 1);
            builder.Append("]\n");

            builder.Append(",\n");
            AppendIndent(builder, 1);
            builder.Append("\"scriptGuidRemappings\": [\n");
            var orderedScriptRemaps = ScriptGuidRemappings
                .OrderBy(r => r.TargetAssetPath)
                .ThenBy(r => r.ScriptType)
                .ThenBy(r => r.StubGuid)
                .ToList();
            for (int i = 0; i < orderedScriptRemaps.Count; i++)
            {
                var remap = orderedScriptRemaps[i];
                AppendIndent(builder, 2);
                builder.Append("{\n");

                AppendIndent(builder, 3);
                builder.Append("\"assetPath\": ");
                builder.Append(EscapeString(remap.TargetAssetPath));
                builder.Append(",\n");

                AppendIndent(builder, 3);
                builder.Append("\"scriptType\": ");
                builder.Append(EscapeString(remap.ScriptType));
                builder.Append(",\n");

                AppendIndent(builder, 3);
                builder.Append("\"stubScriptPath\": ");
                builder.Append(EscapeString(remap.StubScriptPath));
                builder.Append(",\n");

                AppendIndent(builder, 3);
                builder.Append("\"realScriptPath\": ");
                builder.Append(EscapeString(remap.RealScriptPath));
                builder.Append(",\n");

                AppendIndent(builder, 3);
                builder.Append("\"oldGuid\": ");
                builder.Append(EscapeString(remap.StubGuid));
                builder.Append(",\n");

                AppendIndent(builder, 3);
                builder.Append("\"newGuid\": ");
                builder.Append(EscapeString(remap.RealGuid));
                builder.Append('\n');

                AppendIndent(builder, 2);
                builder.Append('}');
                if (i < orderedScriptRemaps.Count - 1)
                {
                    builder.Append(',');
                }
                builder.Append('\n');
            }
            AppendIndent(builder, 1);
            builder.Append("]\n");

            builder.Append('}');
            return builder.ToString();
        }
    }

    [Serializable]
    public class GuidMapping
    {
        public Guid OldGuid { get; set; }
        public Guid NewGuid { get; set; }
        public string AssetName { get; set; }
        public AssetPath AssetPath { get; set; }
    }

    [Serializable]
    public class DependencyUpdate
    {
        public string DependencyName { get; set; }
        public AssetPath DependencyPath { get; set; }
        public Guid OldGuid { get; set; }
        public Guid NewGuid { get; set; }
        public List<FileIdRemapping> FileIdRemappings { get; set; } = new List<FileIdRemapping>();
    }

    [Serializable]
    public class FileDependencyMapping
    {
        public FilePath FilePath { get; set; }
        public List<DependencyUpdate> Dependencies { get; set; }
    }

    [Serializable]
    public class FileIdRemapping
    {
        public Guid FileGuid { get; set; }
        public FilePath FilePath { get; set; }
        public FileID OldFileId { get; set; }
        public FileID NewFileId { get; set; }
    }

    [Serializable]
    public class ScriptGuidRemapping
    {
        public string TargetAssetPath { get; set; }
        public string ScriptType { get; set; }
        public string StubScriptPath { get; set; }
        public string RealScriptPath { get; set; }
        public Guid StubGuid { get; set; }
        public Guid RealGuid { get; set; }
    }
}
