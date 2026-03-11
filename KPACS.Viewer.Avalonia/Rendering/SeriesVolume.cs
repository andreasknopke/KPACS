// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - Rendering/SeriesVolume.cs
// 3D voxel volume built from a sorted DICOM series.
// Stores the complete pixel data of all slices in a contiguous buffer so that
// any arbitrary slice (axial, coronal, sagittal, oblique) can be extracted
// without reloading files from disk.
// ------------------------------------------------------------------------------------------------

using KPACS.Viewer.Models;

namespace KPACS.Viewer.Rendering;

/// <summary>
/// Immutable 3D voxel volume for a single DICOM series.
/// All pixel values are stored as 16-bit signed (short) after rescale.
/// RGB series are not supported — those keep the legacy slice path.
/// </summary>
public sealed class SeriesVolume
{
    /// <summary>Contiguous voxel buffer [z * SizeY * SizeX + y * SizeX + x].</summary>
    public short[] Voxels { get; }

    /// <summary>Number of columns (pixels per row).</summary>
    public int SizeX { get; }

    /// <summary>Number of rows (pixels per column).</summary>
    public int SizeY { get; }

    /// <summary>Number of slices along the acquisition direction.</summary>
    public int SizeZ { get; }

    /// <summary>Pixel spacing in mm along X (column direction).</summary>
    public double SpacingX { get; }

    /// <summary>Pixel spacing in mm along Y (row direction).</summary>
    public double SpacingY { get; }

    /// <summary>Slice spacing in mm along Z (normal direction).</summary>
    public double SpacingZ { get; }

    /// <summary>Patient-space origin of the first voxel (0,0,0).</summary>
    public Vector3D Origin { get; }

    /// <summary>Unit vector along columns (X axis in patient space).</summary>
    public Vector3D RowDirection { get; }

    /// <summary>Unit vector along rows (Y axis in patient space).</summary>
    public Vector3D ColumnDirection { get; }

    /// <summary>Unit vector perpendicular to slices (Z axis in patient space).</summary>
    public Vector3D Normal { get; }

    /// <summary>Window center from the first slice (default preset).</summary>
    public double DefaultWindowCenter { get; }

    /// <summary>Window width from the first slice (default preset).</summary>
    public double DefaultWindowWidth { get; }

    /// <summary>Minimum rescaled voxel value in the volume.</summary>
    public short MinValue { get; }

    /// <summary>Maximum rescaled voxel value in the volume.</summary>
    public short MaxValue { get; }

    /// <summary>True if photometric interpretation is MONOCHROME1 (inverted).</summary>
    public bool IsMonochrome1 { get; }

    /// <summary>Series Instance UID this volume was built from.</summary>
    public string SeriesInstanceUid { get; }

    /// <summary>
    /// Ordered file paths of the instances that make up this volume.
    /// Index i corresponds to slice z = i.
    /// </summary>
    public IReadOnlyList<string> SliceFilePaths { get; }

    public SeriesVolume(
        short[] voxels,
        int sizeX, int sizeY, int sizeZ,
        double spacingX, double spacingY, double spacingZ,
        Vector3D origin,
        Vector3D rowDirection, Vector3D columnDirection, Vector3D normal,
        double defaultWindowCenter, double defaultWindowWidth,
        short minValue, short maxValue,
        bool isMonochrome1,
        string seriesInstanceUid,
        IReadOnlyList<string> sliceFilePaths)
    {
        Voxels = voxels;
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
        SpacingX = spacingX;
        SpacingY = spacingY;
        SpacingZ = spacingZ;
        Origin = origin;
        RowDirection = rowDirection;
        ColumnDirection = columnDirection;
        Normal = normal;
        DefaultWindowCenter = defaultWindowCenter;
        DefaultWindowWidth = defaultWindowWidth;
        MinValue = minValue;
        MaxValue = maxValue;
        IsMonochrome1 = isMonochrome1;
        SeriesInstanceUid = seriesInstanceUid;
        SliceFilePaths = sliceFilePaths;
    }

