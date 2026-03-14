using Avalonia;

namespace KPACS.Viewer.Models;

public enum MeasurementKind
{
    Line,
    Angle,
    Annotation,
    RectangleRoi,
    EllipseRoi,
    PolygonRoi,
    VolumeRoi,
}

public sealed record MeasurementAnchor(Point ImagePoint, Vector3D? PatientPoint);

public sealed record VolumeRoiContour(
    MeasurementAnchor[] Anchors,
    string SourceFilePath,
    string ReferencedSopInstanceUid,
    Vector3D PlaneOrigin,
    Vector3D RowDirection,
    Vector3D ColumnDirection,
    Vector3D Normal,
    double PlanePosition,
    bool IsClosed = true,
    double RowSpacing = 1,
    double ColumnSpacing = 1,
    int ComponentId = 0)
{
    public bool TryProjectTo(DicomSpatialMetadata metadata, out Point[] imagePoints)
    {
        imagePoints = [];

        if (Anchors.Length == 0)
        {
            return false;
        }

        double planeTolerance = Math.Max(0.75, Math.Min(metadata.RowSpacing, metadata.ColumnSpacing));
        if (Anchors.Any(anchor => anchor.PatientPoint is null || metadata.DistanceToPlane(anchor.PatientPoint.Value) > planeTolerance))
        {
            return false;
        }

        imagePoints = Anchors
            .Select(anchor => metadata.PixelPointFromPatient(anchor.PatientPoint!.Value))
            .ToArray();
        return imagePoints.Length > 0;
    }
}

