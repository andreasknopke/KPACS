using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FellowOakDicom;
using KPACS.DCMClasses;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class ResearchSrExportService
{
    private const string PrivateCreator = "KPACS_RESEARCH";
    private const ushort PayloadJsonTag = 0x1001;
    private const ushort PayloadGzipTag = 0x1002;
    private const string SeriesDescription = "KPACS Research SR";
    private static readonly DicomTag CurrentRequestedProcedureEvidenceSequenceTag = new(0x0040, 0xA375);
    private static readonly DicomTag ReferencedSeriesSequenceTag = new(0x0008, 0x1115);
    private static readonly DicomTag ReferencedSopSequenceTag = new(0x0008, 0x1199);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public string ExportMeasurementReport(
        StudyDetails studyDetails,
        IEnumerable<StudyMeasurement> measurements,
        IEnumerable<SliceRadiomicsResult>? radiomicsResults,
        IEnumerable<RegistrationSceneState>? sceneStates,
        string outputDirectory,
        string reportTitle = "Research Measurement Report")
    {
        ArgumentNullException.ThrowIfNull(studyDetails);
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(outputDirectory);

        List<StudyMeasurement> measurementList = measurements.ToList();
        List<SliceRadiomicsResult> radiomicsList = radiomicsResults?.ToList() ?? [];
        List<RegistrationSceneState> sceneStateList = sceneStates?.ToList() ?? [];
        Directory.CreateDirectory(outputDirectory);

        string sopInstanceUid = DicomFunctions.CreateUniqueUid();
        string filePath = Path.Combine(outputDirectory, $"research-sr-{SanitizeFileComponent(sopInstanceUid)}.dcm");

        using var structuredReport = new DicomStructuredReport();
        structuredReport.InitializeDataset();
        PopulateStudyContext(structuredReport, studyDetails);
        structuredReport.Dataset.AddOrUpdate(DicomTag.SOPClassUID, DicomTagConstants.UID_ComprehensiveSR);
        structuredReport.Dataset.AddOrUpdate(DicomTag.SOPInstanceUID, sopInstanceUid);
        structuredReport.Dataset.AddOrUpdate(DicomTag.InstanceNumber, "1");
        structuredReport.Dataset.AddOrUpdate(DicomTag.ContentLabel, "KPACS_RESEARCH");
        structuredReport.SetSeriesNumber(ComputeSeriesNumber(studyDetails));
        structuredReport.CompletionFlag = CompletionFlag.Complete;
        structuredReport.VerificationFlag = VerificationFlag.Unverified;

        structuredReport.AddContent("Report", "1111", reportTitle, ContentValueType.Text, RelationshipType.Contains);
        structuredReport.AddContent("Summary", "KPACS_SUMMARY",
            BuildSummaryText(studyDetails, measurementList, radiomicsList, sceneStateList), ContentValueType.Text, RelationshipType.Contains);

        foreach (StudyMeasurement measurement in measurementList)
        {
            structuredReport.AddContent(
                "Finding",
                "KPACS_FINDING",
                BuildMeasurementText(measurement, radiomicsList.FirstOrDefault(item => item.MeasurementId == measurement.Id)),
                ContentValueType.Text,
                RelationshipType.Contains);
        }

            foreach (RegistrationSceneState sceneState in sceneStateList)
            {
                structuredReport.AddContent(
                "Scene",
                "KPACS_SCENE",
                BuildSceneText(sceneState),
                ContentValueType.Text,
                RelationshipType.Contains);
            }

        foreach (InstanceReference reference in ResolveReferencedInstances(studyDetails, measurementList))
        {
            AddImageReference(structuredReport.Dataset, reference.SeriesInstanceUid, reference.SopClassUid, reference.SopInstanceUid);
        }

        ResearchSrPayload payload = BuildPayload(studyDetails, measurementList, radiomicsList, sceneStateList, sopInstanceUid, reportTitle);
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        byte[] gzipBytes = Compress(jsonBytes);
        AddPrivateTextPayload(structuredReport.Dataset, PayloadJsonTag, json);
        AddPrivateBinaryPayload(structuredReport.Dataset, PayloadGzipTag, gzipBytes);
        structuredReport.SaveFile(filePath);
        return filePath;
    }

    private static void PopulateStudyContext(DicomStructuredReport structuredReport, StudyDetails studyDetails)
    {
        StudyListItem study = studyDetails.Study;
        var dataset = structuredReport.Dataset;
        dataset.AddOrUpdate(DicomTag.PatientName, DicomFunctions.PersonNameVTCompatible(study.PatientName));
        dataset.AddOrUpdate(DicomTag.PatientID, study.PatientId);
        dataset.AddOrUpdate(DicomTag.PatientBirthDate, study.PatientBirthDate);
        dataset.AddOrUpdate(DicomTag.PatientSex, studyDetails.LegacyStudy?.PatientSex ?? string.Empty);
        dataset.AddOrUpdate(DicomTag.AccessionNumber, study.AccessionNumber);
        dataset.AddOrUpdate(DicomTag.StudyInstanceUID, study.StudyInstanceUid);
        dataset.AddOrUpdate(DicomTag.StudyDate, study.StudyDate);
        dataset.AddOrUpdate(DicomTag.StudyDescription, study.StudyDescription);
        dataset.AddOrUpdate(DicomTag.ReferringPhysicianName, DicomFunctions.PersonNameVTCompatible(study.ReferringPhysician));
        dataset.AddOrUpdate(DicomTag.Modality, "SR");
        dataset.AddOrUpdate(DicomTag.SeriesDescription, SeriesDescription);
        dataset.AddOrUpdate(DicomTag.Manufacturer, DicomTagConstants.KPACSManufacturer);
    }

    private static IEnumerable<InstanceReference> ResolveReferencedInstances(StudyDetails studyDetails, IEnumerable<StudyMeasurement> measurements)
    {
        Dictionary<string, (SeriesRecord Series, InstanceRecord Instance)> index = studyDetails.Series
            .SelectMany(series => series.Instances.Select(instance => new KeyValuePair<string, (SeriesRecord, InstanceRecord)>(
                instance.FilePath,
                (series, instance))))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

        HashSet<string> seenSops = new(StringComparer.Ordinal);
        foreach (StudyMeasurement measurement in measurements)
        {
            if (string.IsNullOrWhiteSpace(measurement.SourceFilePath))
            {
                continue;
            }

            if (!index.TryGetValue(measurement.SourceFilePath, out (SeriesRecord Series, InstanceRecord Instance) match))
            {
                continue;
            }

            if (!seenSops.Add(match.Instance.SopInstanceUid))
            {
                continue;
            }

            yield return new InstanceReference(match.Series.SeriesInstanceUid, match.Instance.SopClassUid, match.Instance.SopInstanceUid);
        }
    }

    private static int ComputeSeriesNumber(StudyDetails studyDetails)
    {
        int maxSeriesNumber = studyDetails.Series.Count == 0 ? 0 : studyDetails.Series.Max(series => series.SeriesNumber);
        return Math.Max(1, maxSeriesNumber + 100);
    }

    private static string BuildSummaryText(StudyDetails studyDetails, IReadOnlyCollection<StudyMeasurement> measurements, IReadOnlyCollection<SliceRadiomicsResult> radiomics, IReadOnlyCollection<RegistrationSceneState> scenes)
    {
        return $"Research export for study {studyDetails.Study.StudyInstanceUid}. Measurements: {measurements.Count}. Radiomics entries: {radiomics.Count}. 3D scenes: {scenes.Count}. Generated by K-PACS research workstation.";
    }

    private static string BuildMeasurementText(StudyMeasurement measurement, SliceRadiomicsResult? radiomics)
    {
        string label = measurement.Tracking?.Label ?? string.Empty;
        if (string.IsNullOrWhiteSpace(label))
        {
            label = measurement.Kind.ToString();
        }

        var builder = new StringBuilder();
        builder.Append(label);
        builder.Append(" | kind=").Append(measurement.Kind);
        if (measurement.Tracking is not null)
        {
            builder.Append(" | state=").Append(measurement.Tracking.ReviewState);
            builder.Append(" | confidence=").Append(measurement.Tracking.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(measurement.Tracking.TimepointLabel))
            {
                builder.Append(" | timepoint=").Append(measurement.Tracking.TimepointLabel);
            }
        }

        if (radiomics is not null)
        {
            builder.Append(" | area_mm2=").Append(radiomics.AreaSquareMillimeters.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(" | mean=").Append(radiomics.Mean.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(" | entropy=").Append(radiomics.Entropy.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string BuildSceneText(RegistrationSceneState scene)
    {
        var builder = new StringBuilder();
        builder.Append(scene.Label);
        builder.Append(" | kind=").Append(scene.Kind);
        builder.Append(" | transform=").Append(scene.Transform.Kind);
        builder.Append(" | sourceSeries=").Append(scene.SourceSeriesInstanceUid);
        builder.Append(" | targetSeries=").Append(scene.TargetSeriesInstanceUid);

        if (scene.Refinement is not null)
        {
            builder.Append(" | regConf=").Append(scene.Refinement.RegistrationConfidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(" | localScore=").Append(scene.Refinement.LocalMatchScore.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(" | shiftMm=").Append(scene.Refinement.AppliedDisplacementMm.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (scene.Focus is not null)
        {
            builder.Append(" | focus=")
                .Append(scene.Focus.TargetPatientPoint.X.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(scene.Focus.TargetPatientPoint.Y.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(scene.Focus.TargetPatientPoint.Z.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static ResearchSrPayload BuildPayload(
        StudyDetails studyDetails,
        IReadOnlyCollection<StudyMeasurement> measurements,
        IReadOnlyCollection<SliceRadiomicsResult> radiomics,
        IReadOnlyCollection<RegistrationSceneState> scenes,
        string sopInstanceUid,
        string reportTitle)
    {
        return new ResearchSrPayload(
            "1.0",
            reportTitle,
            studyDetails.Study.StudyInstanceUid,
            sopInstanceUid,
            DateTimeOffset.UtcNow,
            measurements.Select(measurement => new ResearchMeasurementPayload(
                measurement.Id,
                measurement.Kind.ToString(),
                measurement.SourceFilePath,
                measurement.ReferencedSopInstanceUid,
                measurement.FrameOfReferenceUid,
                measurement.AcquisitionNumber,
                measurement.AnnotationText,
                measurement.Tracking,
                measurement.Anchors.Select(anchor => new ResearchAnchorPayload(
                    anchor.ImagePoint.X,
                    anchor.ImagePoint.Y,
                    anchor.PatientPoint?.X,
                    anchor.PatientPoint?.Y,
                    anchor.PatientPoint?.Z)).ToArray())).ToArray(),
                    radiomics.ToArray(),
                    scenes.ToArray());
    }

    private static byte[] Compress(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(payload, 0, payload.Length);
        }

        return output.ToArray();
    }

    private static void AddImageReference(DicomDataset dataset, string seriesInstanceUid, string sopClassUid, string sopInstanceUid)
    {
        if (string.IsNullOrWhiteSpace(seriesInstanceUid) || string.IsNullOrWhiteSpace(sopInstanceUid))
        {
            return;
        }

        if (!dataset.Contains(CurrentRequestedProcedureEvidenceSequenceTag))
        {
            dataset.AddOrUpdate(new DicomSequence(CurrentRequestedProcedureEvidenceSequenceTag));
        }

        DicomSequence evidenceSequence = dataset.GetSequence(CurrentRequestedProcedureEvidenceSequenceTag);
        DicomDataset studyItem;
        if (evidenceSequence.Items.Count == 0)
        {
            studyItem = new DicomDataset();
            studyItem.AddOrUpdate(DicomTag.StudyInstanceUID, dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty));
            studyItem.AddOrUpdate(new DicomSequence(ReferencedSeriesSequenceTag));
            evidenceSequence.Items.Add(studyItem);
        }
        else
        {
            studyItem = evidenceSequence.Items[0];
            if (!studyItem.Contains(ReferencedSeriesSequenceTag))
            {
                studyItem.AddOrUpdate(new DicomSequence(ReferencedSeriesSequenceTag));
            }
        }

        DicomSequence seriesSequence = studyItem.GetSequence(ReferencedSeriesSequenceTag);
        DicomDataset? seriesItem = seriesSequence.Items.FirstOrDefault(item =>
            string.Equals(item.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty), seriesInstanceUid, StringComparison.Ordinal));
        if (seriesItem is null)
        {
            seriesItem = new DicomDataset();
            seriesItem.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesInstanceUid);
            seriesItem.AddOrUpdate(new DicomSequence(ReferencedSopSequenceTag));
            seriesSequence.Items.Add(seriesItem);
        }

        DicomSequence sopSequence = seriesItem.GetSequence(ReferencedSopSequenceTag);
        bool exists = sopSequence.Items.Any(item =>
            string.Equals(item.GetSingleValueOrDefault(DicomTag.ReferencedSOPInstanceUID, string.Empty), sopInstanceUid, StringComparison.Ordinal));
        if (exists)
        {
            return;
        }

        var referenceItem = new DicomDataset();
        referenceItem.AddOrUpdate(DicomTag.ReferencedSOPClassUID,
            string.IsNullOrWhiteSpace(sopClassUid) ? DicomTagConstants.UID_CTImageStorage : sopClassUid);
        referenceItem.AddOrUpdate(DicomTag.ReferencedSOPInstanceUID, sopInstanceUid);
        sopSequence.Items.Add(referenceItem);
    }

    private static void AddPrivateTextPayload(DicomDataset dataset, ushort element, string text)
    {
        dataset.AddOrUpdate(new DicomTag(0x0009, 0x0010), PrivateCreator);
        dataset.AddOrUpdate(new DicomUnlimitedText(new DicomTag(0x0009, element), text ?? string.Empty));
    }

    private static void AddPrivateBinaryPayload(DicomDataset dataset, ushort element, byte[] payload)
    {
        dataset.AddOrUpdate(new DicomTag(0x0009, 0x0010), PrivateCreator);
        dataset.AddOrUpdate(new DicomOtherByte(new DicomTag(0x0009, element), payload));
    }

    private static string SanitizeFileComponent(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return value;
    }

    private sealed record InstanceReference(string SeriesInstanceUid, string SopClassUid, string SopInstanceUid);

    private sealed record ResearchSrPayload(
        string SchemaVersion,
        string Title,
        string StudyInstanceUid,
        string SopInstanceUid,
        DateTimeOffset CreatedUtc,
        ResearchMeasurementPayload[] Measurements,
        SliceRadiomicsResult[] Radiomics,
        RegistrationSceneState[] Scenes);

    private sealed record ResearchMeasurementPayload(
        Guid Id,
        string Kind,
        string SourceFilePath,
        string ReferencedSopInstanceUid,
        string FrameOfReferenceUid,
        string AcquisitionNumber,
        string AnnotationText,
        MeasurementTrackingMetadata? Tracking,
        ResearchAnchorPayload[] Anchors);

    private sealed record ResearchAnchorPayload(
        double ImageX,
        double ImageY,
        double? PatientX,
        double? PatientY,
        double? PatientZ);
}