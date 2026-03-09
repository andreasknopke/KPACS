using System.Globalization;
using Avalonia;
using Avalonia.Input;
using FellowOakDicom;

namespace KPACS.Viewer.Models;

public readonly record struct Vector3D(double X, double Y, double Z)
{
    public static Vector3D operator +(Vector3D left, Vector3D right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    public static Vector3D operator -(Vector3D left, Vector3D right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    public static Vector3D operator *(Vector3D value, double scalar) =>
        new(value.X * scalar, value.Y * scalar, value.Z * scalar);

    public static Vector3D operator /(Vector3D value, double scalar) =>
        new(value.X / scalar, value.Y / scalar, value.Z / scalar);

    public double Dot(Vector3D other) => X * other.X + Y * other.Y + Z * other.Z;

    public Vector3D Cross(Vector3D other) =>
        new(
            Y * other.Z - Z * other.Y,
            Z * other.X - X * other.Z,
            X * other.Y - Y * other.X);

    public double Length => Math.Sqrt(Dot(this));

    public Vector3D Normalize()
    {
        double length = Length;
        return length > 0 ? this / length : this;
    }
}

public sealed record DicomSpatialMetadata(
    string FilePath,
    string SopInstanceUid,
    string SeriesInstanceUid,
    string FrameOfReferenceUid,
    string AcquisitionNumber,
    int Width,
    int Height,
    double RowSpacing,
    double ColumnSpacing,
    Vector3D Origin,
    Vector3D RowDirection,
    Vector3D ColumnDirection,
    Vector3D Normal)
{
    public bool IsCompatibleWith(DicomSpatialMetadata other)
    {
        if (string.IsNullOrWhiteSpace(FrameOfReferenceUid) ||
            string.IsNullOrWhiteSpace(other.FrameOfReferenceUid))
        {
            return false;
        }

        if (!string.Equals(FrameOfReferenceUid, other.FrameOfReferenceUid, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(AcquisitionNumber) &&
            !string.IsNullOrWhiteSpace(other.AcquisitionNumber) &&
            !string.Equals(AcquisitionNumber, other.AcquisitionNumber, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    public Vector3D PatientPointFromPixel(Point imagePoint) =>
        Origin + (RowDirection * (imagePoint.X * ColumnSpacing)) + (ColumnDirection * (imagePoint.Y * RowSpacing));

    public Point PixelPointFromPatient(Vector3D patientPoint)
    {
        Vector3D relative = patientPoint - Origin;
        double x = relative.Dot(RowDirection) / ColumnSpacing;
        double y = relative.Dot(ColumnDirection) / RowSpacing;
        return new Point(x, y);
    }

    public double DistanceToPlane(Vector3D patientPoint) =>
        Math.Abs((patientPoint - Origin).Dot(Normal));

    public bool ContainsImagePoint(Point imagePoint, double tolerance = 0.5) =>
        imagePoint.X >= -tolerance &&
        imagePoint.Y >= -tolerance &&
        imagePoint.X <= Width - 1 + tolerance &&
        imagePoint.Y <= Height - 1 + tolerance;

    public static DicomSpatialMetadata? FromDataset(DicomDataset dataset, string filePath)
    {
        if (!TryGetVector(dataset, DicomTag.ImageOrientationPatient, 6, out double[]? iopValues) ||
            !TryGetVector(dataset, DicomTag.ImagePositionPatient, 3, out double[]? ippValues) ||
            !TryGetVector(dataset, DicomTag.PixelSpacing, 2, out double[]? spacingValues))
        {
            return null;
        }

        double[] iop = iopValues!;
        double[] ipp = ippValues!;
        double[] spacing = spacingValues!;

        int width = dataset.GetSingleValueOrDefault(DicomTag.Columns, 0);
        int height = dataset.GetSingleValueOrDefault(DicomTag.Rows, 0);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        double rowSpacing = spacing[0];
        double columnSpacing = spacing[1];
        if (rowSpacing <= 0 || columnSpacing <= 0)
        {
            return null;
        }

        Vector3D rowDirection = new(iop[0], iop[1], iop[2]);
        Vector3D columnDirection = new(iop[3], iop[4], iop[5]);
        if (rowDirection.Length == 0 || columnDirection.Length == 0)
        {
            return null;
        }

        rowDirection = rowDirection.Normalize();
        columnDirection = columnDirection.Normalize();
        Vector3D normal = rowDirection.Cross(columnDirection).Normalize();
        if (normal.Length == 0)
        {
            return null;
        }

        return new DicomSpatialMetadata(
            filePath,
            dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty),
            dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty),
            dataset.GetSingleValueOrDefault(DicomTag.FrameOfReferenceUID, string.Empty),
            dataset.GetSingleValueOrDefault(DicomTag.AcquisitionNumber, string.Empty),
            width,
            height,
            rowSpacing,
            columnSpacing,
            new Vector3D(ipp[0], ipp[1], ipp[2]),
            rowDirection,
            columnDirection,
            normal);
    }

    private static bool TryGetVector(DicomDataset dataset, DicomTag tag, int expectedCount, out double[]? values)
    {
        values = null;

        try
        {
            string[] parts = dataset.GetValues<string>(tag);
            if (parts.Length < expectedCount)
            {
                return false;
            }

            var parsed = new double[expectedCount];
            for (int index = 0; index < expectedCount; index++)
            {
                if (!double.TryParse(parts[index], NumberStyles.Float | NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out parsed[index]))
                {
                    return false;
                }
            }

            values = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record DicomHoverInfo(Point ImagePoint, KeyModifiers Modifiers);