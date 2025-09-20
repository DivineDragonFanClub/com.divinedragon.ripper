using System;
using System.Collections.Generic;
using System.Linq;

namespace DivineDragon
{
    public enum SkipReason
    {
        AlreadyExists,
        DuplicateShader,
        DuplicateAssembly,
        InvalidAssembly,
        Other
    }

    public enum FileType
    {
        Asset,
        Meta
    }

    [Serializable]
    public class CopyAssetOperation
    {
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public string UnityPath { get; set; }
        public bool IsNew { get; set; }
        public FileType Kind { get; set; }
    }

    [Serializable]
    public class SkipAssetOperation
    {
        public string UnityPath { get; set; }
        public SkipReason Reason { get; set; }
        public string Details { get; set; }
    }

    [Serializable]
    public class GuidRemapOperation
    {
        public string AssetPath { get; set; }
        public string AssetName { get; set; }
        public string OldGuid { get; set; }
        public string NewGuid { get; set; }
    }

    [Serializable]
    public class FileIdRemapOperation
    {
        public string AssetPath { get; set; }
        public string Guid { get; set; }
        public long OldFileId { get; set; }
        public long NewFileId { get; set; }
    }

    [Serializable]
    public class DependencyOperation
    {
        public string AssetPath { get; set; }
        public string DependencyName { get; set; }
        public string DependencyPath { get; set; }
        public string OldGuid { get; set; }
        public string NewGuid { get; set; }
    }

    [Serializable]
    public class ScriptRemapOperation
    {
        public string TargetAssetPath { get; set; }
        public string AssetPath
        {
            get => TargetAssetPath;
            set => TargetAssetPath = value;
        }
        public string ScriptType { get; set; }
        public string StubGuid { get; set; }
        public string RealGuid { get; set; }
        public string StubScriptPath { get; set; }
        public string RealScriptPath { get; set; }
    }

    [Serializable]
    public class SyncOperations
    {
        public List<CopyAssetOperation> Copies { get; } = new List<CopyAssetOperation>();
        public List<SkipAssetOperation> Skips { get; } = new List<SkipAssetOperation>();
        public List<GuidRemapOperation> GuidRemaps { get; } = new List<GuidRemapOperation>();
        public List<FileIdRemapOperation> FileIdRemaps { get; } = new List<FileIdRemapOperation>();
        public List<DependencyOperation> Dependencies { get; } = new List<DependencyOperation>();
        public List<ScriptRemapOperation> ScriptRemaps { get; } = new List<ScriptRemapOperation>();

        public int NewFileCount => Copies
            .Where(c => c.IsNew && c.Kind == FileType.Asset)
            .Select(c => c.UnityPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        public int SkippedFileCount => Skips.Count(s => s.Reason == SkipReason.AlreadyExists || s.Reason == SkipReason.DuplicateShader);
        public int DuplicateShaderCount => Skips.FindAll(s => s.Reason == SkipReason.DuplicateShader).Count;
        public int DuplicateAssemblyCount => Skips.FindAll(s => s.Reason == SkipReason.DuplicateAssembly).Count;
        public int GuidRemapCount => GuidRemaps.Count;
        public int ScriptRemapCount => ScriptRemaps.Count;
        public int FileIdRemapCount => FileIdRemaps.Count;
    }

}
