using System.Collections.ObjectModel;

namespace KPACS.Viewer.Models;

public sealed class ImageboxPaths
{
    public required string ApplicationDirectory { get; init; }
    public required string RootDirectory { get; init; }
    public required string DatabasePath { get; init; }
    public required string StudiesDirectory { get; init; }
}

public sealed class StudyQuery
{
    public string PatientId { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string PatientBirthDate { get; set; } = string.Empty;
    public string AccessionNumber { get; set; } = string.Empty;
    public string ReferringPhysician { get; set; } = string.Empty;
    public string StudyDescription { get; set; } = string.Empty;
    public string QuickSearch { get; set; } = string.Empty;
    public List<string> Modalities { get; set; } = [];
    public DateOnly? FromStudyDate { get; set; }
    public DateOnly? ToStudyDate { get; set; }
}

public sealed class StudyListItem
{
    public long StudyKey { get; init; }
    public string StudyInstanceUid { get; init; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string PatientId { get; set; } = string.Empty;
    public string PatientBirthDate { get; set; } = string.Empty;
    public string AccessionNumber { get; set; } = string.Empty;
    public string StudyDescription { get; set; } = string.Empty;
    public string ReferringPhysician { get; set; } = string.Empty;
    public string StudyDate { get; set; } = string.Empty;
    public string Modalities { get; set; } = string.Empty;
    public int SeriesCount { get; set; }
    public int InstanceCount { get; set; }
    public string StoragePath { get; set; } = string.Empty;
    public DateTime ImportedAtUtc { get; set; }
    public bool IsPreviewOnly { get; set; }
    public string PreviewSourcePath { get; set; } = string.Empty;

    public string DisplayStudyDate => FormatDicomDate(StudyDate);
    public string DisplayPatientBirthDate => FormatDicomDate(PatientBirthDate);
    public string SelectionId => StudyKey > 0 ? $"db:{StudyKey}" : $"preview:{StudyInstanceUid}";

    internal static string FormatDicomDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 8)
        {
            return value;
        }

        return $"{value[6..8]}.{value[4..6]}.{value[..4]}";
    }
}

public sealed class SeriesRecord
{
    public long SeriesKey { get; init; }
    public long StudyKey { get; init; }
    public string SeriesInstanceUid { get; init; } = string.Empty;
    public string Modality { get; set; } = string.Empty;
    public string SeriesDescription { get; set; } = string.Empty;
    public int SeriesNumber { get; set; }
    public int InstanceCount { get; set; }
    public List<InstanceRecord> Instances { get; } = [];
}

public sealed class InstanceRecord
{
    public long InstanceKey { get; init; }
    public long SeriesKey { get; init; }
    public string SopInstanceUid { get; init; } = string.Empty;
    public string SopClassUid { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int InstanceNumber { get; set; }
    public int FrameCount { get; set; }
}

public sealed class StudyDetails
{
    public required StudyListItem Study { get; init; }
    public List<SeriesRecord> Series { get; } = [];
    public KPACS.DCMClasses.Models.StudyInfo? LegacyStudy { get; set; }
}

public sealed class PriorStudySummary
{
    public long StudyKey { get; init; }
    public string StudyInstanceUid { get; init; } = string.Empty;
    public string StudyDescription { get; init; } = string.Empty;
    public string Modalities { get; init; } = string.Empty;
    public string StudyDate { get; init; } = string.Empty;
    public string SourceLabel { get; init; } = string.Empty;
    public bool IsRemote { get; init; }
    public string ArchiveId { get; init; } = string.Empty;

    public string DisplayLabel
    {
        get
        {
            string modality = GetPrimaryModality(Modalities);
            string description = StudyDescription.Trim();
            string title = string.IsNullOrWhiteSpace(description)
                ? (!string.IsNullOrWhiteSpace(modality) ? modality : "Prior study")
                : !string.IsNullOrWhiteSpace(modality) && !description.StartsWith(modality, StringComparison.OrdinalIgnoreCase)
                    ? $"{modality} {description}".Trim()
                    : description;

            string date = FormatShortDicomDate(StudyDate);
            return string.IsNullOrWhiteSpace(date) ? title : $"{title} {date}";
        }
    }

    public string ToolTipText
    {
        get
        {
            string fullDate = StudyListItem.FormatDicomDate(StudyDate);
            string baseText = string.IsNullOrWhiteSpace(fullDate)
                ? DisplayLabel
                : $"{DisplayLabel} • {fullDate}";
            return string.IsNullOrWhiteSpace(SourceLabel)
                ? baseText
                : $"{baseText} • {SourceLabel}";
        }
    }

    private static string GetPrimaryModality(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value
            .Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
    }

    private static string FormatShortDicomDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 8)
        {
            return string.Empty;
        }

        return $"{value[6..8]}.{value[4..6]}.{value[2..4]}";
    }
}

public sealed class ImageboxTreeNode
{
    public ImageboxTreeNode(string title, string? subtitle = null, object? tag = null)
    {
        Title = title;
        Subtitle = subtitle ?? string.Empty;
        Tag = tag;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public object? Tag { get; }
    public ObservableCollection<ImageboxTreeNode> Children { get; } = [];
}

public sealed class ImportResult
{
    public int ImportedStudies { get; set; }
    public int ImportedSeries { get; set; }
    public int ImportedInstances { get; set; }
    public int SkippedFiles { get; set; }
    public List<string> Messages { get; } = [];
}

public sealed class FilesystemFolderNode
{
    public string DisplayName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsPlaceholder { get; init; }
    public bool ChildrenLoaded { get; set; }
    public ObservableCollection<FilesystemFolderNode> Children { get; } = [];
}

public sealed class FilesystemScanResult
{
    public string SourcePath { get; init; } = string.Empty;
    public bool UsedDicomDir { get; init; }
    public string? DicomDirPath { get; init; }
    public int ScannedFiles { get; set; }
    public int SkippedFiles { get; set; }
    public List<StudyDetails> Studies { get; } = [];
    public List<string> Messages { get; } = [];
}

public sealed class FilesystemScanProgress
{
    public int ScannedFiles { get; init; }
    public int SkippedFiles { get; init; }
    public int StudyCount { get; init; }
    public string CurrentPath { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public StudyDetails? UpdatedStudy { get; init; }
}

public sealed class PseudonymizeRequest
{
    public string PatientName { get; set; } = "PSEUDONYMIZED^PATIENT";
    public string PatientId { get; set; } = $"PX-{DateTime.UtcNow:yyyyMMddHHmmss}";
    public string? AccessionNumber { get; set; }
    public string? ReferringPhysician { get; set; }
    public string? PatientBirthDate { get; set; }
}
