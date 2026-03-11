using System.Globalization;
using System.Threading.Channels;
using FellowOakDicom;
using FellowOakDicom.Media;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class DicomImportService
{
    private readonly ImageboxPaths _paths;
    private readonly ImageboxRepository _repository;
    private readonly BackgroundJobService _backgroundJobs;
    private readonly Channel<StudyImportWorkItem> _studyImportQueue = Channel.CreateUnbounded<StudyImportWorkItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly HashSet<string> _queuedStudyInstanceUids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _importJobIdsByStudyInstanceUid = new(StringComparer.Ordinal);
    private readonly Lock _queueLock = new();
    private readonly Task _studyImportWorker;

    public DicomImportService(ImageboxPaths paths, ImageboxRepository repository, BackgroundJobService backgroundJobs)
    {
        _paths = paths;
        _repository = repository;
        _backgroundJobs = backgroundJobs;
        _studyImportWorker = Task.Run(ProcessStudyImportQueueAsync);
    }

    public async Task<ImportResult> ImportPathAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = new ImportResult();
        if (string.IsNullOrWhiteSpace(path))
        {
            result.Messages.Add("No import path selected.");
            return result;
        }

        if (Directory.Exists(path))
        {
            string dicomDirPath = Path.Combine(path, "DICOMDIR");
            if (File.Exists(dicomDirPath))
            {
                await ImportDicomDirectoryAsync(dicomDirPath, result, cancellationToken);
            }
            else
            {
                await ImportFilesAsync(EnumerateCandidateFiles(path), result, cancellationToken);
            }

            return result;
        }

        if (File.Exists(path) && string.Equals(Path.GetFileName(path), "DICOMDIR", StringComparison.OrdinalIgnoreCase))
        {
            await ImportDicomDirectoryAsync(path, result, cancellationToken);
            return result;
        }

        if (File.Exists(path))
        {
            await ImportFilesAsync([path], result, cancellationToken);
            return result;
        }

        result.Messages.Add($"Import path not found: {path}");
        return result;
    }

    public async Task<ImportResult> ImportStudyAsync(StudyDetails studyDetails, CancellationToken cancellationToken = default)
    {
        var result = new ImportResult();
        ArgumentNullException.ThrowIfNull(studyDetails);

        IEnumerable<string> files = studyDetails.Series
            .SelectMany(series => series.Instances)
            .Select(instance => ResolveSourceFilePath(instance))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        await ImportFilesAsync(files!, result, cancellationToken);
        result.Messages.Add($"Imported study {studyDetails.Study.PatientName} ({studyDetails.Study.DisplayStudyDate}).");
        return result;
    }

    public async Task<List<StudyDetails>> IndexFilesystemStudiesAsync(IEnumerable<StudyDetails> studies, string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(studies);

        var indexedStudies = new List<StudyDetails>();
        foreach (StudyDetails study in studies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await IndexStudyMetadataAsync(study, sourcePath, cancellationToken);
            indexedStudies.Add(study);
        }

        return indexedStudies;
    }

    public async Task<bool> QueueStudyImportAsync(StudyDetails studyDetails, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(studyDetails);

        string sourcePath = ResolveStudySourcePath(studyDetails);
        await IndexStudyMetadataAsync(studyDetails, sourcePath, cancellationToken);

        if (studyDetails.Study.Availability == StudyAvailability.Imported)
        {
            return false;
        }

        lock (_queueLock)
        {
            if (!_queuedStudyInstanceUids.Add(studyDetails.Study.StudyInstanceUid))
            {
                return false;
            }
        }

        BackgroundJobInfo job = _backgroundJobs.CreateJob(
            BackgroundJobType.Import,
            $"import:{studyDetails.Study.StudyInstanceUid}",
            $"Import {studyDetails.Study.PatientName}".Trim(),
            $"Queued import for study {studyDetails.Study.PatientName}.");
        _backgroundJobs.AppendLog(job.JobId, $"Source path: {sourcePath}");
        _backgroundJobs.AppendLog(job.JobId, $"Instances scheduled: {studyDetails.Series.Sum(series => series.Instances.Count)}");

        studyDetails.Study.Availability = StudyAvailability.ImportQueued;
        studyDetails.Study.IsPreviewOnly = true;
        await _repository.UpdateStudyImportStateAsync(studyDetails.Study.StudyKey, StudyAvailability.ImportQueued, studyDetails.Study.StoragePath, cancellationToken);
        lock (_queueLock)
        {
            _importJobIdsByStudyInstanceUid[studyDetails.Study.StudyInstanceUid] = job.JobId;
        }

        await _studyImportQueue.Writer.WriteAsync(new StudyImportWorkItem(studyDetails, job.JobId), cancellationToken);
        return true;
    }

    private async Task ImportDicomDirectoryAsync(string dicomDirPath, ImportResult result, CancellationToken cancellationToken)
    {
        string baseDirectory = Path.GetDirectoryName(dicomDirPath) ?? string.Empty;
        try
        {
            var directory = DicomDirectory.Open(dicomDirPath);
            var referencedFiles = new List<string>();
            TraverseDirectoryRecords(directory.RootDirectoryRecord, baseDirectory, referencedFiles);

            if (referencedFiles.Count == 0)
            {
                result.Messages.Add("DICOMDIR did not contain file references. Falling back to recursive scan.");
                await ImportFilesAsync(EnumerateCandidateFiles(baseDirectory), result, cancellationToken);
                return;
            }

            await ImportFilesAsync(referencedFiles.Distinct(StringComparer.OrdinalIgnoreCase), result, cancellationToken);
            result.Messages.Add($"Imported from DICOMDIR: {Path.GetFileName(dicomDirPath)}");
        }
        catch (Exception ex)
        {
            result.Messages.Add($"DICOMDIR import fallback: {ex.Message}");
            await ImportFilesAsync(EnumerateCandidateFiles(baseDirectory), result, cancellationToken);
        }
    }

    private static void TraverseDirectoryRecords(DicomDirectoryRecord? record, string baseDirectory, ICollection<string> files)
    {
        while (record is not null)
        {
            if (string.Equals(record.DirectoryRecordType, "IMAGE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.DirectoryRecordType, "MRDR", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.DirectoryRecordType, "PRIVATE", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var parts = record.GetValues<string>(DicomTag.ReferencedFileID);
                    if (parts is { Length: > 0 })
                    {
                        string path = Path.Combine([baseDirectory, .. parts]);
                        files.Add(path);
                    }
                }
                catch
                {
                }
            }

            if (record.LowerLevelDirectoryRecord is not null)
            {
                TraverseDirectoryRecords(record.LowerLevelDirectoryRecord, baseDirectory, files);
            }

            record = record.NextDirectoryRecord;
        }
    }

    private async Task ImportFilesAsync(IEnumerable<string> files, ImportResult result, CancellationToken cancellationToken)
    {
        var studyCache = new Dictionary<string, long>(StringComparer.Ordinal);
        var seriesCache = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (string filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(filePath) || string.Equals(Path.GetFileName(filePath), "DICOMDIR", StringComparison.OrdinalIgnoreCase))
            {
                result.SkippedFiles++;
                continue;
            }

            try
            {
                var dicomFile = DicomFile.Open(filePath, FileReadOption.ReadAll);
                var dataset = dicomFile.Dataset;
                if (!dataset.Contains(DicomTag.SOPInstanceUID) || !dataset.Contains(DicomTag.StudyInstanceUID) || !dataset.Contains(DicomTag.SeriesInstanceUID))
                {
                    result.SkippedFiles++;
                    continue;
                }

                string studyUid = dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID);
                string seriesUid = dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID);
                string sopUid = dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID);
                string sopClassUid = dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);
                string modality = LegacyStudyInfoMapper.ResolveModality(dataset.GetSingleValueOrDefault(DicomTag.Modality, string.Empty), sopClassUid);
                string studyStoragePath = GetStudyStoragePath(studyUid);
                string targetDirectory = Path.Combine(studyStoragePath, Sanitize(seriesUid));
                Directory.CreateDirectory(targetDirectory);
                string extension = Path.GetExtension(filePath);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".dcm";
                }

                string destinationPath = Path.Combine(targetDirectory, Sanitize(sopUid) + extension);
                if (!Path.GetFullPath(filePath).Equals(Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(filePath, destinationPath, true);
                }

                if (!studyCache.TryGetValue(studyUid, out long studyKey))
                {
                    var study = new StudyListItem
                    {
                        StudyInstanceUid = studyUid,
                        PatientName = dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty),
                        PatientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty),
                        PatientBirthDate = dataset.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty),
                        AccessionNumber = dataset.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty),
                        StudyDescription = dataset.GetSingleValueOrDefault(DicomTag.StudyDescription, string.Empty),
                        ReferringPhysician = dataset.GetSingleValueOrDefault(DicomTag.ReferringPhysicianName, string.Empty),
                        StudyDate = dataset.GetSingleValueOrDefault(DicomTag.StudyDate, string.Empty),
                        Modalities = modality,
                        StoragePath = studyStoragePath,
                        SourcePath = Path.GetDirectoryName(filePath) ?? string.Empty,
                        Availability = StudyAvailability.Imported,
                        ImportedAtUtc = DateTime.UtcNow,
                    };
                    studyKey = await _repository.UpsertStudyAsync(study, cancellationToken);
                    study.StudyKey = studyKey;
                    study.IsPreviewOnly = false;
                    studyCache[studyUid] = studyKey;
                    result.ImportedStudies++;
                }

                if (!seriesCache.TryGetValue(seriesUid, out long seriesKey))
                {
                    var series = new SeriesRecord
                    {
                        StudyKey = studyKey,
                        SeriesInstanceUid = seriesUid,
                        Modality = modality,
                        SeriesDescription = dataset.GetSingleValueOrDefault(DicomTag.SeriesDescription, string.Empty),
                        SeriesNumber = dataset.GetSingleValueOrDefault(DicomTag.SeriesNumber, 0),
                        InstanceCount = 0,
                    };
                    seriesKey = await _repository.UpsertSeriesAsync(studyKey, series, cancellationToken);
                    seriesCache[seriesUid] = seriesKey;
                    result.ImportedSeries++;
                }

                var instance = new InstanceRecord
                {
                    SeriesKey = seriesKey,
                    SopInstanceUid = sopUid,
                    SopClassUid = sopClassUid,
                    FilePath = destinationPath,
                    SourceFilePath = filePath,
                    InstanceNumber = dataset.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0),
                    FrameCount = dataset.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1),
                };
                await _repository.UpsertInstanceAsync(seriesKey, instance, cancellationToken);
                result.ImportedInstances++;
            }
            catch
            {
                result.SkippedFiles++;
            }
        }

        result.Messages.Add($"Imported {result.ImportedStudies} studies, {result.ImportedSeries} series, {result.ImportedInstances} instances.");
    }

    private async Task IndexStudyMetadataAsync(StudyDetails studyDetails, string sourcePath, CancellationToken cancellationToken)
    {
        string studyUid = studyDetails.Study.StudyInstanceUid;
        string targetStudyPath = GetStudyStoragePath(studyUid);

        foreach (SeriesRecord series in studyDetails.Series)
        {
            series.InstanceCount = Math.Max(series.InstanceCount, series.Instances.Count);
            foreach (InstanceRecord instance in series.Instances)
            {
                if (string.IsNullOrWhiteSpace(instance.SourceFilePath))
                {
                    instance.SourceFilePath = instance.FilePath;
                }
            }
        }

        StudyDetails? existing = await _repository.GetStudyDetailsByStudyInstanceUidAsync(studyUid, cancellationToken);
        if (existing is not null)
        {
            MergeExistingStudyState(studyDetails, existing);
        }

        studyDetails.Study.StoragePath = targetStudyPath;
        studyDetails.Study.SourcePath = sourcePath;
        studyDetails.Study.PreviewSourcePath = sourcePath;
        studyDetails.Study.ImportedAtUtc = existing?.Study.ImportedAtUtc ?? (studyDetails.Study.ImportedAtUtc == default ? DateTime.UtcNow : studyDetails.Study.ImportedAtUtc);
        studyDetails.Study.Availability = DetermineStudyAvailability(studyDetails, existing?.Study.Availability);
        studyDetails.Study.IsPreviewOnly = studyDetails.Study.Availability != StudyAvailability.Imported;

        long studyKey = await _repository.UpsertStudyAsync(studyDetails.Study, cancellationToken);
        studyDetails.Study.StudyKey = studyKey;

        foreach (SeriesRecord series in studyDetails.Series)
        {
            series.StudyKey = studyKey;
            long seriesKey = await _repository.UpsertSeriesAsync(studyKey, series, cancellationToken);
            series.SeriesKey = seriesKey;

            foreach (InstanceRecord instance in series.Instances)
            {
                instance.SeriesKey = seriesKey;
                await _repository.UpsertInstanceAsync(seriesKey, instance, cancellationToken);
            }
        }

        studyDetails.Study.SeriesCount = studyDetails.Series.Count;
        studyDetails.Study.InstanceCount = studyDetails.Series.Sum(series => Math.Max(series.InstanceCount, series.Instances.Count));
        studyDetails.PopulateLegacyStudyInfo();
    }

    private async Task ProcessStudyImportQueueAsync()
    {
        await foreach (StudyImportWorkItem workItem in _studyImportQueue.Reader.ReadAllAsync())
        {
            try
            {
                await ImportIndexedStudyAsync(workItem.StudyDetails, workItem.JobId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _backgroundJobs.MarkFailed(workItem.JobId, $"Import failed for {workItem.StudyDetails.Study.PatientName}: {ex.Message}");
            }
            finally
            {
                lock (_queueLock)
                {
                    _queuedStudyInstanceUids.Remove(workItem.StudyDetails.Study.StudyInstanceUid);
                    _importJobIdsByStudyInstanceUid.Remove(workItem.StudyDetails.Study.StudyInstanceUid);
                }
            }
        }
    }

    private async Task ImportIndexedStudyAsync(StudyDetails studyDetails, Guid jobId, CancellationToken cancellationToken)
    {
        if (studyDetails.Study.StudyKey <= 0)
        {
            await IndexStudyMetadataAsync(studyDetails, ResolveStudySourcePath(studyDetails), cancellationToken);
        }

        int totalInstances = studyDetails.Series.Sum(series => series.Instances.Count);
        int completedInstances = 0;
        _backgroundJobs.MarkRunning(jobId, $"Copying study {studyDetails.Study.PatientName} into the local imagebox...", totalInstances);

        studyDetails.Study.Availability = StudyAvailability.Importing;
        studyDetails.Study.IsPreviewOnly = true;
        await _repository.UpdateStudyImportStateAsync(studyDetails.Study.StudyKey, StudyAvailability.Importing, studyDetails.Study.StoragePath, cancellationToken);

        bool hadFailures = false;
        foreach (SeriesRecord series in studyDetails.Series)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string seriesDirectory = Path.Combine(studyDetails.Study.StoragePath, Sanitize(series.SeriesInstanceUid));
            Directory.CreateDirectory(seriesDirectory);

            foreach (InstanceRecord instance in series.Instances)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string sourceFilePath = ResolveSourceFilePath(instance);
                if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                {
                    hadFailures = true;
                    _backgroundJobs.AppendLog(jobId, $"Missing source file: {sourceFilePath}");
                    continue;
                }

                string extension = Path.GetExtension(sourceFilePath);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".dcm";
                }

                string destinationPath = Path.Combine(seriesDirectory, Sanitize(instance.SopInstanceUid) + extension);
                if (!PathsEqual(sourceFilePath, destinationPath))
                {
                    File.Copy(sourceFilePath, destinationPath, true);
                }

                instance.FilePath = destinationPath;
                instance.SourceFilePath = sourceFilePath;
                await _repository.UpsertInstanceAsync(series.SeriesKey, instance, cancellationToken);
                completedInstances++;
                if (completedInstances == 1 || completedInstances == totalInstances || completedInstances % 25 == 0)
                {
                    _backgroundJobs.ReportProgress(
                        jobId,
                        $"Importing {studyDetails.Study.PatientName}: {completedInstances}/{totalInstances} instances copied.",
                        completedInstances,
                        totalInstances);
                }
            }
        }

        StudyAvailability finalAvailability = hadFailures || studyDetails.Series.SelectMany(series => series.Instances).Any(instance => !IsLocalStudyFile(instance.FilePath))
            ? StudyAvailability.ImportFailed
            : StudyAvailability.Imported;

        studyDetails.Study.Availability = finalAvailability;
        studyDetails.Study.IsPreviewOnly = finalAvailability != StudyAvailability.Imported;
        await _repository.UpdateStudyImportStateAsync(studyDetails.Study.StudyKey, finalAvailability, studyDetails.Study.StoragePath, cancellationToken);
        studyDetails.PopulateLegacyStudyInfo();

        if (finalAvailability == StudyAvailability.Imported)
        {
            _backgroundJobs.MarkCompleted(jobId, $"Import completed for {studyDetails.Study.PatientName}. {completedInstances}/{totalInstances} instances are local.");
        }
        else
        {
            _backgroundJobs.MarkFailed(jobId, $"Import finished with missing files for {studyDetails.Study.PatientName}. {completedInstances}/{totalInstances} instances copied.");
        }
    }

    private void MergeExistingStudyState(StudyDetails targetStudy, StudyDetails existingStudy)
    {
        Dictionary<string, SeriesRecord> existingSeriesByUid = existingStudy.Series.ToDictionary(series => series.SeriesInstanceUid, StringComparer.Ordinal);
        foreach (SeriesRecord targetSeries in targetStudy.Series)
        {
            if (!existingSeriesByUid.TryGetValue(targetSeries.SeriesInstanceUid, out SeriesRecord? existingSeries))
            {
                continue;
            }

            targetSeries.SeriesKey = existingSeries.SeriesKey;
            targetSeries.StudyKey = existingSeries.StudyKey;
            targetSeries.InstanceCount = Math.Max(targetSeries.InstanceCount, existingSeries.InstanceCount);
            Dictionary<string, InstanceRecord> existingInstancesByUid = existingSeries.Instances.ToDictionary(instance => instance.SopInstanceUid, StringComparer.Ordinal);
            foreach (InstanceRecord targetInstance in targetSeries.Instances)
            {
                if (!existingInstancesByUid.TryGetValue(targetInstance.SopInstanceUid, out InstanceRecord? existingInstance))
                {
                    continue;
                }

                targetInstance.InstanceKey = existingInstance.InstanceKey;
                targetInstance.SeriesKey = existingInstance.SeriesKey;
                if (string.IsNullOrWhiteSpace(targetInstance.SourceFilePath))
                {
                    targetInstance.SourceFilePath = targetInstance.FilePath;
                }

                if (IsLocalPath(existingInstance.FilePath))
                {
                    targetInstance.FilePath = existingInstance.FilePath;
                }
            }
        }

        targetStudy.Study.StudyKey = existingStudy.Study.StudyKey;
    }

    private StudyAvailability DetermineStudyAvailability(StudyDetails studyDetails, StudyAvailability? existingAvailability)
    {
        if (studyDetails.Series.SelectMany(series => series.Instances).All(instance => IsLocalStudyFile(instance.FilePath)))
        {
            return StudyAvailability.Imported;
        }

        return existingAvailability switch
        {
            StudyAvailability.ImportQueued => StudyAvailability.ImportQueued,
            StudyAvailability.Importing => StudyAvailability.Importing,
            StudyAvailability.ImportFailed => StudyAvailability.ImportFailed,
            _ => StudyAvailability.IndexedExternal,
        };
    }

    private string ResolveStudySourcePath(StudyDetails studyDetails)
    {
        if (!string.IsNullOrWhiteSpace(studyDetails.Study.SourcePath))
        {
            return studyDetails.Study.SourcePath;
        }

        if (!string.IsNullOrWhiteSpace(studyDetails.Study.PreviewSourcePath))
        {
            return studyDetails.Study.PreviewSourcePath;
        }

        string? firstSource = studyDetails.Series
            .SelectMany(series => series.Instances)
            .Select(instance => ResolveSourceFilePath(instance))
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

        if (string.IsNullOrWhiteSpace(firstSource))
        {
            return string.Empty;
        }

        return Directory.Exists(firstSource)
            ? firstSource
            : Path.GetDirectoryName(firstSource) ?? string.Empty;
    }

    private static string ResolveSourceFilePath(InstanceRecord instance)
    {
        if (!string.IsNullOrWhiteSpace(instance.SourceFilePath))
        {
            return instance.SourceFilePath;
        }

        return instance.FilePath;
    }

    private string GetStudyStoragePath(string studyUid) => Path.Combine(_paths.StudiesDirectory, Sanitize(studyUid));

    private bool IsLocalStudyFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        return IsLocalPath(filePath);
    }

    private bool IsLocalPath(string filePath)
    {
        string normalizedStudiesRoot = EnsureTrailingSeparator(Path.GetFullPath(_paths.StudiesDirectory));
        string normalizedFilePath = Path.GetFullPath(filePath);
        return normalizedFilePath.StartsWith(normalizedStudiesRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static IEnumerable<string> EnumerateCandidateFiles(string root)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path =>
                !string.Equals(Path.GetFileName(path), "DICOMDIR", StringComparison.OrdinalIgnoreCase)
                && !path.Contains(Path.DirectorySeparatorChar + ".", StringComparison.Ordinal));
    }

    private static string Sanitize(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        var buffer = value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
        return new string(buffer);
    }

    private sealed record StudyImportWorkItem(StudyDetails StudyDetails, Guid JobId);
}