    /// <summary>
    /// Gets a voxel value with bounds checking. Returns 0 for out-of-bounds coordinates.
    /// </summary>
    public short GetVoxel(int x, int y, int z)
    {
        if ((uint)x >= (uint)SizeX || (uint)y >= (uint)SizeY || (uint)z >= (uint)SizeZ)
            return 0;
        return Voxels[z * SizeY * SizeX + y * SizeX + x];
    }

    /// <summary>
    /// Gets an interpolated voxel value using trilinear interpolation.
    /// </summary>
    public double GetVoxelInterpolated(double x, double y, double z)
    {
        if (x < 0 || y < 0 || z < 0 || x >= SizeX - 1 || y >= SizeY - 1 || z >= SizeZ - 1)
        {
            // Clamp to nearest edge voxel
            return GetVoxel(
                Math.Clamp((int)Math.Round(x), 0, SizeX - 1),
                Math.Clamp((int)Math.Round(y), 0, SizeY - 1),
                Math.Clamp((int)Math.Round(z), 0, SizeZ - 1));
        }

        int x0 = (int)x, y0 = (int)y, z0 = (int)z;
        double fx = x - x0, fy = y - y0, fz = z - z0;

        // Trilinear interpolation
        double c000 = Voxels[z0 * SizeY * SizeX + y0 * SizeX + x0];
        double c100 = Voxels[z0 * SizeY * SizeX + y0 * SizeX + x0 + 1];
        double c010 = Voxels[z0 * SizeY * SizeX + (y0 + 1) * SizeX + x0];
        double c110 = Voxels[z0 * SizeY * SizeX + (y0 + 1) * SizeX + x0 + 1];
        double c001 = Voxels[(z0 + 1) * SizeY * SizeX + y0 * SizeX + x0];
        double c101 = Voxels[(z0 + 1) * SizeY * SizeX + y0 * SizeX + x0 + 1];
        double c011 = Voxels[(z0 + 1) * SizeY * SizeX + (y0 + 1) * SizeX + x0];
        double c111 = Voxels[(z0 + 1) * SizeY * SizeX + (y0 + 1) * SizeX + x0 + 1];

        double c00 = c000 * (1 - fx) + c100 * fx;
        double c10 = c010 * (1 - fx) + c110 * fx;
        double c01 = c001 * (1 - fx) + c101 * fx;
        double c11 = c011 * (1 - fx) + c111 * fx;

        double c0 = c00 * (1 - fy) + c10 * fy;
        double c1 = c01 * (1 - fy) + c11 * fy;

        return c0 * (1 - fz) + c1 * fz;
    }

    /// <summary>
    /// Converts a voxel coordinate to a patient-space point.
    /// </summary>
    public Vector3D VoxelToPatient(double vx, double vy, double vz) =>
        Origin
        + RowDirection * (vx * SpacingX)
        + ColumnDirection * (vy * SpacingY)
        + Normal * (vz * SpacingZ);

    /// <summary>
    /// Converts a patient-space point to voxel coordinates.
    /// </summary>
    public (double X, double Y, double Z) PatientToVoxel(Vector3D patientPoint)
    {
        Vector3D relative = patientPoint - Origin;
        double vx = relative.Dot(RowDirection) / SpacingX;
        double vy = relative.Dot(ColumnDirection) / SpacingY;
        double vz = relative.Dot(Normal) / SpacingZ;
        return (vx, vy, vz);
    }

    /// <summary>
    /// Returns spatial metadata for a given axial slice index, compatible with the
    /// existing <see cref="DicomSpatialMetadata"/> used by linked-view synchronization.
    /// </summary>
    public DicomSpatialMetadata GetSliceSpatialMetadata(int sliceIndex)
    {
        sliceIndex = Math.Clamp(sliceIndex, 0, SizeZ - 1);
        Vector3D sliceOrigin = Origin + Normal * (sliceIndex * SpacingZ);
        string filePath = sliceIndex < SliceFilePaths.Count ? SliceFilePaths[sliceIndex] : "";
        return new DicomSpatialMetadata(
            filePath,
            SopInstanceUid: "",
            SeriesInstanceUid,
            FrameOfReferenceUid: "",
            AcquisitionNumber: "",
            SizeX, SizeY,
            SpacingY, SpacingX,
            sliceOrigin,
            RowDirection, ColumnDirection, Normal);
    }
}
