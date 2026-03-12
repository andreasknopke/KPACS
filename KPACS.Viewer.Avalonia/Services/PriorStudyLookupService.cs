using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public enum PriorStudyLookupMode
{
    LocalRepository,
    RemoteArchive,
}

public sealed class PriorStudyLookupService
{
    private readonly ImageboxRepository _repository;
    private readonly DicomRemoteStudyBrowserService _remoteStudyBrowserService;

    public PriorStudyLookupService(ImageboxRepository repository, DicomRemoteStudyBrowserService remoteStudyBrowserService)
    {
        _repository = repository;
        _remoteStudyBrowserService = remoteStudyBrowserService;
    }

    public async Task<IReadOnlyList<PriorStudySummary>> FindPriorStudiesAsync(StudyListItem currentStudy, PriorStudyLookupMode lookupMode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentStudy);

        if (string.IsNullOrWhiteSpace(currentStudy.PatientId) && string.IsNullOrWhiteSpace(currentStudy.PatientName))
        {
            return [];
        }

        return lookupMode switch
        {
            PriorStudyLookupMode.RemoteArchive => await FindRemotePriorStudiesMappedAsync(currentStudy, cancellationToken),
            _ => await FindLocalPriorStudiesMappedAsync(currentStudy, cancellationToken),
        };
    }

    public async Task LoadPriorStudyPreviewAsync(PriorStudySummary priorStudy, Action<StudyDetails> onUpdated, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(priorStudy);
        ArgumentNullException.ThrowIfNull(onUpdated);

        if (!priorStudy.IsRemote)
        {
            StudyDetails? localDetails = await _repository.GetStudyDetailsByStudyInstanceUidAsync(priorStudy.StudyInstanceUid, cancellationToken);
            if (localDetails is not null)
            {
                onUpdated(localDetails);
            }

            return;
        }

        RemoteArchiveEndpoint archive = _remoteStudyBrowserService.GetArchiveById(priorStudy.ArchiveId)
            ?? throw new InvalidOperationException("The remote archive for the selected prior study is no longer configured.");

        var remoteResult = new RemoteStudySearchResult
        {
            Archive = archive,
            Study = new StudyListItem
            {
                StudyInstanceUid = priorStudy.StudyInstanceUid,
                StudyDescription = priorStudy.StudyDescription,
                Modalities = priorStudy.Modalities,
                StudyDate = priorStudy.StudyDate,
                StoragePath = priorStudy.SourceLabel,
                IsPreviewOnly = true,
            },
            LegacyStudy = new KPACS.DCMClasses.Models.StudyInfo
            {
                StudyInstanceUid = priorStudy.StudyInstanceUid,
                StudyDescription = priorStudy.StudyDescription,
                Modalities = priorStudy.Modalities,
                StudyDate = priorStudy.StudyDate,
                Server = priorStudy.SourceLabel,
            },
        };

        await _remoteStudyBrowserService.LoadRepresentativeStudyPreviewIncrementallyAsync(remoteResult, onUpdated, cancellationToken);
    }

    private async Task<IReadOnlyList<PriorStudySummary>> FindLocalPriorStudiesMappedAsync(StudyListItem currentStudy, CancellationToken cancellationToken)
    {
        IReadOnlyList<StudyListItem> studies = await _repository.FindPriorStudiesAsync(currentStudy, int.MaxValue, cancellationToken);

        return studies
            .Where(study => IsSamePatient(study, currentStudy))
            .Where(study => !string.Equals(study.StudyInstanceUid, currentStudy.StudyInstanceUid, StringComparison.Ordinal))
            .DistinctBy(study => study.StudyInstanceUid)
            .OrderByDescending(study => study.StudyDate)
            .Select(study => new PriorStudySummary
            {
                StudyKey = study.StudyKey,
                StudyInstanceUid = study.StudyInstanceUid,
                StudyDescription = study.StudyDescription,
                Modalities = study.Modalities,
                StudyDate = study.StudyDate,
                SourceLabel = study.StoragePath,
                IsRemote = false,
                IsNewerThanCurrentStudy = IsNewerByDate(study.StudyDate, currentStudy.StudyDate),
            })
            .ToList();
    }

    private async Task<IReadOnlyList<PriorStudySummary>> FindRemotePriorStudiesMappedAsync(StudyListItem currentStudy, CancellationToken cancellationToken)
    {
        var query = new StudyQuery
        {
            PatientId = currentStudy.PatientId,
            PatientName = string.IsNullOrWhiteSpace(currentStudy.PatientId) ? currentStudy.PatientName : string.Empty,
            PatientBirthDate = string.IsNullOrWhiteSpace(currentStudy.PatientId) ? currentStudy.PatientBirthDate : string.Empty,
        };

        List<RemoteStudySearchResult> results = await _remoteStudyBrowserService.SearchStudiesAsync(query, cancellationToken);
        return results
            .Where(result => IsSamePatient(result.Study, currentStudy))
            .Where(result => !string.Equals(result.Study.StudyInstanceUid, currentStudy.StudyInstanceUid, StringComparison.Ordinal))
            .DistinctBy(result => result.Study.StudyInstanceUid)
            .OrderByDescending(result => result.Study.StudyDate)
            .Select(result => new PriorStudySummary
            {
                StudyKey = result.Study.StudyKey,
                StudyInstanceUid = result.Study.StudyInstanceUid,
                StudyDescription = result.Study.StudyDescription,
                Modalities = result.Study.Modalities,
                StudyDate = result.Study.StudyDate,
                SourceLabel = result.Archive.Name,
                IsRemote = true,
                ArchiveId = result.Archive.Id,
                IsNewerThanCurrentStudy = IsNewerByDate(result.Study.StudyDate, currentStudy.StudyDate),
            })
            .ToList();
    }

    private static bool IsSamePatient(StudyListItem candidate, StudyListItem currentStudy)
    {
        if (!string.IsNullOrWhiteSpace(currentStudy.PatientId) && !string.IsNullOrWhiteSpace(candidate.PatientId))
        {
            return string.Equals(candidate.PatientId.Trim(), currentStudy.PatientId.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        bool sameName = string.Equals(candidate.PatientName.Trim(), currentStudy.PatientName.Trim(), StringComparison.OrdinalIgnoreCase);
        if (!sameName)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(currentStudy.PatientBirthDate)
            || string.IsNullOrWhiteSpace(candidate.PatientBirthDate)
            || string.Equals(candidate.PatientBirthDate.Trim(), currentStudy.PatientBirthDate.Trim(), StringComparison.Ordinal);
    }

    private static bool IsNewerByDate(string candidateStudyDate, string currentStudyDate)
    {
        DateOnly? current = ParseDicomDate(currentStudyDate);
        DateOnly? candidate = ParseDicomDate(candidateStudyDate);

        if (current is null || candidate is null)
        {
            return false;
        }

        return candidate.Value > current.Value;
    }

    private static DateOnly? ParseDicomDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 8)
        {
            return null;
        }

        return DateOnly.TryParseExact(value[..8], "yyyyMMdd", out DateOnly parsed)
            ? parsed
            : null;
    }
}