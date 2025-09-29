using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Type aliases for clarity
using Guid = System.String;
using FilePath = System.String;
using AssetPath = System.String;

namespace DivineDragon
{
    [Serializable]
    public class AssemblySkipInfo
    {
        public string AssemblyName { get; set; }
        public string FolderPath { get; set; }
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
    }

    [Serializable]
    public class FileIdRemapping
    {
        public Guid FileGuid { get; set; }
        public FilePath FilePath { get; set; }
        public long OldFileId { get; set; }
        public long NewFileId { get; set; }
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

    public class GuidSyncReport
    {
        public SyncOperations Operations { get; private set; }

        public List<FilePath> NewFilesImported { get; private set; }
        public List<FilePath> SkippedFiles { get; private set; }
        public List<FilePath> DuplicateShaders { get; private set; }
        public List<AssemblySkipInfo> DuplicateAssemblies { get; private set; }
        public List<GuidMapping> Mappings { get; private set; }
        public List<FileIdRemapping> FileIdRemappings { get; private set; }
        public List<ScriptGuidRemapping> ScriptGuidRemappings { get; private set; }
        public Dictionary<FilePath, List<DependencyUpdate>> FileDependencyUpdates { get; private set; }

        public string SummaryText { get; private set; }
        public SyncTiming Timing { get; private set; }

        private GuidSyncReport()
        {
        }

        [Serializable]
        private class ReportJsonPayload
        {
            public List<NewFileJson> newFiles;
            public List<string> skippedFiles;
            public List<GuidMappingJson> uuidMappings;
            public List<ScriptRemapJson> scriptGuidRemappings;
            public List<string> duplicateShaders;
            public List<AssemblySkipInfoJson> duplicateAssemblies;
            public List<FileIdRemapJson> fileIdRemappings;
            public List<FileDependencyJson> fileDependencies;
            public string summary;
            public string operationsJson;
            public SyncTiming timing;
        }

        [Serializable]
        private class NewFileJson
        {
            public string filePath;
            public List<DependencyJson> dependencies;
            public List<FileIdRemapJson> fileIdRemappings;
        }

        [Serializable]
        private class DependencyJson
        {
            public string dependencyName;
            public string dependencyPath;
            public Guid oldGuid;
            public Guid newGuid;
        }

        [Serializable]
        private class GuidMappingJson
        {
            public string assetPath;
            public string assetName;
            public Guid oldGuid;
            public Guid newGuid;
        }

        [Serializable]
        private class FileIdRemapJson
        {
            public Guid fileGuid;
            public string filePath;
            public long oldFileId;
            public long newFileId;
        }

        [Serializable]
        private class AssemblySkipInfoJson
        {
            public string assemblyName;
            public string folderPath;
        }

        [Serializable]
        private class FileDependencyJson
        {
            public string filePath;
            public List<DependencyJson> dependencies;
        }

        [Serializable]
        private class ScriptRemapJson
        {
            public string assetPath;
            public string scriptType;
            public string stubScriptPath;
            public string realScriptPath;
            public Guid oldGuid;
            public Guid newGuid;
        }

        public static GuidSyncReport CreateFromOperations(SyncOperations operations)
        {
            var report = new GuidSyncReport
            {
                Operations = operations ?? new SyncOperations()
            };
            report.Timing = report.Operations.Timing ?? new SyncTiming();
            report.Populate();
            return report;
        }

