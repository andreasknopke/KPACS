using Avalonia;
using Avalonia.Input;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer.Controls;

public partial class DicomViewPanel
{
    public sealed record AutoOutlinedMeasurementInfo(Guid MeasurementId, MeasurementKind Kind, Point SeedPoint, int SensitivityLevel);

    private static readonly int[] s_autoOutlineKernel = [1, 4, 6, 4, 1];
    private const int AutoOutlineMinPixels = 18;
    private const int AutoOutlineMaxPixels = 32000;
    private const int AutoOutlineMaxVoxelCount = 180000;
    private const int AutoOutlineMinSensitivityLevel = -6;
    private const int AutoOutlineMaxSensitivityLevel = 6;

    public event Action<AutoOutlinedMeasurementInfo>? AutoOutlinedMeasurementCreated;

    private bool TryCreateAutoOutlinedPolygonMeasurement(Point imagePoint, int sensitivityLevel = 0)
    {
        if (!TryCreateAutoOutlinedPolygon(imagePoint, out Point[] polygonPoints, sensitivityLevel) || polygonPoints.Length < 3)
        {
            return false;
        }

        _measurementDraft = null;
        SetSelectedMeasurement(null);
        StudyMeasurement measurement = StudyMeasurement.Create(MeasurementKind.PolygonRoi, FilePath, SpatialMetadata, polygonPoints);
        SetSelectedMeasurement(measurement.Id);
        MeasurementCreated?.Invoke(measurement);
        AutoOutlinedMeasurementCreated?.Invoke(new AutoOutlinedMeasurementInfo(measurement.Id, MeasurementKind.PolygonRoi, ClampImagePoint(imagePoint), sensitivityLevel));
        UpdateMeasurementPresentation();
        return true;
    }

    public bool TryRefineAutoOutlinedPolygonMeasurement(
        StudyMeasurement measurement,
        Point seedPoint,
        int sensitivityLevel,
        out StudyMeasurement updatedMeasurement)
    {
        updatedMeasurement = measurement;
        if (measurement.Kind != MeasurementKind.PolygonRoi || SpatialMetadata is null)
        {
            return false;
        }

        if (!TryCreateAutoOutlinedPolygon(seedPoint, out Point[] polygonPoints, sensitivityLevel) || polygonPoints.Length < 3)
        {
            return false;
        }

        updatedMeasurement = measurement.WithAnchors(SpatialMetadata, polygonPoints);
        return true;
    }

    private bool TryCreateAutoOutlinedPolygon(Point imagePoint, out Point[] polygonPoints, int sensitivityLevel = 0)
    {
        polygonPoints = [];

        int seedX = Math.Clamp((int)Math.Round(imagePoint.X), 0, _imageWidth - 1);
        int seedY = Math.Clamp((int)Math.Round(imagePoint.Y), 0, _imageHeight - 1);
        if (!TryBuildAutoOutlineMask(seedX, seedY, sensitivityLevel, out AutoOutlineMask mask))
        {
            return false;
        }

        polygonPoints = TraceAutoOutlineBoundary(mask, maxPointCount: 64)
            .Select(point => new Point(point.X + mask.Left, point.Y + mask.Top))
            .ToArray();
        return polygonPoints.Length >= 3;
    }

