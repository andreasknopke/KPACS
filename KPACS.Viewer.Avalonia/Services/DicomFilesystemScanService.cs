using FellowOakDicom;
using FellowOakDicom.Media;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class DicomFilesystemScanService
{
    private const FileReadOption ScanReadOption = FileReadOption.SkipLargeTags;

    public async Task<FilesystemScanResult> ScanPathAsync(string path, bool preferDicomDir, IProgress<FilesystemScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = new FilesystemScanResult
        {
            SourcePath = path,
        };

        if (string.IsNullOrWhiteSpace(path))
        {
            result.Messages.Add("No scan path selected.");
            return result;
        }

        if (Directory.Exists(path))
        {
            string dicomDirPath = Path.Combine(path, "DICOMDIR");
            if (preferDicomDir && File.Exists(dicomDirPath))
            {
                return await ScanDicomDirectoryAsync(dicomDirPath, progress, cancellationToken);
            }

            return await ScanFilesAsync(path, EnumerateCandidateFiles(path), false, null, progress, cancellationToken);
        }

        if (File.Exists(path) && string.Equals(Path.GetFileName(path), "DICOMDIR", StringComparison.OrdinalIgnoreCase))
        {
            return await ScanDicomDirectoryAsync(path, progress, cancellationToken);
        }

        if (File.Exists(path))
        {
            return await ScanFilesAsync(path, [path], false, null, progress, cancellationToken);
        }

        result.Messages.Add($"Scan path not found: {path}");
        return result;
    }

    public static bool ContainsDicomDir(string folderPath)
    {
        return Directory.Exists(folderPath) && File.Exists(Path.Combine(folderPath, "DICOMDIR"));
    }

    private async Task<FilesystemScanResult> ScanDicomDirectoryAsync(string dicomDirPath, IProgress<FilesystemScanProgress>? progress, CancellationToken cancellationToken)
    {
        string baseDirectory = Path.GetDirectoryName(dicomDirPath) ?? string.Empty;

        try
        {
            var directory = DicomDirectory.Open(dicomDirPath);
            var referencedFiles = new List<string>();
            TraverseDirectoryRecords(directory.RootDirectoryRecord, baseDirectory, referencedFiles);

            if (referencedFiles.Count == 0)
            {
                var fallback = await ScanFilesAsync(baseDirectory, EnumerateCandidateFiles(baseDirectory), false, dicomDirPath, progress, cancellationToken);
                fallback.Messages.Add("DICOMDIR had no usable file references. Recursive scan was used instead.");
                return fallback;
            }

            return await ScanFilesAsync(baseDirectory, referencedFiles.Distinct(StringComparer.OrdinalIgnoreCase), true, dicomDirPath, progress, cancellationToken);
        }
        catch (Exception ex)
        {
            var fallback = await ScanFilesAsync(baseDirectory, EnumerateCandidateFiles(baseDirectory), false, dicomDirPath, progress, cancellationToken);
            fallback.Messages.Add($"DICOMDIR scan fallback: {ex.Message}");
            return fallback;
        }
    }

    private static async Task<FilesystemScanResult> ScanFilesAsync(
        string sourcePath,
        IEnumerable<string> files,
        bool usedDicomDir,
        string? dicomDirPath,
        IProgress<FilesystemScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var result = new FilesystemScanResult
            {
                SourcePath = sourcePath,
                UsedDicomDir = usedDicomDir,
                DicomDirPath = dicomDirPath,
            };

            var studyLookup = new Dictionary<string, StudyDetails>(StringComparer.Ordinal);
            var seriesLookup = new Dictionary<string, SeriesRecord>(StringComparer.Ordinal);
            var publishedStudyUids = new HashSet<string>(StringComparer.Ordinal);
            int lastReportedCount = 0;
            string? previousStudyUid = null;

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
                    var dicomFile = DicomFile.Open(filePath, ScanReadOption);
                    var dataset = dicomFile.Dataset;
                    if (!dataset.Contains(DicomTag.SOPInstanceUID) || !dataset.Contains(DicomTag.StudyInstanceUID) || !dataset.Contains(DicomTag.SeriesInstanceUID))
                    {
                        result.SkippedFiles++;
                        continue;
                    }

                    result.ScannedFiles++;

                    string studyUid = dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID);
                    string seriesUid = dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID);
                    string sopUid = dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID);
                    string sopClassUid = dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);
                    string modality = LegacyStudyInfoMapper.ResolveModality(dataset.GetSingleValueOrDefault(DicomTag.Modality, string.Empty), sopClassUid);

                    if (!string.IsNullOrWhiteSpace(previousStudyUid)
                        && !string.Equals(previousStudyUid, studyUid, StringComparison.Ordinal))
                    {
                        PublishStudy(previousStudyUid, studyLookup, publishedStudyUids, result, progress, filePath);
                    }

                    if (!studyLookup.TryGetValue(studyUid, out StudyDetails? details))
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
                            StoragePath = sourcePath,
                            ImportedAtUtc = DateTime.UtcNow,
                            IsPreviewOnly = true,
                            PreviewSourcePath = sourcePath,
                        };

                        details = new StudyDetails
                        {
                            Study = study,
                        };

                        studyLookup.Add(studyUid, details);
                    }

                    if (!seriesLookup.TryGetValue(seriesUid, out SeriesRecord? series))
                    {
                        series = new SeriesRecord
                        {
                            StudyKey = details.Study.StudyKey,
                            SeriesInstanceUid = seriesUid,
                            Modality = modality,
                            SeriesDescription = dataset.GetSingleValueOrDefault(DicomTag.SeriesDescription, string.Empty),
                            SeriesNumber = dataset.GetSingleValueOrDefault(DicomTag.SeriesNumber, 0),
                        };

                        seriesLookup.Add(seriesUid, series);
                        details.Series.Add(series);
                    }

                    series.Instances.Add(new InstanceRecord
                    {
                        SopInstanceUid = sopUid,
                        SopClassUid = sopClassUid,
                        FilePath = filePath,
                        InstanceNumber = dataset.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0),
                        FrameCount = dataset.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1),
                    });

                    if (details.Series.Count > 0)
                    {
                        details.Study.SeriesCount = details.Series.Count;
                        details.Study.InstanceCount = details.Series.Sum(candidateSeries => candidateSeries.Instances.Count);
                        details.Study.Modalities = string.Join(", ",
                            details.Series.Select(candidateSeries => candidateSeries.Modality)
                                .Where(candidateModality => !string.IsNullOrWhiteSpace(candidateModality))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(candidateModality => candidateModality));
                    }

                    previousStudyUid = studyUid;

                    int processedCount = result.ScannedFiles + result.SkippedFiles;
                    if (processedCount - lastReportedCount >= 25)
                    {
                        lastReportedCount = processedCount;
                        ReportProgress(progress, result, filePath, details, $"Scanned {result.ScannedFiles} files, skipped {result.SkippedFiles}.");
                    }
                }
                catch
                {
                    result.SkippedFiles++;
                    int processedCount = result.ScannedFiles + result.SkippedFiles;
                    if (processedCount - lastReportedCount >= 25)
                    {
                        lastReportedCount = processedCount;
                        ReportProgress(progress, result, filePath, null, $"Scanned {result.ScannedFiles} files, skipped {result.SkippedFiles}.");
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(previousStudyUid))
            {
                PublishStudy(previousStudyUid, studyLookup, publishedStudyUids, result, progress, sourcePath);
            }

            foreach (string studyUid in studyLookup.Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                PublishStudy(studyUid, studyLookup, publishedStudyUids, result, progress, sourcePath);
            }

            foreach (StudyDetails details in result.Studies)
            {
                details.Study.SeriesCount = details.Series.Count;
                details.Study.InstanceCount = details.Series.Sum(series => series.Instances.Count);
                details.Study.Modalities = string.Join(", ",
                    details.Series.Select(series => series.Modality)
                        .Where(modality => !string.IsNullOrWhiteSpace(modality))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(modality => modality));

                foreach (SeriesRecord series in details.Series)
                {
                    series.Instances.Sort((left, right) =>
                    {
                        int instanceCompare = left.InstanceNumber.CompareTo(right.InstanceNumber);
                        return instanceCompare != 0 ? instanceCompare : string.Compare(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase);
                    });
                    series.InstanceCount = series.Instances.Count;
                }

                details.PopulateLegacyStudyInfo();
            }

            result.Studies.Sort((left, right) =>
            {
                int dateCompare = string.Compare(right.Study.StudyDate, left.Study.StudyDate, StringComparison.OrdinalIgnoreCase);
                return dateCompare != 0 ? dateCompare : string.Compare(left.Study.PatientName, right.Study.PatientName, StringComparison.OrdinalIgnoreCase);
            });

            result.Messages.Add($"Scanned {result.ScannedFiles} DICOM files and found {result.Studies.Count} studies.");
            ReportProgress(progress, result, sourcePath, null, result.Messages[^1]);
            return result;
        }, cancellationToken);
    }

    private static void ReportProgress(IProgress<FilesystemScanProgress>? progress, FilesystemScanResult result, string currentPath, StudyDetails? updatedStudy, string message)
    {
        progress?.Report(new FilesystemScanProgress
        {
            ScannedFiles = result.ScannedFiles,
            SkippedFiles = result.SkippedFiles,
            StudyCount = result.Studies.Count,
            CurrentPath = currentPath,
            Message = message,
            UpdatedStudy = updatedStudy,
        });
    }

    private static void PublishStudy(
        string studyUid,
        IReadOnlyDictionary<string, StudyDetails> studyLookup,
        ISet<string> publishedStudyUids,
        FilesystemScanResult result,
        IProgress<FilesystemScanProgress>? progress,
        string currentPath)
    {
        if (string.IsNullOrWhiteSpace(studyUid)
            || publishedStudyUids.Contains(studyUid)
            || !studyLookup.TryGetValue(studyUid, out StudyDetails? details))
        {
            return;
        }

        result.Studies.Add(details);
        publishedStudyUids.Add(studyUid);
        ReportProgress(progress, result, currentPath, details, $"{result.Studies.Count} studies are ready for preview.");
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
                        files.Add(Path.Combine([baseDirectory, .. parts]));
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

    private static IEnumerable<string> EnumerateCandidateFiles(string root)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path =>
                !string.Equals(Path.GetFileName(path), "DICOMDIR", StringComparison.OrdinalIgnoreCase)
                && !path.Contains(Path.DirectorySeparatorChar + ".", StringComparison.Ordinal))
            .OrderByDescending(GetPathDepth)
            .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static int GetPathDepth(string path) =>
        path.Count(ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar);
}
