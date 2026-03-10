namespace KPACS.Viewer.Models;

public sealed class DicomNetworkSettings
{
    public string LocalAeTitle { get; set; } = "KPACS";
    public int LocalPort { get; set; } = 11112;
    public string InboxDirectory { get; set; } = string.Empty;
    public bool EnableDicomCommunicationLogging { get; set; }
    public string DicomCommunicationLogPath { get; set; } = string.Empty;
    public string SelectedArchiveId { get; set; } = string.Empty;
    public List<RemoteArchiveEndpoint> Archives { get; set; } =
    [
        new RemoteArchiveEndpoint
        {
            Name = "Default Archive",
            Host = "127.0.0.1",
            Port = 104,
            RemoteAeTitle = "ARCHIVE",
        },
    ];

    public RemoteArchiveEndpoint? GetSelectedArchive()
    {
        if (!string.IsNullOrWhiteSpace(SelectedArchiveId))
        {
            RemoteArchiveEndpoint? selected = Archives.FirstOrDefault(archive => string.Equals(archive.Id, SelectedArchiveId, StringComparison.Ordinal));
            if (selected is not null)
            {
                return selected;
            }
        }

        return Archives.FirstOrDefault();
    }

    public void Normalize(string applicationDirectory)
    {
        LocalAeTitle = NormalizeAeTitle(LocalAeTitle, "KPACS");
        LocalPort = LocalPort <= 0 ? 11112 : LocalPort;
        InboxDirectory = string.IsNullOrWhiteSpace(InboxDirectory)
            ? Path.Combine(applicationDirectory, "network-inbox")
            : InboxDirectory.Trim();
        DicomCommunicationLogPath = string.IsNullOrWhiteSpace(DicomCommunicationLogPath)
            ? Path.Combine(applicationDirectory, "dicom-communication.log")
            : Path.GetFullPath(DicomCommunicationLogPath.Trim());

        if (Archives.Count == 0)
        {
            Archives.Add(new RemoteArchiveEndpoint());
        }

        foreach (RemoteArchiveEndpoint archive in Archives)
        {
            archive.Normalize();
        }

        RemoteArchiveEndpoint selectedArchive = GetSelectedArchive() ?? Archives[0];
        SelectedArchiveId = selectedArchive.Id;
    }

    internal static string NormalizeAeTitle(string? value, string fallback)
    {
        string trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            trimmed = fallback;
        }

        return trimmed.Length <= 16 ? trimmed : trimmed[..16];
    }
}

public sealed class RemoteArchiveEndpoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Default Archive";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 104;
    public string RemoteAeTitle { get; set; } = "ARCHIVE";

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            Id = Guid.NewGuid().ToString("N");
        }

        Name = string.IsNullOrWhiteSpace(Name) ? "Default Archive" : Name.Trim();
        Host = string.IsNullOrWhiteSpace(Host) ? "127.0.0.1" : Host.Trim();
        Port = Port <= 0 ? 104 : Port;
        RemoteAeTitle = DicomNetworkSettings.NormalizeAeTitle(RemoteAeTitle, "ARCHIVE");
    }
}

public sealed class RemoteStudySearchResult
{
    public required StudyListItem Study { get; init; }
    public required RemoteArchiveEndpoint Archive { get; init; }
    public required KPACS.DCMClasses.Models.StudyInfo LegacyStudy { get; init; }
}

public sealed class RemoteSeriesPreview
{
    public required KPACS.DCMClasses.Models.SeriesInfo LegacySeries { get; init; }
    public List<KPACS.DCMClasses.Models.ImageInfo> Images { get; } = [];

    public KPACS.DCMClasses.Models.ImageInfo? GetRepresentativeImage()
    {
        if (Images.Count == 0)
        {
            return null;
        }

        List<KPACS.DCMClasses.Models.ImageInfo> ordered = Images
            .OrderBy(image => TryParseInt(image.ImageNumber))
            .ThenBy(image => image.SopInstUid, StringComparer.Ordinal)
            .ToList();

        return ordered[ordered.Count / 2];
    }

    private static int TryParseInt(string? value)
    {
        return int.TryParse(value, out int parsed) ? parsed : int.MaxValue;
    }
}