public sealed record StudyMeasurement(
    Guid Id,
    MeasurementKind Kind,
    string SourceFilePath,
    string ReferencedSopInstanceUid,
    string FrameOfReferenceUid,
    string AcquisitionNumber,
    MeasurementAnchor[] Anchors,
    string AnnotationText = "",
    MeasurementTrackingMetadata? Tracking = null,
    Point? LabelOffset = null,
    VolumeRoiContour[]? VolumeContours = null)
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
            annotationText,
            LabelOffset: new Point(10, 10));
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

    public StudyMeasurement WithLabelOffset(Point labelOffset) =>
        this with { LabelOffset = labelOffset };

    public StudyMeasurement WithTracking(MeasurementTrackingMetadata? tracking) =>
        this with { Tracking = tracking };

    public StudyMeasurement WithVolumeContours(IEnumerable<VolumeRoiContour> volumeContours) =>
        this with
        {
            VolumeContours = volumeContours.ToArray(),
            Anchors = volumeContours.SelectMany(contour => contour.Anchors).ToArray(),
        };

    public static StudyMeasurement CreateVolumeRoi(
        string sourceFilePath,
        DicomSpatialMetadata? metadata,
        IEnumerable<VolumeRoiContour> volumeContours)
    {
        VolumeRoiContour[] contours = volumeContours.ToArray();
        VolumeRoiContour? firstContour = contours.FirstOrDefault();

        return new StudyMeasurement(
            Guid.NewGuid(),
            MeasurementKind.VolumeRoi,
            firstContour?.SourceFilePath ?? sourceFilePath,
            firstContour?.ReferencedSopInstanceUid ?? metadata?.SopInstanceUid ?? string.Empty,
            metadata?.FrameOfReferenceUid ?? string.Empty,
            metadata?.AcquisitionNumber ?? string.Empty,
            contours.SelectMany(contour => contour.Anchors).ToArray(),
            LabelOffset: new Point(10, 10),
            VolumeContours: contours);
    }

    public bool TryGetPatientCenter(out Vector3D center)
    {
        Vector3D[] patientPoints = Anchors
            .Where(anchor => anchor.PatientPoint is not null)
            .Select(anchor => anchor.PatientPoint!.Value)
            .ToArray();

        if (patientPoints.Length == 0)
        {
            center = default;
            return false;
        }

        Vector3D sum = default;
        foreach (Vector3D point in patientPoints)
        {
            sum += point;
        }

        center = sum / patientPoints.Length;
        return true;
    }

    public bool TryProjectTo(DicomSpatialMetadata? metadata, out Point[] imagePoints)
    {
        imagePoints = [];

        if (Kind == MeasurementKind.VolumeRoi)
        {
            if (TryProjectVolumeContoursTo(metadata, out Point[][] volumeContours) && volumeContours.Length > 0)
            {
                imagePoints = volumeContours[0];
                return true;
            }

            return false;
        }

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

    public bool TryProjectVolumeContoursTo(DicomSpatialMetadata? metadata, out Point[][] contours)
    {
        return TryProjectVolumeContoursTo(metadata, out contours, out _);
    }

    public bool TryProjectVolumeContoursTo(DicomSpatialMetadata? metadata, out Point[][] contours, out bool isInterpolated)
    {
        contours = [];
        isInterpolated = false;

        if (metadata is null || VolumeContours is null || VolumeContours.Length == 0 || !IsCompatibleWith(metadata))
        {
            return false;
        }

        List<Point[]> projected = [];
        foreach (VolumeRoiContour contour in VolumeContours)
        {
            if (!contour.IsClosed || !contour.TryProjectTo(metadata, out Point[] imagePoints) || imagePoints.Length < 3)
            {
                continue;
            }

            projected.Add(imagePoints);
        }

        if (projected.Count > 0)
        {
            contours = projected.ToArray();
            return true;
        }

        if (TryProjectInterpolatedVolumeContour(metadata, out Point[] interpolatedContour))
        {
            contours = [interpolatedContour];
            isInterpolated = true;
            return true;
        }

        return false;
    }

    private bool TryProjectInterpolatedVolumeContour(DicomSpatialMetadata metadata, out Point[] imagePoints)
    {
        imagePoints = [];

        if (VolumeContours is null || VolumeContours.Length < 2)
        {
            return false;
        }

        const int sampleCount = 40;
        double currentPlanePosition = metadata.Origin.Dot(metadata.Normal);

        (VolumeRoiContour Contour, Vector3D[] Points)[] closedContours = VolumeContours
            .Where(contour => contour.IsClosed && contour.Anchors.Length >= 3)
            .OrderBy(contour => contour.PlanePosition)
            .Select(contour => (Contour: contour, Points: ResampleClosedContour(contour, sampleCount)))
            .Where(item => item.Points.Length >= 3)
            .ToArray();

        if (closedContours.Length < 2)
        {
            return false;
        }

        for (int index = 1; index < closedContours.Length; index++)
        {
            if (closedContours[index - 1].Points.Length == closedContours[index].Points.Length)
            {
                closedContours[index] = (
                    closedContours[index].Contour,
                    AlignContourPoints(closedContours[index - 1].Points, closedContours[index].Points));
            }
        }

        for (int index = 0; index < closedContours.Length - 1; index++)
        {
            (VolumeRoiContour lowerContour, Vector3D[] lowerPoints) = closedContours[index];
            (VolumeRoiContour upperContour, Vector3D[] upperPoints) = closedContours[index + 1];

            double lowerPlane = lowerContour.PlanePosition;
            double upperPlane = upperContour.PlanePosition;
            if (currentPlanePosition <= lowerPlane || currentPlanePosition >= upperPlane)
            {
                continue;
            }

            double thickness = upperPlane - lowerPlane;
            if (Math.Abs(thickness) <= double.Epsilon)
            {
                continue;
            }

            double t = (currentPlanePosition - lowerPlane) / thickness;
            Vector3D[] interpolatedPoints = InterpolateContourPoints(lowerPoints, upperPoints, t);
            imagePoints = interpolatedPoints
                .Select(metadata.PixelPointFromPatient)
                .ToArray();

            return imagePoints.Length >= 3;
        }

        return false;
    }

    private static Vector3D[] ResampleClosedContour(VolumeRoiContour contour, int sampleCount)
    {
        Vector3D[] points = contour.Anchors
            .Where(anchor => anchor.PatientPoint is not null)
            .Select(anchor => anchor.PatientPoint!.Value)
            .ToArray();
        if (points.Length < 3 || sampleCount < 3)
        {
            return points;
        }

        double[] cumulative = new double[points.Length + 1];
        for (int index = 0; index < points.Length; index++)
        {
            cumulative[index + 1] = cumulative[index] + GetDistance(points[index], points[(index + 1) % points.Length]);
        }

        double totalLength = cumulative[^1];
        if (totalLength <= double.Epsilon)
        {
            return points;
        }

        Vector3D[] result = new Vector3D[sampleCount];
        double step = totalLength / sampleCount;
        int segmentIndex = 0;

        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            double target = sampleIndex * step;
            while (segmentIndex < points.Length - 1 && cumulative[segmentIndex + 1] < target)
            {
                segmentIndex++;
            }

            double segmentStart = cumulative[segmentIndex];
            double segmentEnd = cumulative[segmentIndex + 1];
            double segmentLength = Math.Max(double.Epsilon, segmentEnd - segmentStart);
            double t = (target - segmentStart) / segmentLength;
            result[sampleIndex] = Lerp(points[segmentIndex], points[(segmentIndex + 1) % points.Length], t);
        }

        if (GetSignedContourArea(result, contour.PlaneOrigin, contour.RowDirection, contour.ColumnDirection) < 0)
        {
            Array.Reverse(result);
        }

        return result;
    }

    private static Vector3D[] AlignContourPoints(Vector3D[] reference, Vector3D[] candidate)
    {
        if (reference.Length == 0 || candidate.Length == 0 || reference.Length != candidate.Length)
        {
            return candidate;
        }

        int bestShift = 0;
        double bestCost = double.MaxValue;
        for (int shift = 0; shift < candidate.Length; shift++)
        {
            double cost = 0;
            for (int index = 0; index < reference.Length; index++)
            {
                Vector3D delta = reference[index] - candidate[(index + shift) % candidate.Length];
                cost += delta.Dot(delta);
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                bestShift = shift;
            }
        }

        if (bestShift == 0)
        {
            return candidate;
        }

        Vector3D[] aligned = new Vector3D[candidate.Length];
        for (int index = 0; index < candidate.Length; index++)
        {
            aligned[index] = candidate[(index + bestShift) % candidate.Length];
        }

        return aligned;
    }

    private static Vector3D[] InterpolateContourPoints(Vector3D[] first, Vector3D[] second, double t)
    {
        int count = Math.Min(first.Length, second.Length);
        Vector3D[] points = new Vector3D[count];
        for (int index = 0; index < count; index++)
        {
            points[index] = Lerp(first[index], second[index], t);
        }

        return points;
    }

    private static double GetDistance(Vector3D first, Vector3D second) => (first - second).Length;

    private static double GetSignedContourArea(
        IReadOnlyList<Vector3D> contour,
        Vector3D planeOrigin,
        Vector3D rowDirection,
        Vector3D columnDirection)
    {
        if (contour.Count < 3)
        {
            return 0;
        }

        double area = 0;
        for (int index = 0; index < contour.Count; index++)
        {
            Vector3D currentRelative = contour[index] - planeOrigin;
            Vector3D nextRelative = contour[(index + 1) % contour.Count] - planeOrigin;
            double currentX = currentRelative.Dot(rowDirection);
            double currentY = currentRelative.Dot(columnDirection);
            double nextX = nextRelative.Dot(rowDirection);
            double nextY = nextRelative.Dot(columnDirection);
            area += (currentX * nextY) - (nextX * currentY);
        }

        return area * 0.5;
    }

    private static Vector3D Lerp(Vector3D first, Vector3D second, double t) => first + ((second - first) * t);

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