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
            public List<FileIdRemapJson> fileIdRemappings;
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
            report.Populate();
            return report;
        }

        private void Populate()
        {
            NewFilesImported = Operations.Copies
                .Where(c => c.IsNew && !c.IsMeta)
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

            SummaryText = string.Join("\n", lines);
        }

        public string ToJson()
        {
            var payload = new ReportJsonPayload
            {
                newFiles = BuildNewFileJson(),
                skippedFiles = (SkippedFiles ?? new List<FilePath>()).ToList(),
                uuidMappings = BuildGuidMappingJson(),
                scriptGuidRemappings = BuildScriptRemapJson()
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
                    fileIdRemappings = null
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
                    newGuid = mapping.NewGuid,
                    fileIdRemappings = null
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