    private bool TryCreateAutoOutlinedVolumeRoiDraft(Point imagePoint, int sensitivityLevel = 0)
    {
        if (_volume is null || SpatialMetadata is null)
        {
            return false;
        }

        if (!TrySegmentVolumeSeed(imagePoint, sensitivityLevel, out HashSet<int> region))
        {
            return false;
        }

        VolumeRoiContour[] contours = BuildAutoOutlinedVolumeContours(region);
        if (contours.Length == 0)
        {
            return false;
        }

        _measurementDraft = null;
        SetSelectedMeasurement(null);

        bool appendToExisting = _volumeRoiDraft is not null && CanRetainVolumeRoiDraftForCurrentSlice() && _volumeRoiDraft.AdditiveModeEnabled;
        VolumeRoiDraft draft;
        if (appendToExisting)
        {
            draft = _volumeRoiDraft!;
            draft.PendingAddContour = null;
            draft.AutoOutlineState = new VolumeRoiAutoOutlineState(ClampImagePoint(imagePoint), sensitivityLevel);
            int componentId = draft.NextComponentId++;
            draft.ActiveAddComponentId = componentId;
            foreach (VolumeRoiContour contour in contours.OrderBy(contour => contour.PlanePosition))
            {
                string sliceKey = BuildVolumeRoiSliceKey(draft.SeriesInstanceUid, draft.FrameOfReferenceUid, contour.PlanePosition);
                string contourKey = BuildVolumeRoiContourKey(sliceKey, componentId);
                VolumeRoiDraftContour incomingContour = new(
                    sliceKey,
                    contourKey,
                    componentId,
                    contour.SourceFilePath,
                    contour.ReferencedSopInstanceUid,
                    contour.PlaneOrigin,
                    contour.RowDirection,
                    contour.ColumnDirection,
                    contour.Normal,
                    contour.PlanePosition,
                    contour.RowSpacing,
                    contour.ColumnSpacing,
                    [.. contour.Anchors],
                    contour.IsClosed);

                draft.Contours[contourKey] = incomingContour;
            }
        }
        else
        {
            draft = new VolumeRoiDraft(
                SpatialMetadata.SeriesInstanceUid,
                SpatialMetadata.FrameOfReferenceUid,
                SpatialMetadata.AcquisitionNumber,
                SpatialMetadata.Normal.Normalize(),
                GetPlanePosition(SpatialMetadata),
                FilePath,
                SpatialMetadata.SopInstanceUid)
            {
                AutoOutlineState = new VolumeRoiAutoOutlineState(ClampImagePoint(imagePoint), sensitivityLevel),
                AdditiveModeEnabled = _volumeRoiDraft?.AdditiveModeEnabled == true,
            };

            foreach (VolumeRoiContour contour in contours.OrderBy(contour => contour.PlanePosition))
            {
                string sliceKey = BuildVolumeRoiSliceKey(draft.SeriesInstanceUid, draft.FrameOfReferenceUid, contour.PlanePosition);
                string contourKey = BuildVolumeRoiContourKey(sliceKey, contour.ComponentId);
                draft.Contours[contourKey] = new VolumeRoiDraftContour(
                    sliceKey,
                    contourKey,
                    contour.ComponentId,
                    contour.SourceFilePath,
                    contour.ReferencedSopInstanceUid,
                    contour.PlaneOrigin,
                    contour.RowDirection,
                    contour.ColumnDirection,
                    contour.Normal,
                    contour.PlanePosition,
                    contour.RowSpacing,
                    contour.ColumnSpacing,
                    [.. contour.Anchors],
                    contour.IsClosed);
            }

            draft.NextComponentId = Math.Max(1, contours.Select(contour => contour.ComponentId).DefaultIfEmpty(0).Max() + 1);
            draft.ActiveAddComponentId = null;

            _volumeRoiDraft = draft;
        }

        NotifyVolumeRoiDraftChanged();
        UpdateMeasurementPresentation();
        return true;
    }

