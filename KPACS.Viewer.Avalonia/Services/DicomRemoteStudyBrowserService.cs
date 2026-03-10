using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using KPACS.DCMClasses;
using KPACS.DCMClasses.Models;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class DicomRemoteStudyBrowserService
{
    private const int RepresentativeImageMoveConcurrency = 2;

    private readonly NetworkSettingsService _settingsService;
    private readonly ImageboxRepository _repository;

    public DicomRemoteStudyBrowserService(NetworkSettingsService settingsService, ImageboxRepository repository)
    {
        _settingsService = settingsService;
        _repository = repository;
    }

    public RemoteArchiveEndpoint? GetSelectedArchive() => _settingsService.CurrentSettings.GetSelectedArchive();

    public RemoteArchiveEndpoint? GetArchiveById(string? archiveId)
    {
        if (string.IsNullOrWhiteSpace(archiveId))
        {
            return GetSelectedArchive();
        }

        return _settingsService.CurrentSettings.Archives.FirstOrDefault(archive => string.Equals(archive.Id, archiveId, StringComparison.Ordinal))
            ?? GetSelectedArchive();
    }

    public async Task<List<RemoteStudySearchResult>> SearchStudiesAsync(StudyQuery query, CancellationToken cancellationToken = default)
    {
        RemoteArchiveEndpoint archive = GetSelectedArchive()
            ?? throw new InvalidOperationException("No remote archive is configured.");

        DicomCommunicationTrace.Log("SEARCH", $"Starting remote study search on {archive.Name} ({archive.Host}:{archive.Port}, AE {archive.RemoteAeTitle}) {SummarizeStudyQuery(query)}.");

        var client = CreateClient(archive);
        var filter = new StudyInfo
        {
            PatientId = BuildWildcardMatch(query.PatientId),
            PatientName = BuildWildcardMatch(query.PatientName),
            PatientBD = query.PatientBirthDate,
            AccessionNumber = BuildWildcardMatch(query.AccessionNumber),
            StudyDescription = BuildWildcardMatch(query.StudyDescription),
            PhysiciansName = BuildWildcardMatch(query.ReferringPhysician),
            Modalities = query.Modalities.Count switch
            {
                0 => string.Empty,
                1 => query.Modalities[0],
                _ => string.Join("\\", query.Modalities),
            },
            StudyDate = BuildDicomDateRange(query.FromStudyDate, query.ToStudyDate),
        };

        try
        {
            List<StudyInfo> remoteStudies = await client.FindStudiesAsync(filter, cancellationToken);

            return remoteStudies
                .OrderByDescending(study => study.StudyDate)
                .ThenBy(study => study.PatientName)
                .Select(study => new RemoteStudySearchResult
                {
                    Archive = archive,
                    LegacyStudy = study,
                    Study = new StudyListItem
                    {
                        StudyInstanceUid = study.StudyInstanceUid,
                        PatientName = study.PatientName,
                        PatientId = study.PatientId,
                        PatientBirthDate = study.PatientBD,
                        AccessionNumber = study.AccessionNumber,
                        StudyDescription = study.StudyDescription,
                        ReferringPhysician = study.PhysiciansName,
                        StudyDate = study.StudyDate,
                        Modalities = study.Modalities,
                        StoragePath = archive.Name,
                        InstanceCount = Math.Max(study.Image, 0),
                        IsPreviewOnly = true,
                    },
                })
                .ToList();
        }
        catch (Exception ex)
        {
            DicomCommunicationTrace.LogException("SEARCH", $"Remote study search failed on {archive.Name}", ex);
            throw;
        }
    }

    private static string BuildWildcardMatch(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (trimmed.Contains('*') || trimmed.Contains('?'))
        {
            return trimmed;
        }

        return $"*{trimmed}*";
    }

    private static string SummarizeStudyQuery(StudyQuery query)
    {
        List<string> parts = [];

        AppendQueryPart(parts, "patientId", query.PatientId);
        AppendQueryPart(parts, "patientName", query.PatientName);
        AppendQueryPart(parts, "birthDate", query.PatientBirthDate);
        AppendQueryPart(parts, "accession", query.AccessionNumber);
        AppendQueryPart(parts, "referrer", query.ReferringPhysician);
        AppendQueryPart(parts, "description", query.StudyDescription);
        AppendQueryPart(parts, "quick", query.QuickSearch);
        if (query.Modalities.Count > 0)
        {
            parts.Add($"modalities={string.Join("\\", query.Modalities)}");
        }

        if (query.FromStudyDate is not null || query.ToStudyDate is not null)
        {
            parts.Add($"dateRange={query.FromStudyDate:yyyy-MM-dd}..{query.ToStudyDate:yyyy-MM-dd}");
        }

        return parts.Count == 0 ? "[no filters]" : $"[{string.Join(", ", parts)}]";
    }

    private static void AppendQueryPart(List<string> parts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{label}={value.Trim()}");
        }
    }



    public async Task<(StudyDetails Details, List<RemoteSeriesPreview> Series)> LoadStudyPreviewAsync(RemoteStudySearchResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        StudyDetails? localStudy = await _repository.GetStudyDetailsByStudyInstanceUidAsync(result.Study.StudyInstanceUid, cancellationToken);
        var client = CreateClient(result.Archive);
        List<SeriesInfo> seriesInfos = await client.FindSeriesAsync(result.Study.StudyInstanceUid, cancellationToken);
        var seriesPreviews = new List<RemoteSeriesPreview>();
        var details = new StudyDetails
        {
            Study = new StudyListItem
            {
                StudyInstanceUid = result.Study.StudyInstanceUid,
                PatientName = result.Study.PatientName,
                PatientId = result.Study.PatientId,
                PatientBirthDate = result.Study.PatientBirthDate,
                AccessionNumber = result.Study.AccessionNumber,
                StudyDescription = result.Study.StudyDescription,
                ReferringPhysician = result.Study.ReferringPhysician,
                StudyDate = result.Study.StudyDate,
                Modalities = result.Study.Modalities,
                StoragePath = result.Archive.Name,
                IsPreviewOnly = true,
            },
        };

        foreach (SeriesInfo seriesInfo in seriesInfos.OrderBy(series => TryParseInt(series.SeriesNumber)).ThenBy(series => series.SerDesc))
        {
            List<ImageInfo> images = await client.FindImagesAsync(result.Study.StudyInstanceUid, seriesInfo.SerInstUid, cancellationToken);
            var preview = new RemoteSeriesPreview
            {
                LegacySeries = seriesInfo,
            };
            preview.Images.AddRange(images.OrderBy(image => TryParseInt(image.ImageNumber)).ThenBy(image => image.SopInstUid, StringComparer.Ordinal));
            seriesPreviews.Add(preview);

            SeriesRecord seriesRecord = new()
            {
                SeriesInstanceUid = seriesInfo.SerInstUid,
                Modality = seriesInfo.SerModality,
                SeriesDescription = seriesInfo.SerDesc,
                SeriesNumber = TryParseInt(seriesInfo.SeriesNumber),
                InstanceCount = Math.Max(images.Count, localStudy?.Series.FirstOrDefault(series => string.Equals(series.SeriesInstanceUid, seriesInfo.SerInstUid, StringComparison.Ordinal))?.Instances.Count ?? 0),
            };

            foreach (ImageInfo image in preview.Images)
            {
                seriesRecord.Instances.Add(new InstanceRecord
                {
                    SopInstanceUid = image.SopInstUid,
                    SopClassUid = image.SopClassUid,
                    InstanceNumber = TryParseInt(image.ImageNumber),
                    FrameCount = TryParseInt(image.NumberOfFrames, 1),
                    FilePath = $"Instance {image.ImageNumber}".Trim(),
                });
            }

            if (seriesRecord.Instances.Count == 0 && localStudy is not null)
            {
                SeriesRecord? localSeries = localStudy.Series.FirstOrDefault(series => string.Equals(series.SeriesInstanceUid, seriesInfo.SerInstUid, StringComparison.Ordinal));
                if (localSeries is not null)
                {
                    seriesRecord.Instances.AddRange(localSeries.Instances);
                    seriesRecord.InstanceCount = Math.Max(seriesRecord.InstanceCount, localSeries.Instances.Count);
                }
            }

            details.Series.Add(seriesRecord);
        }

        details.Study.SeriesCount = details.Series.Count;
        details.Study.InstanceCount = details.Series.Sum(series => series.InstanceCount > 0 ? series.InstanceCount : series.Instances.Count);
        details.LegacyStudy = result.LegacyStudy.Clone();
        details.LegacyStudy.Series.Clear();
        foreach (SeriesRecord series in details.Series)
        {
            details.LegacyStudy.Series.Add(new SeriesInfo
            {
                SerDesc = series.SeriesDescription,
                SeriesNumber = series.SeriesNumber > 0 ? series.SeriesNumber.ToString() : string.Empty,
                SerModality = series.Modality,
                SerInstUid = series.SeriesInstanceUid,
                StudyInstanceUid = details.Study.StudyInstanceUid,
                Images = series.Instances.Select(instance => new ImageInfo
                {
                    SopInstUid = instance.SopInstanceUid,
                    SopClassUid = instance.SopClassUid,
                    SerInstUid = series.SeriesInstanceUid,
                    StudyInstUid = details.Study.StudyInstanceUid,
                    ImageNumber = instance.InstanceNumber > 0 ? instance.InstanceNumber.ToString() : string.Empty,
                    ImgFilename = instance.FilePath,
                    NumberOfFrames = instance.FrameCount > 0 ? instance.FrameCount.ToString() : string.Empty,
                }).ToList(),
            });
        }

        return (details, seriesPreviews);
    }

    public async Task LoadRepresentativeStudyPreviewIncrementallyAsync(RemoteStudySearchResult result, Action<StudyDetails> onUpdated, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onUpdated);

        StudyDetails? existingLocal = await _repository.GetStudyDetailsByStudyInstanceUidAsync(result.Study.StudyInstanceUid, cancellationToken);
        (StudyDetails previewDetails, List<RemoteSeriesPreview> seriesPreviews) = await LoadStudyPreviewAsync(result, cancellationToken);
        if (existingLocal is not null)
        {
            MergeLocalStudyIntoPreview(previewDetails, existingLocal);
        }

        onUpdated(CloneStudyDetails(previewDetails));

        foreach (RemoteSeriesPreview preview in seriesPreviews)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ImageInfo? representative = preview.GetRepresentativeImage();
            if (representative is null || string.IsNullOrWhiteSpace(preview.LegacySeries.SerInstUid) || string.IsNullOrWhiteSpace(representative.SopInstUid))
            {
                continue;
            }

            await MoveImageAsync(result.Archive, result.Study.StudyInstanceUid, preview.LegacySeries.SerInstUid, representative.SopInstUid, _settingsService.CurrentSettings.LocalAeTitle, cancellationToken);

            await PublishMergedPreviewUpdateAsync(previewDetails, result.Study.StudyInstanceUid, onUpdated, cancellationToken);
        }

        if (!IsStudyFullyLocal(previewDetails))
        {
            _ = ContinuePriorStudyFetchAsync(result, previewDetails, onUpdated, cancellationToken);
        }
    }

    public async Task<RemoteStudyRetrievalSession> CreateRetrievalSessionAsync(RemoteStudySearchResult result, StudyDetails mergedStudy, IReadOnlyCollection<RemoteSeriesPreview> seriesPreviews, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(mergedStudy);
        ArgumentNullException.ThrowIfNull(seriesPreviews);

        RemoteArchiveEndpoint archive = result.Archive;
        DicomNetworkSettings settings = _settingsService.CurrentSettings;
        var session = new RemoteStudyRetrievalSession(
            _repository,
            CreateClient(archive),
            settings.LocalAeTitle,
            result,
            mergedStudy,
            seriesPreviews);
        await session.RefreshStudyAsync(cancellationToken);
        return session;
    }

    public async Task<bool> SendStudyAsync(StudyDetails studyDetails, IProgress<(int completed, int total)>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(studyDetails);

        RemoteArchiveEndpoint archive = GetSelectedArchive()
            ?? throw new InvalidOperationException("No remote archive is configured.");

        List<string> filePaths = studyDetails.Series
            .SelectMany(series => series.Instances)
            .Select(instance => instance.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (filePaths.Count == 0)
        {
            throw new InvalidOperationException("The selected study has no local DICOM files to send.");
        }

        DicomNetworkClient client = CreateClient(archive);
        bool success = await client.StoreSCUAsync(filePaths, progress, cancellationToken);
        if (!success)
        {
            throw new InvalidOperationException($"C-STORE failed while sending the study to {archive.Name}.");
        }

        return true;
    }

    public async Task<StudyDetails?> RetrieveStudyForViewerAsync(RemoteStudySearchResult result, IReadOnlyCollection<RemoteSeriesPreview>? seriesPreviews, IProgress<string>? status = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        DicomNetworkSettings settings = _settingsService.CurrentSettings;
        RemoteArchiveEndpoint archive = result.Archive;

        status?.Report($"Starting retrieval from {archive.Name}...");

        IReadOnlyCollection<RemoteSeriesPreview> previews = seriesPreviews ?? [];
        int expectedSeriesCount = previews.Count;
        int loadedSeriesCount = 0;
        if (previews.Count > 0)
        {
            status?.Report($"Fetching representative preview images for {expectedSeriesCount} series...");
            loadedSeriesCount = await FetchRepresentativeImagesAsync(
                result.Archive,
                result.Study.StudyInstanceUid,
                previews,
                settings.LocalAeTitle,
                RepresentativeImageMoveConcurrency,
                cancellationToken);
        }

        StudyDetails? previewReadyStudy = null;
        if (loadedSeriesCount > 0)
        {
            previewReadyStudy = await WaitForLocalStudyAsync(
                result.Study.StudyInstanceUid,
                details => details.Series.Count > 0 && details.Series.Count(series => series.Instances.Count > 0) >= loadedSeriesCount,
                TimeSpan.FromSeconds(12),
                cancellationToken);
        }

        var client = CreateClient(archive);
        status?.Report($"Representative preview complete ({loadedSeriesCount}/{expectedSeriesCount} series). Continuing full background retrieval from {archive.Name}...");
        Task<bool> fullFetchTask = client.MoveStudyAsync(result.Study.StudyInstanceUid, settings.LocalAeTitle, cancellationToken: cancellationToken);

        _ = ObserveFullFetchAsync(fullFetchTask, archive.Name, result.Study.StudyInstanceUid, status);
        return previewReadyStudy ?? await _repository.GetStudyDetailsByStudyInstanceUidAsync(result.Study.StudyInstanceUid, cancellationToken);
    }

    private async Task<int> FetchRepresentativeImagesAsync(RemoteArchiveEndpoint archive, string studyInstanceUid, IReadOnlyCollection<RemoteSeriesPreview> seriesPreviews, string destinationAe, int maxConcurrency, CancellationToken cancellationToken)
    {
        int loadedSeries = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency), Math.Max(1, maxConcurrency));
        List<Task> tasks = [];

        foreach (RemoteSeriesPreview preview in seriesPreviews)
        {
            ImageInfo? representative = preview.GetRepresentativeImage();
            if (representative is null || string.IsNullOrWhiteSpace(preview.LegacySeries.SerInstUid) || string.IsNullOrWhiteSpace(representative.SopInstUid))
            {
                continue;
            }

            await gate.WaitAsync(cancellationToken);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    bool success = await MoveImageAsync(archive, studyInstanceUid, preview.LegacySeries.SerInstUid, representative.SopInstUid, destinationAe, cancellationToken);
                    if (success)
                    {
                        Interlocked.Increment(ref loadedSeries);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);
        return loadedSeries;
    }

    private static async Task<bool> MoveImageAsync(RemoteArchiveEndpoint archive, string studyInstanceUid, string seriesInstanceUid, string sopInstanceUid, string destinationAe, CancellationToken cancellationToken)
    {
        bool success = false;
        DicomCommunicationTrace.Log("DICOM-SCU", $"[{destinationAe.Trim()} -> {archive.RemoteAeTitle.Trim()} {archive.Host}:{archive.Port}] SEND C-MOVE image dest={destinationAe} [study={studyInstanceUid}, series={seriesInstanceUid}, sopInstance={sopInstanceUid}]");

        var client = DicomClientFactory.Create(archive.Host, archive.Port, false, destinationAe, archive.RemoteAeTitle);
        var request = new DicomCMoveRequest(destinationAe, studyInstanceUid, seriesInstanceUid, sopInstanceUid);
        request.OnResponseReceived += (_, response) =>
        {
            DicomCommunicationTrace.Log("DICOM-SCU", $"[{destinationAe.Trim()} -> {archive.RemoteAeTitle.Trim()} {archive.Host}:{archive.Port}] RECV C-MOVE image status={response.Status} [study={studyInstanceUid}, series={seriesInstanceUid}, sopInstance={sopInstanceUid}]");
            if (response.Status == DicomStatus.Success)
            {
                success = true;
            }
        };

        try
        {
            await client.AddRequestAsync(request);
            await client.SendAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            DicomCommunicationTrace.LogException("DICOM-SCU", $"[{destinationAe.Trim()} -> {archive.RemoteAeTitle.Trim()} {archive.Host}:{archive.Port}] C-MOVE image failed", ex);
        }

        return success;
    }

    private async Task ContinuePriorStudyFetchAsync(RemoteStudySearchResult result, StudyDetails previewDetails, Action<StudyDetails> onUpdated, CancellationToken cancellationToken)
    {
        try
        {
            DicomNetworkSettings settings = _settingsService.CurrentSettings;
            DicomNetworkClient client = CreateClient(result.Archive);
            Task<bool> fullFetchTask = client.MoveStudyAsync(result.Study.StudyInstanceUid, settings.LocalAeTitle, cancellationToken: cancellationToken);

            while (!fullFetchTask.IsCompleted)
            {
                await Task.Delay(500, cancellationToken);
                await PublishMergedPreviewUpdateAsync(previewDetails, result.Study.StudyInstanceUid, onUpdated, cancellationToken);
            }

            await fullFetchTask;
            await PublishMergedPreviewUpdateAsync(previewDetails, result.Study.StudyInstanceUid, onUpdated, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private async Task PublishMergedPreviewUpdateAsync(StudyDetails previewDetails, string studyInstanceUid, Action<StudyDetails> onUpdated, CancellationToken cancellationToken)
    {
        StudyDetails? local = await _repository.GetStudyDetailsByStudyInstanceUidAsync(studyInstanceUid, cancellationToken);
        if (local is null)
        {
            return;
        }

        StudyDetails merged = CloneStudyDetails(previewDetails);
        MergeLocalStudyIntoPreview(merged, local);
        onUpdated(merged);
    }

    private static bool IsStudyFullyLocal(StudyDetails details)
    {
        return details.Series.Count > 0
            && details.Series.All(series => series.Instances.Count > 0 && series.Instances.All(instance => !string.IsNullOrWhiteSpace(instance.FilePath) && File.Exists(instance.FilePath)));
    }

    private static void MergeLocalStudyIntoPreview(StudyDetails target, StudyDetails local)
    {
        Dictionary<string, SeriesRecord> localSeriesByUid = local.Series.ToDictionary(series => series.SeriesInstanceUid, StringComparer.Ordinal);

        foreach (SeriesRecord targetSeries in target.Series)
        {
            if (!localSeriesByUid.TryGetValue(targetSeries.SeriesInstanceUid, out SeriesRecord? localSeries))
            {
                continue;
            }

            Dictionary<string, InstanceRecord> localInstancesByUid = localSeries.Instances.ToDictionary(instance => instance.SopInstanceUid, StringComparer.Ordinal);
            foreach (InstanceRecord targetInstance in targetSeries.Instances)
            {
                if (!localInstancesByUid.TryGetValue(targetInstance.SopInstanceUid, out InstanceRecord? localInstance))
                {
                    continue;
                }

                targetInstance.FilePath = localInstance.FilePath;
                targetInstance.InstanceNumber = localInstance.InstanceNumber;
                targetInstance.FrameCount = localInstance.FrameCount;
                if (!string.IsNullOrWhiteSpace(localInstance.SopClassUid))
                {
                    targetInstance.SopClassUid = localInstance.SopClassUid;
                }
            }

            foreach (InstanceRecord localInstance in localSeries.Instances)
            {
                if (targetSeries.Instances.Any(instance => string.Equals(instance.SopInstanceUid, localInstance.SopInstanceUid, StringComparison.Ordinal)))
                {
                    continue;
                }

                targetSeries.Instances.Add(CloneInstance(localInstance));
            }

            targetSeries.InstanceCount = Math.Max(targetSeries.InstanceCount, localSeries.InstanceCount);
            targetSeries.Instances.Sort(static (left, right) =>
            {
                int byNumber = left.InstanceNumber.CompareTo(right.InstanceNumber);
                return byNumber != 0 ? byNumber : string.Compare(left.SopInstanceUid, right.SopInstanceUid, StringComparison.Ordinal);
            });
        }
    }

    private static StudyDetails CloneStudyDetails(StudyDetails source)
    {
        var clone = new StudyDetails
        {
            Study = new StudyListItem
            {
                StudyKey = source.Study.StudyKey,
                StudyInstanceUid = source.Study.StudyInstanceUid,
                PatientName = source.Study.PatientName,
                PatientId = source.Study.PatientId,
                PatientBirthDate = source.Study.PatientBirthDate,
                AccessionNumber = source.Study.AccessionNumber,
                StudyDescription = source.Study.StudyDescription,
                ReferringPhysician = source.Study.ReferringPhysician,
                StudyDate = source.Study.StudyDate,
                Modalities = source.Study.Modalities,
                SeriesCount = source.Study.SeriesCount,
                InstanceCount = source.Study.InstanceCount,
                StoragePath = source.Study.StoragePath,
                ImportedAtUtc = source.Study.ImportedAtUtc,
                IsPreviewOnly = source.Study.IsPreviewOnly,
                PreviewSourcePath = source.Study.PreviewSourcePath,
            },
            LegacyStudy = source.LegacyStudy?.Clone(),
        };

        foreach (SeriesRecord series in source.Series)
        {
            var clonedSeries = new SeriesRecord
            {
                SeriesKey = series.SeriesKey,
                StudyKey = series.StudyKey,
                SeriesInstanceUid = series.SeriesInstanceUid,
                Modality = series.Modality,
                SeriesDescription = series.SeriesDescription,
                SeriesNumber = series.SeriesNumber,
                InstanceCount = series.InstanceCount,
            };

            foreach (InstanceRecord instance in series.Instances)
            {
                clonedSeries.Instances.Add(CloneInstance(instance));
            }

            clone.Series.Add(clonedSeries);
        }

        return clone;
    }

    private static InstanceRecord CloneInstance(InstanceRecord source)
    {
        return new InstanceRecord
        {
            InstanceKey = source.InstanceKey,
            SeriesKey = source.SeriesKey,
            SopInstanceUid = source.SopInstanceUid,
            SopClassUid = source.SopClassUid,
            FilePath = source.FilePath,
            InstanceNumber = source.InstanceNumber,
            FrameCount = source.FrameCount,
        };
    }

    private async Task ObserveFullFetchAsync(Task<bool> fullFetchTask, string archiveName, string studyInstanceUid, IProgress<string>? status)
    {
        try
        {
            bool success = await fullFetchTask;
            status?.Report(success
                ? $"Background retrieval from {archiveName} completed for study {studyInstanceUid}."
                : $"Background retrieval from {archiveName} did not complete successfully.");
        }
        catch (Exception ex)
        {
            status?.Report($"Background retrieval failed: {ex.Message}");
        }
    }

    private async Task<StudyDetails?> WaitForLocalStudyAsync(string studyInstanceUid, Func<StudyDetails, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            StudyDetails? details = await _repository.GetStudyDetailsByStudyInstanceUidAsync(studyInstanceUid, cancellationToken);
            if (details is not null && predicate(details))
            {
                return details;
            }

            await Task.Delay(500, cancellationToken);
        }

        return await _repository.GetStudyDetailsByStudyInstanceUidAsync(studyInstanceUid, cancellationToken);
    }

    private DicomNetworkClient CreateClient(RemoteArchiveEndpoint archive)
    {
        DicomNetworkSettings settings = _settingsService.CurrentSettings;
        return new DicomNetworkClient
        {
            IP = archive.Host,
            Port = archive.Port,
            LocalAET = settings.LocalAeTitle,
            RemoteAET = archive.RemoteAeTitle,
            ServerAlias = archive.Name,
            DefaultCharacterSet = DicomFunctions.ApplicationsDefaultCharSet,
        };
    }

    private static string BuildDicomDateRange(DateOnly? fromDate, DateOnly? toDate)
    {
        string from = fromDate?.ToString("yyyyMMdd") ?? string.Empty;
        string to = toDate?.ToString("yyyyMMdd") ?? string.Empty;

        return (from, to) switch
        {
            ("", "") => string.Empty,
            (_, "") => $"{from}-",
            ("", _) => $"-{to}",
            _ => $"{from}-{to}",
        };
    }

    private static int TryParseInt(string? value, int fallback = 0)
    {
        return int.TryParse(value, out int parsed) ? parsed : fallback;
    }
}