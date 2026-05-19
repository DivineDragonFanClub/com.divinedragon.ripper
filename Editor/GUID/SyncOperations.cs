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
    public class SyncTiming
    {
        public long AssetRipperMs;
        public long PlanMs;
        public long ExecutionMs;
        public long ReportMs;
        public long TotalMs;

        public long DirectoryCreateMs;
        public long CopyMs;
        public long ScriptRemapMs;
        public long GuidAnalyzeMs;
        public long GuidApplyMs;
        public long GuidTotalMs;
        public long CleanupMs;
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

    /// <summary>
    /// A GUID reference in a main-project YAML file that resolves to no meta file in either the
    /// main project or the AssetRipper output. Typical cause: a previous dump session imported an
    /// asset (e.g. a font) that internally references another asset (e.g. its atlas texture) by
    /// the AssetRipper-assigned GUID, and a later dump (or manual re-import) changed the target's
    /// project-side GUID. The synchronizer's normal path-matching pass can't recover from this
    /// because the orphan GUID exists nowhere — there's nothing to match against.
    ///
    /// We surface these so they're visible (Editor.log, sync report) and, where we have a high-
    /// confidence filename-proximity guess, propose a remap target the user can apply with one click.
    /// </summary>
    [Serializable]
    public class OrphanReferenceOperation
    {
        /// <summary>Unity-style path of the file that contains the dead reference.</summary>
        public string AssetPath { get; set; }
        /// <summary>The GUID that resolves to nothing in either project.</summary>
        public string OrphanGuid { get; set; }
        /// <summary>1-based line in the asset file where the reference appears (first occurrence).</summary>
        public int LineNumber { get; set; }
        /// <summary>How many times the orphan GUID is referenced in this file.</summary>
        public int Occurrences { get; set; }
        /// <summary>If we found a likely fix by filename proximity, the candidate's Unity path.</summary>
        public string SuggestedAssetPath { get; set; }
        /// <summary>If we found a likely fix, the candidate's GUID.</summary>
        public string SuggestedGuid { get; set; }
        /// <summary>Why we believe SuggestedGuid is the right target ("name match", etc.). Empty when no suggestion.</summary>
        public string SuggestionReason { get; set; }
        /// <summary>Set when the scanner already rewrote the file in place using SuggestedGuid. UI uses this to render an applied/unapplied state.</summary>
        public bool WasAutoFixed { get; set; }
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
        public List<OrphanReferenceOperation> OrphanReferences { get; } = new List<OrphanReferenceOperation>();

        public SyncTiming Timing { get; } = new SyncTiming();

        public int NewFileCount => Copies
            .Where(c => c.IsNew && c.Kind == FileType.Asset)
            .Select(c => c.UnityPath)
            .Distinct()
            .Count();

        public int SkippedFileCount => Skips.Count(s => s.Reason == SkipReason.AlreadyExists || s.Reason == SkipReason.DuplicateShader);
        public int DuplicateShaderCount => Skips.FindAll(s => s.Reason == SkipReason.DuplicateShader).Count;
        public int DuplicateAssemblyCount => Skips.FindAll(s => s.Reason == SkipReason.DuplicateAssembly).Count;
        public int GuidRemapCount => GuidRemaps.Count;
        public int ScriptRemapCount => ScriptRemaps.Count;
        public int FileIdRemapCount => FileIdRemaps.Count;
        public int OrphanReferenceCount => OrphanReferences.Count;
    }

}
