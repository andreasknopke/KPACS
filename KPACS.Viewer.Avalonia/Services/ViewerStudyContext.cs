using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class ViewerStudyContext
{
    public required StudyDetails StudyDetails { get; init; }
    public RemoteStudyRetrievalSession? RemoteRetrievalSession { get; init; }
    public Func<CancellationToken, Task<IReadOnlyList<PriorStudySummary>>>? LoadPriorStudiesAsync { get; init; }
    public Func<PriorStudySummary, Action<StudyDetails>, CancellationToken, Task>? LoadPriorStudyPreviewAsync { get; init; }
    public int LayoutRows { get; set; } = 1;
    public int LayoutColumns { get; set; } = 1;
}
