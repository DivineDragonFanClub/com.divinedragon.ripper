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
    public class GuidSyncReport
    {
        public List<FilePath> NewFilesImported { get; private set; }

        public List<FilePath> SkippedFiles { get; private set; }

        public List<GuidMapping> Mappings { get; private set; }

        public List<FileIdRemapping> FileIdRemappings { get; private set; }

        [SerializeField]
        private List<FileDependencyMapping> _fileDependencyMappings;

        private readonly Dictionary<Guid, GuidMapping> _byOldGuid = new Dictionary<Guid, GuidMapping>();

        public string SummaryText { get; private set; }

        public GuidSyncReport()
        {
            NewFilesImported = new List<FilePath>();
            SkippedFiles = new List<FilePath>();
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
                        NewGuid = mapping.NewGuid
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

        public void AddFileIdRemapping(Guid fileGuid, FilePath filePath, FileID oldFileId, FileID newFileId)
        {
            FileIdRemappings.Add(new FileIdRemapping
            {
                FileGuid = fileGuid,
                FilePath = filePath,
                OldFileId = oldFileId,
                NewFileId = newFileId
            });
        }

        public void FinalizeReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"New Files Imported: {NewFilesImported.Count}");
            sb.AppendLine($"Files Skipped: {SkippedFiles.Count}");
            sb.AppendLine($"UUID Mappings: {Mappings.Count}");
            sb.AppendLine($"FileID Remappings: {FileIdRemappings.Count}");

            SummaryText = sb.ToString();
        }

        public string ToJson()
        {
            var newFilesWithDeps = new List<JsonFileWithDependencies>();
            foreach (var file in NewFilesImported)
            {
                var fileWithDeps = new JsonFileWithDependencies
                {
                    filePath = file,
                    dependencies = new List<JsonDependencyMapping>()
                };

                if (FileDependencyUpdates.TryGetValue(file, out var deps))
                {
                    foreach (var dep in deps)
                    {
                        fileWithDeps.dependencies.Add(new JsonDependencyMapping
                        {
                            dependencyName = dep.DependencyName,
                            dependencyPath = dep.DependencyPath.Replace(".meta", ""),
                            oldGuid = dep.OldGuid,
                            newGuid = dep.NewGuid
                        });
                    }
                }

                newFilesWithDeps.Add(fileWithDeps);
            }

            var jsonObject = new JsonReport
            {
                newFiles = newFilesWithDeps.ToArray(),
                skippedFiles = SkippedFiles.ToArray(),
                uuidMappings = Mappings.Select(m => new JsonGuidMapping
                {
                    assetPath = m.AssetPath.Replace(".meta", ""),
                    assetName = m.AssetName,
                    oldGuid = m.OldGuid,
                    newGuid = m.NewGuid
                }).ToArray()
            };

            return JsonUtility.ToJson(jsonObject, true);
        }

        [Serializable]
        private class JsonReport
        {
            public JsonFileWithDependencies[] newFiles;
            public string[] skippedFiles;
            public JsonGuidMapping[] uuidMappings;
        }

        [Serializable]
        private class JsonFileWithDependencies
        {
            public string filePath;
            public List<JsonDependencyMapping> dependencies;
        }

        [Serializable]
        private class JsonDependencyMapping
        {
            public string dependencyName;
            public string dependencyPath;
            public string oldGuid;
            public string newGuid;
        }

        [Serializable]
        private class JsonGuidMapping
        {
            public string assetPath;
            public string assetName;
            public string oldGuid;
            public string newGuid;
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
}