using System.Globalization;
using FellowOakDicom;
using FellowOakDicom.Media;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class DicomImportService
{
    private readonly ImageboxPaths _paths;
    private readonly ImageboxRepository _repository;

    public DicomImportService(ImageboxPaths paths, ImageboxRepository repository)
    {
        _paths = paths;
        _repository = repository;
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
            .Select(instance => instance.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        await ImportFilesAsync(files, result, cancellationToken);
        result.Messages.Add($"Imported study {studyDetails.Study.PatientName} ({studyDetails.Study.DisplayStudyDate}).");
        return result;
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
                string targetDirectory = Path.Combine(_paths.StudiesDirectory, Sanitize(studyUid), Sanitize(seriesUid));
                Directory.CreateDirectory(targetDirectory);
                string extension = Path.GetExtension(filePath);
                if (string.IsNullOrWhiteSpace(extension)) extension = ".dcm";
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
                        StoragePath = Path.Combine(_paths.StudiesDirectory, Sanitize(studyUid)),
                        ImportedAtUtc = DateTime.UtcNow,
                    };
                    studyKey = await _repository.UpsertStudyAsync(study, cancellationToken);
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
}
