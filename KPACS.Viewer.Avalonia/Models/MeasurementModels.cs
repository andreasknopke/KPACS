using Avalonia;

namespace KPACS.Viewer.Models;

public enum MeasurementKind
{
    Line,
    Angle,
    Annotation,
    RectangleRoi,
    PolygonRoi,
}

public sealed record MeasurementAnchor(Point ImagePoint, Vector3D? PatientPoint);

public sealed record StudyMeasurement(
    Guid Id,
    MeasurementKind Kind,
    string SourceFilePath,
    string ReferencedSopInstanceUid,
    string FrameOfReferenceUid,
    string AcquisitionNumber,
    MeasurementAnchor[] Anchors,
    string AnnotationText = "")
{
    public static StudyMeasurement Create(
        MeasurementKind kind,
        string sourceFilePath,
        DicomSpatialMetadata? metadata,
        IReadOnlyList<Point> imagePoints,
        string annotationText = "")
    {
        ArgumentNullException.ThrowIfNull(imagePoints);

        return new StudyMeasurement(
            Guid.NewGuid(),
            kind,
            sourceFilePath,
            metadata?.SopInstanceUid ?? string.Empty,
            metadata?.FrameOfReferenceUid ?? string.Empty,
            metadata?.AcquisitionNumber ?? string.Empty,
            imagePoints.Select(point => new MeasurementAnchor(
                point,
                metadata?.PatientPointFromPixel(point))).ToArray(),
            annotationText);
    }

    public StudyMeasurement WithAnchors(DicomSpatialMetadata? metadata, IReadOnlyList<Point> imagePoints) =>
        this with
        {
            Anchors = imagePoints
                .Select(point => new MeasurementAnchor(point, metadata?.PatientPointFromPixel(point)))
                .ToArray(),
        };

    public StudyMeasurement WithAnnotationText(string annotationText) =>
        this with { AnnotationText = annotationText ?? string.Empty };

    public bool TryProjectTo(DicomSpatialMetadata? metadata, out Point[] imagePoints)
    {
        imagePoints = [];

        if (Anchors.Length == 0)
        {
            return false;
        }

        if (metadata is not null &&
            Anchors.All(anchor => anchor.PatientPoint is not null) &&
            IsCompatibleWith(metadata))
        {
            double planeTolerance = Math.Max(0.75, Math.Min(metadata.RowSpacing, metadata.ColumnSpacing));
            if (Anchors.Any(anchor => metadata.DistanceToPlane(anchor.PatientPoint!.Value) > planeTolerance))
            {
                return false;
            }

            imagePoints = Anchors
                .Select(anchor => metadata.PixelPointFromPatient(anchor.PatientPoint!.Value))
                .ToArray();
            return true;
        }

        if (!string.IsNullOrWhiteSpace(SourceFilePath) &&
            metadata is not null &&
            string.Equals(SourceFilePath, metadata.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            imagePoints = Anchors.Select(anchor => anchor.ImagePoint).ToArray();
            return true;
        }

        return false;
    }

    private bool IsCompatibleWith(DicomSpatialMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(FrameOfReferenceUid) ||
            string.IsNullOrWhiteSpace(metadata.FrameOfReferenceUid) ||
            !string.Equals(FrameOfReferenceUid, metadata.FrameOfReferenceUid, StringComparison.Ordinal))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(AcquisitionNumber) ||
            string.IsNullOrWhiteSpace(metadata.AcquisitionNumber) ||
            string.Equals(AcquisitionNumber, metadata.AcquisitionNumber, StringComparison.Ordinal);
    }
}