    private bool TryBuildAutoOutlineMask(int seedX, int seedY, int sensitivityLevel, out AutoOutlineMask mask)
    {
        mask = default;
        if (!TryGetPixelValue(seedX, seedY, out double seedValue))
        {
            return false;
        }

        int radius = Math.Clamp(Math.Max(_imageWidth, _imageHeight) / 5, 36, 120);
        int left = Math.Max(0, seedX - radius);
        int top = Math.Max(0, seedY - radius);
        int right = Math.Min(_imageWidth - 1, seedX + radius);
        int bottom = Math.Min(_imageHeight - 1, seedY + radius);
        int width = right - left + 1;
        int height = bottom - top + 1;
        if (width <= 2 || height <= 2)
        {
            return false;
        }

        int localSeedX = seedX - left;
        int localSeedY = seedY - top;
        double[,] homogenizedValues = BuildHomogenizedPixelWindow(left, top, width, height);
        double homogenizedSeedValue = homogenizedValues[localSeedX, localSeedY];
        if (!TryComputeAutoOutlineTolerance(seedX, seedY, seedValue, homogenizedSeedValue, sensitivityLevel, out double localMean, out double tolerance))
        {
            return false;
        }

        bool[,] included = new bool[width, height];
        bool[,] visited = new bool[width, height];
        Queue<(int X, int Y)> queue = new();
        queue.Enqueue((localSeedX, localSeedY));
        visited[localSeedX, localSeedY] = true;

        int count = 0;
        while (queue.Count > 0)
        {
            (int x, int y) = queue.Dequeue();
            int imageX = x + left;
            int imageY = y + top;
            double homogenizedValue = homogenizedValues[x, y];
            if (!TryGetPixelValue(imageX, imageY, out double rawValue))
            {
                rawValue = homogenizedValue;
            }

            if (!IsAutoOutlinePixelAccepted(homogenizedValue, rawValue, homogenizedSeedValue, seedValue, localMean, tolerance))
            {
                continue;
            }

            included[x, y] = true;
            count++;
            if (count > AutoOutlineMaxPixels)
            {
                return false;
            }

            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (offsetX == 0 && offsetY == 0)
                    {
                        continue;
                    }

                    int nextX = x + offsetX;
                    int nextY = y + offsetY;
                    if ((uint)nextX >= (uint)width || (uint)nextY >= (uint)height || visited[nextX, nextY])
                    {
                        continue;
                    }

                    visited[nextX, nextY] = true;
                    queue.Enqueue((nextX, nextY));
                }
            }
        }

        if (count < AutoOutlineMinPixels)
        {
            return false;
        }

        bool[,] cleanedMask = SmoothAutoOutlineMask(included, localSeedX, localSeedY, out count);
        if (count < AutoOutlineMinPixels)
        {
            return false;
        }

        mask = new AutoOutlineMask(left, top, cleanedMask, count);
        return true;
    }

    private bool TryComputeAutoOutlineTolerance(int seedX, int seedY, double seedValue, double homogenizedSeedValue, int sensitivityLevel, out double mean, out double tolerance)
    {
        List<double> values = [];
        for (int y = Math.Max(0, seedY - 3); y <= Math.Min(_imageHeight - 1, seedY + 3); y++)
        {
            for (int x = Math.Max(0, seedX - 3); x <= Math.Min(_imageWidth - 1, seedX + 3); x++)
            {
                if (TryGetHomogenizedPixelValue(x, y, out double value))
                {
                    values.Add(value);
                }
            }
        }

        if (values.Count == 0)
        {
            mean = 0;
            tolerance = 0;
            return false;
        }

        mean = values.Average();
        double averageValue = mean;
        double variance = values.Average(value => (value - averageValue) * (value - averageValue));
        double stdDev = Math.Sqrt(Math.Max(variance, 0));
        double min = values.Min();
        double max = values.Max();
        double interQuartileRange = ComputeInterQuartileRange(values);
        string modality = (_modality ?? string.Empty).Trim().ToUpperInvariant();
        double baselineTolerance = modality == "CT"
            ? 18.0
            : Math.Max(6.0, Math.Abs(homogenizedSeedValue != 0 ? homogenizedSeedValue : seedValue) * 0.075);
        tolerance = ApplyAutoOutlineSensitivity(Math.Max(baselineTolerance, Math.Max(stdDev * 2.1, Math.Max((max - min) * 0.48, interQuartileRange * 1.7))), sensitivityLevel);
        return true;
    }

    private static bool IsAutoOutlinePixelAccepted(double value, double rawValue, double seedValue, double rawSeedValue, double localMean, double tolerance)
    {
        double seedDelta = Math.Abs(value - seedValue);
        double meanDelta = Math.Abs(value - localMean);
        double rawSeedDelta = Math.Abs(rawValue - rawSeedValue);
        return seedDelta <= tolerance ||
            meanDelta <= tolerance * 0.72 ||
            (seedDelta <= tolerance * 1.12 && rawSeedDelta <= tolerance * 0.55);
    }

    private static Point[] TraceAutoOutlineBoundary(AutoOutlineMask mask, int maxPointCount)
    {
        List<Point> boundary = [];
        int width = mask.Pixels.GetLength(0);
        int height = mask.Pixels.GetLength(1);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!mask.Pixels[x, y] || !IsBoundaryPixel(mask.Pixels, x, y))
                {
                    continue;
                }

                boundary.Add(new Point(x + 0.5, y + 0.5));
            }
        }

        if (boundary.Count < 3)
        {
            return [];
        }

        Point centroid = new(boundary.Average(point => point.X), boundary.Average(point => point.Y));
        Point[] ordered = boundary
            .OrderBy(point => Math.Atan2(point.Y - centroid.Y, point.X - centroid.X))
            .ToArray();

        int targetCount = Math.Clamp(maxPointCount, 12, Math.Max(12, ordered.Length));
        if (ordered.Length <= targetCount)
        {
            return ordered;
        }

        Point[] simplified = new Point[targetCount];
        for (int index = 0; index < targetCount; index++)
        {
            int sourceIndex = (int)Math.Round(index * (ordered.Length / (double)targetCount), MidpointRounding.AwayFromZero) % ordered.Length;
            simplified[index] = ordered[sourceIndex];
        }

        return simplified;
    }

    private static bool IsBoundaryPixel(bool[,] mask, int x, int y)
    {
        int width = mask.GetLength(0);
        int height = mask.GetLength(1);
        for (int offsetY = -1; offsetY <= 1; offsetY++)
        {
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                if (offsetX == 0 && offsetY == 0)
                {
                    continue;
                }

                int nextX = x + offsetX;
                int nextY = y + offsetY;
                if ((uint)nextX >= (uint)width || (uint)nextY >= (uint)height || !mask[nextX, nextY])
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool TrySegmentVolumeSeed(Point imagePoint, int sensitivityLevel, out HashSet<int> region)
    {
        region = [];
        if (_volume is null || SpatialMetadata is null)
        {
            return false;
        }

        SpatialVector3D seedPatientPoint = SpatialMetadata.PatientPointFromPixel(imagePoint);
        (double voxelX, double voxelY, double voxelZ) = _volume.PatientToVoxel(seedPatientPoint);
        int seedX = Math.Clamp((int)Math.Round(voxelX), 0, _volume.SizeX - 1);
        int seedY = Math.Clamp((int)Math.Round(voxelY), 0, _volume.SizeY - 1);
        int seedZ = Math.Clamp((int)Math.Round(voxelZ), 0, _volume.SizeZ - 1);
        short seedValue = _volume.GetVoxel(seedX, seedY, seedZ);
        double homogenizedSeedValue = GetHomogenizedVoxelValue(seedX, seedY, seedZ);

        if (!TryComputeVolumeTolerance(seedX, seedY, seedZ, seedValue, homogenizedSeedValue, sensitivityLevel, out double localMean, out double tolerance))
        {
            return false;
        }

        int maxRadiusX = Math.Clamp((int)Math.Round(80.0 / Math.Max(_volume.SpacingX, 0.1)), 12, _volume.SizeX);
        int maxRadiusY = Math.Clamp((int)Math.Round(80.0 / Math.Max(_volume.SpacingY, 0.1)), 12, _volume.SizeY);
        int maxRadiusZ = Math.Clamp((int)Math.Round(80.0 / Math.Max(_volume.SpacingZ, 0.1)), 8, _volume.SizeZ);

        HashSet<int> visited = [];
        Queue<(int X, int Y, int Z)> queue = new();
        queue.Enqueue((seedX, seedY, seedZ));
        visited.Add(GetVoxelKey(seedX, seedY, seedZ, _volume.SizeX, _volume.SizeY));

        while (queue.Count > 0)
        {
            (int x, int y, int z) = queue.Dequeue();
            short value = _volume.GetVoxel(x, y, z);
            double homogenizedValue = GetHomogenizedVoxelValue(x, y, z);
            if (!IsAutoOutlinePixelAccepted(homogenizedValue, value, homogenizedSeedValue, seedValue, localMean, tolerance))
            {
                continue;
            }

            region.Add(GetVoxelKey(x, y, z, _volume.SizeX, _volume.SizeY));
            if (region.Count > AutoOutlineMaxVoxelCount)
            {
                region.Clear();
                return false;
            }

            Span<(int X, int Y, int Z)> neighbors =
            [
                (x - 1, y, z), (x + 1, y, z),
                (x, y - 1, z), (x, y + 1, z),
                (x, y, z - 1), (x, y, z + 1),
            ];

            foreach ((int nextX, int nextY, int nextZ) in neighbors)
            {
                if ((uint)nextX >= (uint)_volume.SizeX || (uint)nextY >= (uint)_volume.SizeY || (uint)nextZ >= (uint)_volume.SizeZ)
                {
                    continue;
                }

                if (Math.Abs(nextX - seedX) > maxRadiusX || Math.Abs(nextY - seedY) > maxRadiusY || Math.Abs(nextZ - seedZ) > maxRadiusZ)
                {
                    continue;
                }

                int key = GetVoxelKey(nextX, nextY, nextZ, _volume.SizeX, _volume.SizeY);
                if (!visited.Add(key))
                {
                    continue;
                }

                queue.Enqueue((nextX, nextY, nextZ));
            }
        }

        return region.Count >= AutoOutlineMinPixels * 2;
    }

    private bool TryComputeVolumeTolerance(int seedX, int seedY, int seedZ, short seedValue, double homogenizedSeedValue, int sensitivityLevel, out double mean, out double tolerance)
    {
        mean = 0;
        tolerance = 0;
        if (_volume is null)
        {
            return false;
        }

        List<double> values = [];
        for (int z = Math.Max(0, seedZ - 1); z <= Math.Min(_volume.SizeZ - 1, seedZ + 1); z++)
        {
            for (int y = Math.Max(0, seedY - 2); y <= Math.Min(_volume.SizeY - 1, seedY + 2); y++)
            {
                for (int x = Math.Max(0, seedX - 2); x <= Math.Min(_volume.SizeX - 1, seedX + 2); x++)
                {
                    values.Add(GetHomogenizedVoxelValue(x, y, z));
                }
            }
        }

        if (values.Count == 0)
        {
            return false;
        }

        mean = values.Average();
        double averageValue = mean;
        double variance = values.Average(value => (value - averageValue) * (value - averageValue));
        double stdDev = Math.Sqrt(Math.Max(variance, 0));
        double range = values.Max() - values.Min();
        double interQuartileRange = ComputeInterQuartileRange(values);
        string modality = (_modality ?? string.Empty).Trim().ToUpperInvariant();
        double baselineTolerance = modality == "CT" ? 22.0 : Math.Max(8.0, Math.Abs(homogenizedSeedValue != 0 ? homogenizedSeedValue : seedValue) * 0.075);
        tolerance = ApplyAutoOutlineSensitivity(Math.Max(baselineTolerance, Math.Max(stdDev * 2.2, Math.Max(range * 0.54, interQuartileRange * 1.85))), sensitivityLevel);
        return true;
    }

    private static double ApplyAutoOutlineSensitivity(double tolerance, int sensitivityLevel)
    {
        double scaled = tolerance * Math.Pow(1.14, sensitivityLevel);
        return Math.Max(1.0, scaled);
    }

    private bool TryGetHomogenizedPixelValue(int x, int y, out double value)
    {
        value = 0;
        if (_imageWidth <= 0 || _imageHeight <= 0 || (uint)x >= (uint)_imageWidth || (uint)y >= (uint)_imageHeight)
        {
            return false;
        }

        int radius = s_autoOutlineKernel.Length / 2;
        double weightedSum = 0;
        double weightTotal = 0;
        for (int offsetY = -radius; offsetY <= radius; offsetY++)
        {
            int sampleY = Math.Clamp(y + offsetY, 0, _imageHeight - 1);
            int weightY = s_autoOutlineKernel[offsetY + radius];
            for (int offsetX = -radius; offsetX <= radius; offsetX++)
            {
                int sampleX = Math.Clamp(x + offsetX, 0, _imageWidth - 1);
                if (!TryGetPixelValue(sampleX, sampleY, out double sample))
                {
                    continue;
                }

                int weight = weightY * s_autoOutlineKernel[offsetX + radius];
                weightedSum += sample * weight;
                weightTotal += weight;
            }
        }

        if (weightTotal <= 0)
        {
            return TryGetPixelValue(x, y, out value);
        }

        value = weightedSum / weightTotal;
        return true;
    }

    private double[,] BuildHomogenizedPixelWindow(int left, int top, int width, int height)
    {
        double[,] values = new double[width, height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                TryGetHomogenizedPixelValue(left + x, top + y, out values[x, y]);
            }
        }

        return values;
    }

    private bool[,] SmoothAutoOutlineMask(bool[,] included, int seedX, int seedY, out int pixelCount)
    {
        int width = included.GetLength(0);
        int height = included.GetLength(1);
        bool[,] smoothed = new bool[width, height];
        pixelCount = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int neighbors = 0;
                int active = 0;
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        int nextX = x + offsetX;
                        int nextY = y + offsetY;
                        if ((uint)nextX >= (uint)width || (uint)nextY >= (uint)height)
                        {
                            continue;
                        }

                        neighbors++;
                        if (included[nextX, nextY])
                        {
                            active++;
                        }
                    }
                }

                smoothed[x, y] = included[x, y]
                    ? active >= Math.Min(4, neighbors)
                    : active >= Math.Min(6, neighbors);
            }
        }

        if ((uint)seedX < (uint)width && (uint)seedY < (uint)height)
        {
            smoothed[seedX, seedY] = true;
        }

        bool[,] connected = RetainSeedConnectedMask(smoothed, seedX, seedY, out pixelCount);
        return connected;
    }

    private static bool[,] RetainSeedConnectedMask(bool[,] mask, int seedX, int seedY, out int pixelCount)
    {
        int width = mask.GetLength(0);
        int height = mask.GetLength(1);
        bool[,] connected = new bool[width, height];
        pixelCount = 0;
        if ((uint)seedX >= (uint)width || (uint)seedY >= (uint)height || !mask[seedX, seedY])
        {
            return connected;
        }

        bool[,] visited = new bool[width, height];
        Queue<(int X, int Y)> queue = new();
        queue.Enqueue((seedX, seedY));
        visited[seedX, seedY] = true;

        while (queue.Count > 0)
        {
            (int x, int y) = queue.Dequeue();
            if (!mask[x, y])
            {
                continue;
            }

            connected[x, y] = true;
            pixelCount++;
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (offsetX == 0 && offsetY == 0)
                    {
                        continue;
                    }

                    int nextX = x + offsetX;
                    int nextY = y + offsetY;
                    if ((uint)nextX >= (uint)width || (uint)nextY >= (uint)height || visited[nextX, nextY])
                    {
                        continue;
                    }

                    visited[nextX, nextY] = true;
                    queue.Enqueue((nextX, nextY));
                }
            }
        }

        return connected;
    }

    private double GetHomogenizedVoxelValue(int x, int y, int z)
    {
        if (_volume is null)
        {
            return 0;
        }

        double weightedSum = 0;
        double weightTotal = 0;
        for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
        {
            int sampleZ = Math.Clamp(z + offsetZ, 0, _volume.SizeZ - 1);
            int weightZ = offsetZ == 0 ? 2 : 1;
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                int sampleY = Math.Clamp(y + offsetY, 0, _volume.SizeY - 1);
                int weightY = offsetY == 0 ? 2 : 1;
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    int sampleX = Math.Clamp(x + offsetX, 0, _volume.SizeX - 1);
                    int weightX = offsetX == 0 ? 2 : 1;
                    int weight = weightX * weightY * weightZ;
                    weightedSum += _volume.GetVoxel(sampleX, sampleY, sampleZ) * weight;
                    weightTotal += weight;
                }
            }
        }

        return weightTotal <= 0 ? 0 : weightedSum / weightTotal;
    }

    private static double ComputeInterQuartileRange(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        double[] ordered = [.. values.OrderBy(value => value)];
        double q1 = InterpolatePercentile(ordered, 0.25);
        double q3 = InterpolatePercentile(ordered, 0.75);
        return Math.Max(0, q3 - q1);
    }

    private static double InterpolatePercentile(IReadOnlyList<double> orderedValues, double percentile)
    {
        if (orderedValues.Count == 0)
        {
            return 0;
        }

        double clamped = Math.Clamp(percentile, 0, 1);
        double position = clamped * (orderedValues.Count - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return orderedValues[lower];
        }

        double t = position - lower;
        return orderedValues[lower] + ((orderedValues[upper] - orderedValues[lower]) * t);
    }

    private VolumeRoiContour[] BuildAutoOutlinedVolumeContours(HashSet<int> region)
    {
        if (_volume is null || SpatialMetadata is null)
        {
            return [];
        }

        int sliceCount = VolumeReslicer.GetSliceCount(_volume, _volumeOrientation);
        List<VolumeRoiContour> contours = [];
        for (int sliceIndex = 0; sliceIndex < sliceCount; sliceIndex++)
        {
            if (!TryBuildSliceMaskFromRegion(region, sliceIndex, out bool[,] sliceMask))
            {
                continue;
            }

            Point[] imagePoints = TraceAutoOutlineBoundary(new AutoOutlineMask(0, 0, sliceMask, 0), maxPointCount: 56);
            if (imagePoints.Length < 3)
            {
                continue;
            }

            DicomSpatialMetadata metadata = VolumeReslicer.GetSliceSpatialMetadata(_volume, _volumeOrientation, sliceIndex);
            MeasurementAnchor[] anchors = imagePoints
                .Select(point => new MeasurementAnchor(point, metadata.PatientPointFromPixel(point)))
                .ToArray();
            contours.Add(new VolumeRoiContour(
                anchors,
                metadata.FilePath,
                metadata.SopInstanceUid,
                metadata.Origin,
                metadata.RowDirection,
                metadata.ColumnDirection,
                metadata.Normal,
                metadata.Origin.Dot(metadata.Normal),
                true,
                metadata.RowSpacing,
                metadata.ColumnSpacing));
        }

        return contours.ToArray();
    }

    private bool TryBuildSliceMaskFromRegion(HashSet<int> region, int sliceIndex, out bool[,] mask)
    {
        mask = new bool[1, 1];
        if (_volume is null)
        {
            return false;
        }

        switch (_volumeOrientation)
        {
            case SliceOrientation.Axial:
                mask = new bool[_volume.SizeX, _volume.SizeY];
                for (int y = 0; y < _volume.SizeY; y++)
                {
                    for (int x = 0; x < _volume.SizeX; x++)
                    {
                        mask[x, y] = region.Contains(GetVoxelKey(x, y, sliceIndex, _volume.SizeX, _volume.SizeY));
                    }
                }
                break;
            case SliceOrientation.Coronal:
            {
                int width = _volume.SizeX;
                double targetSpacingY = _volume.SpacingY > 0 ? _volume.SpacingY : 1.0;
                int height = GetResampledDepth(_volume.SizeZ, _volume.SpacingZ, targetSpacingY);
                mask = new bool[width, height];
                for (int row = 0; row < height; row++)
                {
                    int z = Math.Clamp((int)Math.Round(MapOutputRowToSourceZ(row, height, _volume.SizeZ)), 0, _volume.SizeZ - 1);
                    for (int x = 0; x < width; x++)
                    {
                        mask[x, row] = region.Contains(GetVoxelKey(x, sliceIndex, z, _volume.SizeX, _volume.SizeY));
                    }
                }
                break;
            }
            case SliceOrientation.Sagittal:
            {
                int width = _volume.SizeY;
                double targetSpacingY = _volume.SpacingY > 0 ? _volume.SpacingY : 1.0;
                int height = GetResampledDepth(_volume.SizeZ, _volume.SpacingZ, targetSpacingY);
                mask = new bool[width, height];
                for (int row = 0; row < height; row++)
                {
                    int z = Math.Clamp((int)Math.Round(MapOutputRowToSourceZ(row, height, _volume.SizeZ)), 0, _volume.SizeZ - 1);
                    for (int y = 0; y < width; y++)
                    {
                        mask[y, row] = region.Contains(GetVoxelKey(sliceIndex, y, z, _volume.SizeX, _volume.SizeY));
                    }
                }
                break;
            }
            default:
                return false;
        }

        int setCount = 0;
        foreach (bool included in mask)
        {
            if (included)
            {
                setCount++;
            }
        }

        return setCount >= AutoOutlineMinPixels;
    }

    private static int GetResampledDepth(int sourceDepth, double sourceSpacing, double targetSpacing)
    {
        if (sourceDepth <= 1 || sourceSpacing <= 0 || targetSpacing <= 0)
        {
            return sourceDepth;
        }

        double physicalDepth = (sourceDepth - 1) * sourceSpacing;
        return Math.Max(1, (int)Math.Round(physicalDepth / targetSpacing) + 1);
    }

    private static double MapOutputRowToSourceZ(int row, int outputHeight, int sourceDepth)
    {
        if (outputHeight <= 1 || sourceDepth <= 1)
        {
            return 0;
        }

        return (outputHeight - 1 - row) * (sourceDepth - 1) / (double)(outputHeight - 1);
    }

    private static int GetVoxelKey(int x, int y, int z, int sizeX, int sizeY) => (z * sizeY * sizeX) + (y * sizeX) + x;

    private readonly record struct AutoOutlineMask(int Left, int Top, bool[,] Pixels, int Count);
}