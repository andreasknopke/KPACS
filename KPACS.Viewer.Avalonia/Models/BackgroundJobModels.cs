namespace KPACS.Viewer.Models;

public enum BackgroundJobType
{
    Import,
    Send,
}

public enum BackgroundJobState
{
    Queued,
    Running,
    Completed,
    Failed,
}

public sealed class BackgroundJobInfo
{
    public Guid JobId { get; init; }
    public BackgroundJobType JobType { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public BackgroundJobState State { get; set; } = BackgroundJobState.Queued;
    public int CompletedUnits { get; set; }
    public int TotalUnits { get; set; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string LogPath { get; init; } = string.Empty;
}