        public static GuidSyncReport FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            ReportJsonPayload payload;
            try
            {
                payload = JsonUtility.FromJson<ReportJsonPayload>(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to parse GUID sync report JSON: {ex.Message}");
                return null;
            }

            if (payload == null)
            {
                return null;
            }

            var report = new GuidSyncReport
            {
                Operations = new SyncOperations()
            };
            report.Timing = report.Operations.Timing;

            report.NewFilesImported = payload.newFiles?
                .Select(n => n?.filePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<FilePath>();

            report.SkippedFiles = payload.skippedFiles?
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<FilePath>();

            report.DuplicateShaders = payload.duplicateShaders?
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<FilePath>();

            report.DuplicateAssemblies = payload.duplicateAssemblies?
                .Select(a => new AssemblySkipInfo
                {
                    AssemblyName = a?.assemblyName,
                    FolderPath = a?.folderPath
                })
                .Where(a => a != null && !string.IsNullOrEmpty(a.FolderPath))
                .OrderBy(a => a.FolderPath, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<AssemblySkipInfo>();

            report.Mappings = payload.uuidMappings?
                .Select(m => new GuidMapping
                {
                    AssetPath = m?.assetPath,
                    AssetName = m?.assetName,
                    OldGuid = m?.oldGuid,
                    NewGuid = m?.newGuid
                })
                .Where(m => m != null && !string.IsNullOrEmpty(m.AssetPath))
                .OrderBy(m => m.AssetName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<GuidMapping>();

            report.FileIdRemappings = payload.fileIdRemappings?
                .Select(f => new FileIdRemapping
                {
                    FileGuid = f.fileGuid,
                    FilePath = f.filePath,
                    OldFileId = f.oldFileId,
                    NewFileId = f.newFileId
                })
                .Where(f => !string.IsNullOrEmpty(f.FilePath))
                .ToList() ?? new List<FileIdRemapping>();

            report.ScriptGuidRemappings = payload.scriptGuidRemappings?
                .Select(s => new ScriptGuidRemapping
                {
                    TargetAssetPath = s.assetPath,
                    ScriptType = s.scriptType,
                    StubScriptPath = s.stubScriptPath,
                    RealScriptPath = s.realScriptPath,
                    StubGuid = s.oldGuid,
                    RealGuid = s.newGuid
                })
                .Where(s => s != null)
                .ToList() ?? new List<ScriptGuidRemapping>();

            report.FileDependencyUpdates = BuildDependencyDictionary(payload);

            report.SummaryText = payload.summary ?? string.Empty;

            if (payload.timing != null)
            {
                CopyTiming(payload.timing, report.Timing);
            }

            return report;
        }

        private static Dictionary<FilePath, List<DependencyUpdate>> BuildDependencyDictionary(ReportJsonPayload payload)
        {
            var comparer = StringComparer.OrdinalIgnoreCase;
            var result = new Dictionary<FilePath, List<DependencyUpdate>>(comparer);

            void Merge(string path, IEnumerable<DependencyJson> deps)
            {
                if (string.IsNullOrEmpty(path))
                    return;

                if (!result.TryGetValue(path, out var list))
                {
                    list = new List<DependencyUpdate>();
                    result[path] = list;
                }

                if (deps == null)
                    return;

                foreach (var dep in deps)
                {
                    if (dep == null)
                        continue;

                    list.Add(new DependencyUpdate
                    {
                        DependencyName = dep.dependencyName,
                        DependencyPath = dep.dependencyPath,
                        OldGuid = dep.oldGuid,
                        NewGuid = dep.newGuid
                    });
                }
            }

            if (payload.fileDependencies != null)
            {
                foreach (var entry in payload.fileDependencies)
                {
                    Merge(entry?.filePath, entry?.dependencies);
                }
            }

            if (payload.newFiles != null)
            {
                foreach (var newFile in payload.newFiles)
                {
                    Merge(newFile?.filePath, newFile?.dependencies);
                }
            }

            foreach (var kvp in result.ToArray())
            {
                var ordered = kvp.Value
                    .Where(d => d != null && (!string.IsNullOrEmpty(d.DependencyName) || !string.IsNullOrEmpty(d.DependencyPath)))
                    .Distinct(new DependencyUpdateComparer())
                    .OrderBy(d => d.DependencyName ?? string.Empty, comparer)
                    .ToList();
                result[kvp.Key] = ordered;
            }

            return result;
        }

        private class DependencyUpdateComparer : IEqualityComparer<DependencyUpdate>
        {
            public bool Equals(DependencyUpdate x, DependencyUpdate y)
            {
                if (ReferenceEquals(x, y))
                    return true;
                if (x is null || y is null)
                    return false;

                return string.Equals(x.DependencyName, y.DependencyName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.DependencyPath, y.DependencyPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.OldGuid, y.OldGuid, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.NewGuid, y.NewGuid, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(DependencyUpdate obj)
            {
                if (obj == null)
                    return 0;

                int hash = 17;
                hash = hash * 23 + (obj.DependencyName?.ToLowerInvariant().GetHashCode() ?? 0);
                hash = hash * 23 + (obj.DependencyPath?.ToLowerInvariant().GetHashCode() ?? 0);
                hash = hash * 23 + (obj.OldGuid?.ToLowerInvariant().GetHashCode() ?? 0);
                hash = hash * 23 + (obj.NewGuid?.ToLowerInvariant().GetHashCode() ?? 0);
                return hash;
            }
        }

        private void Populate()
        {
            NewFilesImported = Operations.Copies
                .Where(c => c.IsNew && c.Kind == FileType.Asset)
                .Select(c => c.UnityPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var skipped = new List<FilePath>();
            DuplicateShaders = new List<FilePath>();
            DuplicateAssemblies = new List<AssemblySkipInfo>();

            foreach (var skip in Operations.Skips)
            {
                switch (skip.Reason)
                {
                    case SkipReason.AlreadyExists:
                        skipped.Add(skip.UnityPath);
                        break;
                    case SkipReason.DuplicateShader:
                        skipped.Add(skip.UnityPath);
                        DuplicateShaders.Add(skip.UnityPath);
                        break;
                    case SkipReason.DuplicateAssembly:
                        DuplicateAssemblies.Add(new AssemblySkipInfo
                        {
                            AssemblyName = skip.Details,
                            FolderPath = skip.UnityPath
                        });
                        break;
                    case SkipReason.InvalidAssembly:
                        DuplicateAssemblies.Add(new AssemblySkipInfo
                        {
                            AssemblyName = skip.Details ?? "Invalid Assembly",
                            FolderPath = skip.UnityPath
                        });
                        break;
                    default:
                        skipped.Add(skip.UnityPath);
                        break;
                }
            }

            SkippedFiles = skipped
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            DuplicateShaders = DuplicateShaders
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Mappings = Operations.GuidRemaps
                .Select(op => new GuidMapping
                {
                    AssetPath = op.AssetPath,
                    AssetName = op.AssetName,
                    OldGuid = op.OldGuid,
                    NewGuid = op.NewGuid
                })
                .OrderBy(m => m.AssetPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            FileIdRemappings = Operations.FileIdRemaps
                .Select(op => new FileIdRemapping
                {
                    FileGuid = op.Guid,
                    FilePath = op.AssetPath,
                    OldFileId = op.OldFileId,
                    NewFileId = op.NewFileId
                })
                .ToList();

            ScriptGuidRemappings = Operations.ScriptRemaps
                .Select(op => new ScriptGuidRemapping
                {
                    TargetAssetPath = op.AssetPath,
                    ScriptType = op.ScriptType,
                    StubScriptPath = op.StubScriptPath,
                    RealScriptPath = op.RealScriptPath,
                    StubGuid = op.StubGuid,
                    RealGuid = op.RealGuid
                })
                .ToList();

            FileDependencyUpdates = Operations.Dependencies
                .GroupBy(d => d.AssetPath)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(d => new DependencyUpdate
                    {
                        DependencyName = d.DependencyName,
                        DependencyPath = d.DependencyPath,
                        OldGuid = d.OldGuid,
                        NewGuid = d.NewGuid
                    }).OrderBy(d => d.DependencyName, StringComparer.OrdinalIgnoreCase).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            BuildSummary();
        }

        private void BuildSummary()
        {
            var lines = new List<string>
            {
                $"New Files Imported: {NewFilesImported.Count}",
                $"Files Skipped: {SkippedFiles.Count}",
                $"Duplicate Shaders Skipped: {DuplicateShaders.Count}",
                $"Duplicate Assemblies Skipped: {DuplicateAssemblies.Count}",
                $"Script Stub Remappings: {ScriptGuidRemappings.Count}",
                $"UUID Mappings: {Mappings.Count}",
                $"FileID Remappings: {FileIdRemappings.Count}"
            };

            if (Timing != null)
            {
                if (Timing.TotalMs > 0)
                {
                    lines.Add($"Total Sync Time: {Timing.TotalMs} ms");
                }
                if (Timing.AssetRipperMs > 0)
                {
                    lines.Add($"AssetRipper Duration: {Timing.AssetRipperMs} ms");
                }
            }

            SummaryText = string.Join("\n", lines);
        }

        private static void CopyTiming(SyncTiming source, SyncTiming destination)
        {
            if (source == null || destination == null)
            {
                return;
            }

            destination.AssetRipperMs = source.AssetRipperMs;
            destination.PlanMs = source.PlanMs;
            destination.ExecutionMs = source.ExecutionMs;
            destination.ReportMs = source.ReportMs;
            destination.TotalMs = source.TotalMs;

            destination.DirectoryCreateMs = source.DirectoryCreateMs;
            destination.CopyMs = source.CopyMs;
            destination.ScriptRemapMs = source.ScriptRemapMs;
            destination.GuidAnalyzeMs = source.GuidAnalyzeMs;
            destination.GuidApplyMs = source.GuidApplyMs;
            destination.GuidTotalMs = source.GuidTotalMs;
            destination.CleanupMs = source.CleanupMs;
        }

        public string ToJson()
        {
            var payload = new ReportJsonPayload
            {
                newFiles = BuildNewFileJson(),
                skippedFiles = (SkippedFiles ?? new List<FilePath>()).ToList(),
                uuidMappings = BuildGuidMappingJson(),
                scriptGuidRemappings = BuildScriptRemapJson(),
                duplicateShaders = (DuplicateShaders ?? new List<FilePath>()).ToList(),
                duplicateAssemblies = BuildDuplicateAssemblyJson(),
                fileIdRemappings = BuildFileIdRemapJson(),
                fileDependencies = BuildFileDependencyJson(),
                summary = SummaryText,
                operationsJson = Operations != null ? JsonUtility.ToJson(Operations) : null,
                timing = Timing
            };

            return JsonUtility.ToJson(payload, true);
        }

        private List<NewFileJson> BuildNewFileJson()
        {
            if (NewFilesImported == null || NewFilesImported.Count == 0)
            {
                return new List<NewFileJson>();
            }

            return NewFilesImported
                .Select(path => new NewFileJson
                {
                    filePath = path,
                    dependencies = BuildDependencyJson(path),
                    fileIdRemappings = BuildFileIdRemapJson(path)
                })
                .ToList();
        }

        private List<DependencyJson> BuildDependencyJson(FilePath path)
        {
            if (FileDependencyUpdates == null)
            {
                return new List<DependencyJson>();
            }

            if (!FileDependencyUpdates.TryGetValue(path, out var deps) || deps == null || deps.Count == 0)
            {
                return new List<DependencyJson>();
            }

            return deps
                .Select(d => new DependencyJson
                {
                    dependencyName = d.DependencyName,
                    dependencyPath = d.DependencyPath,
                    oldGuid = d.OldGuid,
                    newGuid = d.NewGuid
                })
                .ToList();
        }

        private List<GuidMappingJson> BuildGuidMappingJson()
        {
            if (Mappings == null || Mappings.Count == 0)
            {
                return new List<GuidMappingJson>();
            }

            return Mappings
                .Select(mapping => new GuidMappingJson
                {
                    assetPath = mapping.AssetPath,
                    assetName = mapping.AssetName,
                    oldGuid = mapping.OldGuid,
                    newGuid = mapping.NewGuid
                })
                .ToList();
        }

        private List<FileIdRemapJson> BuildFileIdRemapJson()
        {
            if (FileIdRemappings == null || FileIdRemappings.Count == 0)
            {
                return new List<FileIdRemapJson>();
            }

            return FileIdRemappings
                .Select(remap => new FileIdRemapJson
                {
                    fileGuid = remap.FileGuid,
                    filePath = remap.FilePath,
                    oldFileId = remap.OldFileId,
                    newFileId = remap.NewFileId
                })
                .ToList();
        }

        private List<FileIdRemapJson> BuildFileIdRemapJson(FilePath assetPath)
        {
            if (FileIdRemappings == null || FileIdRemappings.Count == 0)
            {
                return new List<FileIdRemapJson>();
            }

            return FileIdRemappings
                .Where(remap => string.Equals(remap.FilePath, assetPath, StringComparison.OrdinalIgnoreCase))
                .Select(remap => new FileIdRemapJson
                {
                    fileGuid = remap.FileGuid,
                    filePath = remap.FilePath,
                    oldFileId = remap.OldFileId,
                    newFileId = remap.NewFileId
                })
                .ToList();
        }

        private List<AssemblySkipInfoJson> BuildDuplicateAssemblyJson()
        {
            if (DuplicateAssemblies == null || DuplicateAssemblies.Count == 0)
            {
                return new List<AssemblySkipInfoJson>();
            }

            return DuplicateAssemblies
                .Select(info => new AssemblySkipInfoJson
                {
                    assemblyName = info.AssemblyName,
                    folderPath = info.FolderPath
                })
                .ToList();
        }

        private List<FileDependencyJson> BuildFileDependencyJson()
        {
            if (FileDependencyUpdates == null || FileDependencyUpdates.Count == 0)
            {
                return new List<FileDependencyJson>();
            }

            return FileDependencyUpdates
                .Select(kvp => new FileDependencyJson
                {
                    filePath = kvp.Key,
                    dependencies = kvp.Value?.Select(dep => new DependencyJson
                    {
                        dependencyName = dep.DependencyName,
                        dependencyPath = dep.DependencyPath,
                        oldGuid = dep.OldGuid,
                        newGuid = dep.NewGuid
                    }).ToList() ?? new List<DependencyJson>()
                })
                .ToList();
        }

        private List<ScriptRemapJson> BuildScriptRemapJson()
        {
            if (ScriptGuidRemappings == null || ScriptGuidRemappings.Count == 0)
            {
                return new List<ScriptRemapJson>();
            }

            return ScriptGuidRemappings
                .Select(remap => new ScriptRemapJson
                {
                    assetPath = remap.TargetAssetPath,
                    scriptType = remap.ScriptType,
                    stubScriptPath = remap.StubScriptPath,
                    realScriptPath = remap.RealScriptPath,
                    oldGuid = remap.StubGuid,
                    newGuid = remap.RealGuid
                })
                .ToList();
        }
    }
}
